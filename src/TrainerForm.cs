using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GK2Trainer
{
    /// <summary>
    /// 守墓人2 (Graveyard Keeper 2) 内存修改器
    /// 定位原理：运行时动态解析 Mono 元数据 + 结构自识别，无硬编码地址。
    /// </summary>
    public class TrainerForm : Form
    {
        private ProcessMemory _mem = new ProcessMemory();
        private GameResLocator _loc;
        private Dictionary<string, GameResAtom> _res = new Dictionary<string, GameResAtom>();
        private List<GameResAtom> _playerAtoms = new List<GameResAtom>();
        private int _pid;

        private Timer _timer;
        private Label _lblStatus;
        private Button _btnRescan;
        private Button _btnApplyOnce;
        private TextBox _txtLog;
        /// <summary>【t37】客户区最底端的 Bilibili 感谢链接。</summary>
        private LinkLabel _lnkThanks;
        /// <summary>日志栏保留的最近条数（多行滚动区，超出后丢最早一条）。</summary>
        private const int LOG_MAX_LINES = 30;
        private readonly List<string> _logLines = new List<string>();
        private ProgressBar _progress;

        // 物品数量（Item 层：玩家背包内实际存在的物品）
        private ComboBox _cmbItemId;
        /// <summary>【t27】覆盖在 ComboBox 文字区上方的自绘面板：ComboBox 在「已收起但持有焦点」时
        /// 会用 SystemColors.Highlight（#0078D7）填满整个客户区，原生属性压不住 ⇒ 用自绘覆盖。</summary>
        private Panel _pnlDropFace;
        /// <summary>【t25】覆盖在 ComboBox 右端的自绘下拉按钮（系统按钮区在深色主题下是亮块）。</summary>
        private Panel _pnlDropArrow;
        private bool _dropFaceHot = false;
        private bool _dropFaceActive = false;
        private Label _lblItemCurrent;
        private TextBox _txtItemLock;
        private Panel _pnlItemLock;
        private Button _btnItemApply;
        private Label _lblItemCurCap;
        private Label _lblItemLockCap;

        // ------------------------------------------------------------------
        // 【2026-09-26 · 方案C 前置】「物品种类修改」三级联动（**本轮只读**）
        //   框1 = 既有 _cmbItemId（背包中的物品）→ 框2 = 目标种类（全库 814 条 id 聚合）
        //   → 框3 = 星级 / 属性（所选种类下的全部 id 变体）。
        //   ⚠ 本区**不含任何写游戏内存的代码**，也没有「更换 / 应用」按钮：
        //     写入路径另行设计（本轮只把「当前选择最终对应的 id」显示出来供核对）。
        // ------------------------------------------------------------------
        /// <summary>框2「目标种类」：分组键 = 中文名 + ItemType 数值（814 条聚合出的种类）。</summary>
        private ComboBox _cmbKind;
        /// <summary>框3「星级 / 属性」：所选种类下全部 id 的变体（单 id 种类时置灰）。</summary>
        private ComboBox _cmbVariant;
        private Label _lblKindCap;
        private Label _lblVariantCap;
        // 【2026-09-26 收尾轮 · 删除】原 `_lblKindPick`（结果回显：当前选择最终对应的**物品 id**，
        //   形如 cook_vegetable_salad:3）已整体移除 —— 实机 UI 抓取证实它把**内部英文 id**
        //   （示例：`stick`）直接显示在玩家面前，属"非面向玩家的信息"；而它想表达的"最终会换成什么"
        //   已由框2（中文名）+ 框3（星级）**完整表达**，保留只会是冗余 + 天书。
        //   腾出的 121px 宽度转给框3（见 AlignKindRow），星级/红白文本更不容易被截断。
        /// <summary>
        /// 【规格更新 2026-09-26】种类行**自己的**「应用」按钮：只写 `Item + 0x10`（id 指针，8 字节）。
        /// 与数量行的 `_btnItemApply`（只写 `Item + 0x40`，4 字节）**并列且完全独立**：
        /// 两者各有独立的守卫、判定与提示，不共用任何可变状态（成功/失败标志、输入清空、选中重置）。
        /// </summary>
        private Button _btnKindApply;
        /// <summary>
        /// 【规格更新 2026-09-26】「应用后自动按 Tab 打开背包」开关（默认勾选）。
        /// 勾选 ⇒ 种类行写入成功后切前台并连发两次 Tab（见 <see cref="BagRefreshWorker"/>）；
        /// 未勾选 ⇒ 只写内存，日志提示玩家手动重开背包。
        /// </summary>
        private CheckBox _chkAutoRefresh;
        /// <summary>
        /// 【置灰可读性】覆盖在 <see cref="_cmbVariant"/> 之上的自绘「面纱」：仅在框3 置灰时可见，
        /// 用 <see cref="STEAM_TEXT_OFF"/> 在 <see cref="STEAM_INPUT_OFF"/> 上绘制禁用原因
        /// （6.67:1），绕开「ComboBox 禁用态由系统绘制、ForeColor 不生效」这一深色主题硬坑。
        /// 框3 本身仍走原生 <c>Enabled=false</c>（键盘 / 鼠标都展不开，语义为真正的不可用）。
        /// </summary>
        private Panel _pnlVarVeil;
        /// <summary>框2 的同款面纱（预热完成前显示「数据未就绪」，避免系统禁用态把框体画成浅灰）。</summary>
        private Panel _pnlKindVeil;
        /// <summary>框2 / 框3 的自绘下拉箭头覆盖层（系统按钮区在深色主题下是纯白亮块）。</summary>
        private Panel _pnlKindArrow;
        private Panel _pnlVarArrow;
        /// <summary>【收尾 · 样式统一】框2 / 框3 的自绘文字区覆盖层（与框1 的 `_pnlDropFace` 同规格）。</summary>
        private SteamFacePanel _pnlKindFace;
        private SteamFacePanel _pnlVarFace;
        /// <summary>框2 的全部条目（显示顺序 = 此列表顺序，与 <see cref="_cmbKind"/> 的 Items 一一对应）。</summary>
        private readonly List<KindEntry> _kinds = new List<KindEntry>();
        private bool _suppressKind = false;
        // 【2026-09-26 收尾轮 · 删除】原 `_suppressVar`（抑制框3 重建期间的回显回调）随
        //   `OnVariantSelectionChanged` / `_lblKindPick` 一并移除：框3 已无 SelectedIndexChanged 订阅者。
        /// <summary>种类数据版本（类型表/星级表/名表条数）。仅版本变化时才重建框2，避免打断玩家当前选择。</summary>
        private string _kindDataVersion = "";
        /// <summary>「星级数据未就绪」提示只输出一次，避免反复重建时刷屏。</summary>
        private bool _kindStarvedLogged = false;
        // 【方案A 取证 2026-09-26】框2 覆盖统计：只在数字变化时记一条，避免刷新刷屏
        private int _kindCoverageIds = -1;
        private int _kindCoverageNoZh = -1;
        private int _kindCoverageKinds = -1;
        private int _kindCoverageAlias = -1;
        private int _kindCoverageStatAdded = -2;
        // 【方案A 取证】预热阶段算出的对照数字（供框2 汇总日志一条输出）
        private int _statAliasAdded = -1;
        private int _statNameBase = -1;
        private int _statNameWithAlias = -1;
        private string _statAliasText = "未建立";
        private readonly Dictionary<string, ItemEntry> _itemNames = new Dictionary<string, ItemEntry>();
        /// <summary>
        /// 物品 id → [redSkulls, whiteSkulls]，用于在物品下拉里显示红白骷髅标注，
        /// 使同名变体可区分（如 `bones_0_0:1` 红0白0 与 `bones_0_1:1` 红0白1 都叫「骨骼（铜星）」）。
        /// 【2026-09-26】与 `_itemNames` 共用 `_nameGate` 同步门；数据来源为
        /// `GameResLocator.ItemDefAttrs`（在同一遍堆扫描里顺带读取，字段偏移实测 814/814 = 100% 吻合），
        /// 并随**独立属性缓存**落盘（`NameCacheStore.TryWriteAttrs` / `TryReadAttrs`），
        /// 以免「缓存命中」这条常见路径上属性缺失。
        /// </summary>
        private readonly Dictionary<string, int[]> _itemAttrs = new Dictionary<string, int[]>();

        /// <summary>
        /// 物品 id → [quality, qualityType]，用于「物品种类修改」（方案C）的星级菜单判定。
        /// 【2026-09-26】`qualityType == 0` ⇒ 该物品不分星级（645 条）⇒ 星级菜单必须置灰；
        /// `== 1` 且该物品名只有一个星级变体（4 个：褐蘑菇、埋葬许可证 I/II/III）⇒ 同样置灰。
        /// 与 `_itemAttrs` 同源同门（`_nameGate`）；纯数据、与地址无关。
        /// </summary>
        private readonly Dictionary<string, int[]> _itemQuality = new Dictionary<string, int[]>();

        /// <summary>
        /// 物品 id → `ItemType` 枚举**数值**（偏移 +0xCC，实测 814/814）。
        /// 【2026-09-26 · 方案C】分组键 = **中文名 + ItemType**（用户确认）。
        /// ⚠ 存的是数值不是名称；显示用名称由 <see cref="ItemTypeName"/> 映射（数值取自反编译源码
        /// `public enum ItemType`，非推测）。
        /// </summary>
        private readonly Dictionary<string, int> _itemType = new Dictionary<string, int>();

        /// <summary>
        /// 物品 id → ItemDef 实例地址 / id 的 MonoString 指针。**仅供方案C 写入路径使用**
        /// （复用游戏堆中**已存在**的字符串，避免自行分配 MonoString）。
        /// ⚠ 地址只存活于本进程内存：**绝不落盘**（红线③），也绝不参与缓存文件读写。
        /// </summary>
        private readonly Dictionary<string, long> _itemDefAddr = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _itemDefIdPtr = new Dictionary<string, long>();
        private List<GameResLocator.ItemRef> _invItems = new List<GameResLocator.ItemRef>();
        private long _selItemAddr = 0;
        private string _selItemId = "";
        private bool _suppressSel = false;
        private bool _scanBusy = false;

        // 物品名表（itemId → 中文名，恒定来自游戏运行时内存提取）
        //   _namesReady       整表已就绪（启动预热装表成功，或刷新时装载会话缓存）
        //   _warmupRunning    启动预热线程正在扫描
        //   _nameGate         保护 _itemNames —— UI 线程与预热/刷新后台线程会并发访问
        private readonly object _nameGate = new object();
        private volatile bool _namesReady = false;
        private volatile bool _warmupRunning = false;

        /// <summary>
        /// 【t30 · R6④】取证 / 对照代码开关，**默认关闭**。
        ///   开启方式：设置环境变量 <c>GK2TRAINER_EVIDENCE=1</c> 后启动。
        ///   开启后才会执行：①「同一函数两遍调用」的对照实验（`IntersectNames(...,loc)` vs
        ///   `IntersectNames(...,null)`）；②【方案A 实测】长汇总日志；③ 别名链新增示例日志。
        ///   这些只服务于交付取证，不是产品功能；关闭时零开销（不拼字符串、不做第二遍调用）。
        /// </summary>
        private static readonly bool EvidenceMode =
            (System.Environment.GetEnvironmentVariable("GK2TRAINER_EVIDENCE") == "1");

        /// <summary>
        /// 【t30 · R1①】UI 侧词表 / 别名表的后台补建是否进行中（防重入）。
        ///   缓存命中路径原先不建立 UI 侧 `_loc` 的词表与别名表，导致 `LookupNameKeyWithAlias`
        ///   的别名分支在主路径上不可用（评审 t29 finding R1）。
        /// </summary>
        private volatile bool _locBackfillRunning = false;

        /// <summary>
        /// 【t34 · T1/T2】本次会话的名表是否**来自缓存**（`TryLoadCacheIntoTable` 成功装载）。
        ///   只有这条路径才允许后台补建 UI 侧词表：缓存命中路径下 `_loc` 缺词表会导致
        ///   `VerifyAliasAgainstLocTable` 永远跑不成（T2 死结）；而冷启动 / 预热扫描路径的
        ///   UI 侧 `_loc` 词表并非必需（名表已含全量条目），补建只会白跑一次全堆扫描。
        /// </summary>
        private volatile bool _namesFromCache = false;
        /// <summary>【t29 返工 · R1④】「别名表 vs 词表」补验是否已**真正跑过一次**。
        /// 用它而不是「别名表已就绪」作守卫 —— 后者会让自检在主路径上永不运行。</summary>
        private volatile bool _aliasSelfTestDone = false;
        private string _gameFingerprint = null;
        private bool _likesTipShown = false;
        // 物品型功能（科学/science）的背包枚举缓存:数量 >0 时游戏才创建 Item,需要定期补扫
        private List<GameResLocator.ItemRef> _bagItemCache = null;
        private int _bagRescanCounter = 0;
        // 科学分解产物存放在研究台(survey_wgo)等 WGO 容器,不在玩家主背包——
        // 该地址由「刷新」时后台结构链定位（FindItemViaWorld）解析,主背包枚举覆盖不到它
        // 【P0-③】跨线程读写 → 一律经 Volatile / Interlocked 访问
        private long _sciItemAddr = 0;
        private bool _sciTipShown = false;
        // 【换档追踪】读档后旧对象不释放（§2.4）,缓存的科学地址会指向旧档研究台——
        // 每 ~8 轮（2 s）用结构链重定位一次:链自带「当前存档」语义,读档后旧地址不可达即自动回落
        private int _sciRecheckCounter = 0;
        /// <summary>【P0-③】科学重定位后台线程的去重标志（0=空闲，1=运行中）。</summary>
        private int _sciRecheckRunning = 0;
        /// <summary>
        /// 【P0-③】科学重定位间隔（Tick 轮数，1 轮 = 250 ms）。
        /// 命中 → 8 轮（2 s，保证读档后 2 s 内自动追踪）；未命中 → 指数退避到 240 轮（60 s），
        /// 避免「当前档没有科学」时每 2 s 扫一遍全部 974 个 WgoData（实测 272 ms/次）。
        /// </summary>
        private volatile int _sciRecheckInterval = 8;

        private readonly List<Feature> _features = new List<Feature>();

        // ------------------------------------------------------------------
        // Steam 主题色板（色值实取自 Steam 客户端 steamui\css）
        //   面板渐变原文：linear-gradient(180deg, #2A475E 0%, #1B2838 80%)
        //   括号内为该色在「本渐变对应位置」上的 WCAG 2.1 对比度实测值
        // ------------------------------------------------------------------
        private static readonly Color STEAM_GRAD_TOP = Color.FromArgb(0x2A, 0x47, 0x5E);    // #2A475E 渐变顶
        private static readonly Color STEAM_GRAD_BOTTOM = Color.FromArgb(0x1B, 0x28, 0x38); // #1B2838 渐变底
        private static readonly Color STEAM_PANEL = Color.FromArgb(0x23, 0x26, 0x2E);       // #23262E 面板/输入框底
        private static readonly Color STEAM_BORDER = Color.FromArgb(0x3D, 0x44, 0x50);      // #3D4450 边框
        private static readonly Color STEAM_TEXT = Color.FromArgb(0xC6, 0xD4, 0xDF);        // #C6D4DF 主文本（6.4~9.9:1）
        private static readonly Color STEAM_TEXT_DIM = Color.FromArgb(0x8B, 0x92, 0x9A);    // #8B929A 次级文本（仅非文本用途）
        private static readonly Color STEAM_ACCENT = Color.FromArgb(0x1A, 0x9F, 0xFF);      // #1A9FFF 强调蓝（勾选态）
        // 警示橙：Steam 色板为 #F37D07，但它在渐变上部只有 3.6~4.0:1（不达标）；
        // 同色相提亮为 #FFA033 后，渐变顶 4.68:1、渐变底 6.6:1，满足「玩家可见文本 ≥ 4.5:1」。
        private static readonly Color STEAM_WARN = Color.FromArgb(0xFF, 0xA0, 0x33);
        // 【2026-09-26 · 方案C】禁用态专用色（**绝不用系统禁用色**）：
        //   WinForms 的系统控件在 Enabled=false 时由系统绘制禁用文字，ForeColor 不生效，
        //   深色面板上几乎不可见（本项目既有结论，见 t24 SteamCheckBox 注释）。
        //   故禁用态改为「原生禁用 + 自绘面纱」：底色 #1A1C22 比面板底 #23262E 再暗一档，
        //   文字 #9AA3AE 对 #1A1C22 的 WCAG 2.1 对比度 = 6.67:1（正文阈值 4.5:1）。
        private static readonly Color STEAM_INPUT_OFF = Color.FromArgb(0x1A, 0x1C, 0x22);   // 禁用底
        private static readonly Color STEAM_TEXT_OFF = Color.FromArgb(0x9A, 0xA3, 0xAE);    // 禁用文字（6.67:1）

        // 状态栏三种语义色（Steam 的状态文本用中性色，成功态不必高饱和）：
        //   就绪 = STEAM_TEXT（#C6D4DF 中性）· 等待/加载 = STEAM_WAIT（Khaki）· 错误 = STEAM_WARN（警示橙）
        private static readonly Color STEAM_WAIT = Color.Khaki;
        // 小面积强调用的紫红渐变：Steam 实测最高频 linear-gradient(85deg, #b178ad, #843978)（56 次）；
        // 竖条这类窄元素用其竖直等价形式（Steam 另有 180deg 版本 28 次）。
        private static readonly Color STEAM_ACCENT_TOP = Color.FromArgb(0xB1, 0x78, 0xAD);    // #b178ad
        private static readonly Color STEAM_ACCENT_BOTTOM = Color.FromArgb(0x84, 0x39, 0x78); // #843978

        /// <summary>程序性改写 CheckBox.Checked 时置位，避免触发「点击拦截」提示。</summary>
        private bool _suppressChk = false;

        // ------------------------------------------------------------------
        // 面板几何与画法 —— 与控件布局一一对应，改布局时必须同步
        //
        // 【依据·机制而非数值】深色主题里投影几乎不可见（Material/Flutter 官方原文：
        //   "Material drop shadows can be difficult to see in a dark theme"），所以层级
        //   不能靠阴影，只能靠三件事：①表面色差 ②1px 细边框 ③顶部 1px 内高光。
        //   Steam 正是这么做的：结构面板一律中性 #23262E（实测 977 处），彩色只留给
        //   「有身份语义、可配置」的元素 —— 那套蓝/紫红渐变属于键盘主题 KeyTheme_0..9，
        //   是「可选皮肤」（默认态其实是白色渐变），不是通用面板色。
        //   圆角取 Steam 最高频的 2px（2/3/4px 共 233 处，几乎不用大圆角）。
        // ------------------------------------------------------------------
        private static readonly Color STEAM_PANEL_HILIGHT = Color.FromArgb(26, 255, 255, 255); // inset 0 1px 0 rgba(255,255,255,.10)
        private static readonly Color STEAM_SEP = Color.FromArgb(38, 255, 255, 255);           // 面板内分隔线（15% 白）
        private static readonly Color STEAM_OK = Color.FromArgb(0x66, 0xC0, 0xF4);             // Steam 经典浅蓝（就绪/数值）

        /// <summary>功能面板：表头 + 9 行功能 + 操作按钮行。</summary>
        private static readonly Rectangle PANEL_FEATURES = new Rectangle(10, 68, 512, 328);
        /// <summary>下半区面板：物品数量修改区 + 新增「物品种类修改」行 + 日志区（同属「读取结果」，
        /// 共用一块面板，中间一条内部分隔线）。
        /// 【2026-09-26 · 方案C】高度 134 → 168（+34）：面板内新增一行三级联动控件
        /// （种类 / 星级 / 回显，见 <see cref="AlignKindRow"/>），日志与感谢链接同步下移 34px。</summary>
        private static readonly Rectangle PANEL_LOWER = new Rectangle(10, 402, 512, 168);

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// 画一个 Steam 风格面板（浮在渐变之上）：#23262E 实色 + 1px #3D4450 边框
        /// + 顶部 1px 内高光。三者合起来表达「这一层浮在背景之上」——
        /// 不依赖投影（深色下看不见），也不依赖大圆角（Steam 几乎不用）。
        /// </summary>
        private static void DrawPanel(Graphics g, Rectangle r)
        {
            System.Drawing.Drawing2D.SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Drawing2D.GraphicsPath path = RoundedRect(r, 2))
            {
                using (SolidBrush fill = new SolidBrush(STEAM_PANEL))
                    g.FillPath(fill, path);
                using (Pen hi = new Pen(STEAM_PANEL_HILIGHT))
                    g.DrawLine(hi, r.Left + 2, r.Top + 1, r.Right - 3, r.Top + 1);
                using (Pen border = new Pen(STEAM_BORDER))
                    g.DrawPath(border, path);
            }
            g.SmoothingMode = old;
        }

        /// <summary>
        /// 【t24】整窗背景 = Steam 主题蓝色竖直渐变，精确复刻 Steam 客户端定义的
        /// linear-gradient(180deg, #2A475E 0%, #1B2838 80%)：
        /// 0% → 80% 由 #2A475E 线性过渡到 #1B2838，80% → 100% 保持 #1B2838。
        /// 用 3 段 ColorBlend 表达（比两色默认插值更忠实原文，且 32 位插值无可见色带）。
        /// Label / CheckBox 的 BackColor 均为 Transparent，会请求父容器绘制本渐变 ⇒ 无深色补丁。
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle r = ClientRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            using (System.Drawing.Drawing2D.LinearGradientBrush br =
                new System.Drawing.Drawing2D.LinearGradientBrush(
                    r, STEAM_GRAD_TOP, STEAM_GRAD_BOTTOM,
                    System.Drawing.Drawing2D.LinearGradientMode.Vertical))
            {
                System.Drawing.Drawing2D.ColorBlend cb = new System.Drawing.Drawing2D.ColorBlend(3);
                cb.Positions = new float[] { 0f, 0.8f, 1f };
                cb.Colors = new Color[] { STEAM_GRAD_TOP, STEAM_GRAD_BOTTOM, STEAM_GRAD_BOTTOM };
                br.InterpolationColors = cb;
                e.Graphics.FillRectangle(br, r);
            }
            // ① 品牌竖条：Steam 实测紫红渐变的小面积应用（linear-gradient(85deg,#b178ad,#843978)，
        //    竖条用其竖直等价形式）。选它的理由：① 面积小（3×26px），不参与信息层级，
        //    不会像大块彩色那样与深色蓝调冲突或拉低文本对比度；② 它在 x=10..12，
        //    与两块面板的左边界同在 x=10 —— 顺带成为「对齐基准」的可见锚点。
        Rectangle brand = new Rectangle(PANEL_FEATURES.Left, 9, 3, 26);
        using (System.Drawing.Drawing2D.LinearGradientBrush bg =
            new System.Drawing.Drawing2D.LinearGradientBrush(brand, STEAM_ACCENT_TOP, STEAM_ACCENT_BOTTOM,
                System.Drawing.Drawing2D.LinearGradientMode.Vertical))
        using (System.Drawing.Drawing2D.GraphicsPath bpath = RoundedRect(brand, 2))
        {
            System.Drawing.Drawing2D.SmoothingMode old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillPath(bg, bpath);
            e.Graphics.SmoothingMode = old;
        }
            // ② 标题区底部分隔线 —— 与面板同宽（10..522），保证框架元素在同一条对齐网格上
            using (Pen sep = new Pen(STEAM_SEP))
                e.Graphics.DrawLine(sep, PANEL_FEATURES.Left, 36, PANEL_FEATURES.Right, 36);
            // ② 面板：功能区 / 下半区（物品 + 日志）各一块，浮在渐变之上
            DrawPanel(e.Graphics, PANEL_FEATURES);
            DrawPanel(e.Graphics, PANEL_LOWER);
            // ③ 下半区面板内部的分隔线（物品区 ↔ 日志区）：这里上下各有 4px 余量，不会切到控件。
            //    ⚠ 表头下 / 按钮行上**不再画线**：那两处上下只有 1~2px，线会与首尾锁定输入框"融合"
            //    （用户实机反馈），改由面板内留白 + 控件自身边框区分层级。
            // 【2026-09-26 · 方案C】454 → 488：新增的「种类 / 星级」行（459..485）属**输入区**，
            //    必须留在分隔线上方；该线始终保持「日志顶 - 4」这一结构关系（旧 454 = 458 - 4，
            //    新 488 = 492 - 4），位置迁移而非重新设计。
            using (Pen inner = new Pen(STEAM_SEP))
                e.Graphics.DrawLine(inner, PANEL_LOWER.Left + 6, 488, PANEL_LOWER.Right - 7, 488);
        }

        /// <summary>【P0-⑤】上次冷刷新标定出的原子 vtable（同一游戏进程内复用，省一次全堆自举）。</summary>
        private long _atomVTableHint = 0;
        /// <summary>【t3 C-4】最近一次异常的类型名与消息（内部诊断；不上屏，供排障读取）。</summary>
        private volatile string _lastErrorNote = "";
        /// <summary>【P2-1 性能】每个 Tick 复用的「本轮已写入的资源键」集合，避免每 250 ms 新建 List。</summary>
        private readonly List<string> _resLockedThisTick = new List<string>();
        /// <summary>【P0-④ 性能】缓存的游戏进程实例（存活探活用；仅 PID 变化时重建）。</summary>
        private System.Diagnostics.Process _gameProc = null;

        private class Feature
        {
            public string Key;
            /// <summary>资源查找键（= GameResAtom.type）。默认与 Key 相同；
            /// 仅在「同一资源挂多个界面项」时不同（当前版本已无此类项，字段保留以备扩展）。</summary>
            public string ResKey;
            /// <summary>物品型功能的物品 ID（如 science）。非空时本项走 Item 层（背包内该物品的 +0x40 count），
            /// 而不是 GameRes 资源层——science 是 ItemDef 不是 GameRes 资源，数量 >0 时才会在背包创建 Item。</summary>
            public string ItemId;
            public string Caption;
            public float DefaultLock;
            public CheckBox Chk;
            public TextBox Txt;
            public Label Lbl;
            public bool Missing;
            /// <summary>【t24】本条目当前是否可操作（就绪 且 资源/物品已读取到）。</summary>
            public bool Usable;
        }

        private static readonly string[][] FeatureDefs = new string[][]
        {
            new string[] { "money",     "无限金钱",     "999999" },
            new string[] { "energy",    "无限能量",     "100"    },
            new string[] { "insanity",  "疯狂清空",     "0"      },
            new string[] { "tech_green","绿色技能点",   "999"    },
            new string[] { "tech_blue", "蓝色技能点",   "999"    },
            new string[] { "tech_red",  "红色技能点",   "999"    },
            // 科学：不是 GameRes 资源（GameResAtom 无此 type），是 ItemDef——数量 >0 时以背包物品
            // （Item,数量在 +0x40）形式存在，与「信仰 faith」同机制（2026-09-23 实测）。
            // 第 5 列 ItemId 非空 ⇒ 本项走 Item 层；背包没有该物品时条目禁用并提示先获得 1 点。
            new string[] { "science",   "科学",         "100",    null,       "science" },
            // 【2026-09-25 用户复测更正】此前按用户初步测试把「幸福度全满」改名为「周点赞上限」
            //   并让它单独成行；后续用户复测确认：**happiness 改的就是点赞本身**，
            //   当时读到的"15"是与其他数据**巧合相等**，并不存在独立的「周点赞上限」概念。
            //   ⇒ 该行已删除，全程序只保留下面这一个读写 happiness 的功能项（点赞）。
            // 点赞（游戏 HUD 左上角 👍 10/10）：其 TMP 文本为 <sprite name="happiness">10/10。
            // 【2026-09-23 决定性实验证实可写】IL 链：HUD.UpdateHappinessInstant ← PlayerData.GetRes("happiness")
            //   → GameResSystemBase.Get → resValues[i].value —— 正是本修改器写入的 GameResAtom(+0x18)。
            //   实测：写入 3 后游戏内交易获得 +1，HUD 立即显示 4/10（底数即写入值）。
            // ⚠ 可见性：HUD 是常驻对象、平时不重绘（切语言/切分辨率/进出场景都不触发），
            //   仅在「点赞数值变动 / 教堂品质(TownSystem.Quality)变化 / 读档」时重读。
            //   写入本身即时生效（交易上限等游戏逻辑读的就是该值）。
            // 【2026-09-25 机制认知更正·用户实测】原文案写「卖 1 件任意商品（如 0.06👍 的甜菜根）
            //   即可强制 HUD 刷新显示」——**该表述错误**。实测：+0.06 与 +0.84 这类**小数增量
            //   都不会触发 HUD 重绘**，只有**获得 1 个整数点赞（+1👍）**时 HUD 才会更新显示。
            //   历史文档（HANDOFF §2.x 点赞相关条目）沿用了同一错误说法，需同步更正。
            // 第 4 列为资源查找键；本项是**唯一**读写 happiness atom 的界面项
            //（原「周点赞上限」行已于 2026-09-25 按用户复测结论删除 —— 两者本来就是同一个值）。
            new string[] { "likes",     "点赞",         "10",     "happiness" },
        };

        public TrainerForm()
        {
            try
            {
                System.IO.Stream iconStream = GetType().Assembly.GetManifestResourceStream("gk2.ico");
                if (iconStream != null) this.Icon = new System.Drawing.Icon(iconStream);
            }
            catch { }

            // 版本号：与 GitHub Release tag 一一对应（v1.1.0 = 2026-09-26：物品种类修改 + 别名表汉化 + 缓存/UI 修复）
            Text = "守墓人2 修改器  v1.1.0   [By:东皇钟]";

            // 【t24 修复 · 窗口被放大导致右下留白】
            // AutoScaleMode = Font 时，OnLoad 会按 AutoScaleFactor = 当前字体度量 / 设计基准
            // 缩放整个窗体。旧顺序把 Font 放在 AutoScaleMode **之后**：
            //   ① 设 AutoScaleMode 时，设计基准 AutoScaleDimensions 仍是系统默认字体的 (6,12)；
            //   ② 随后才把 Font 改成 Microsoft YaHei UI 9pt，运行时度量变成 (7,17)；
            //   ⇒ 缩放系数 = 7/6 ≈ 1.1667（宽）、17/12 ≈ 1.4167（高）
            //   ⇒ 客户区 532×576 被放大成 **621×816**（实测吻合），而控件按绝对坐标创建、
            //     不随窗体缩放 ⇒ 右侧与下侧出现大片留白。
            // 修法（保留自动缩放机制、只去掉隐式基准）：先定 Font，再把设计基准显式对齐到
            // 「当前字体下的真实度量」，使 AutoScaleFactor 恒为 1 —— 不硬编码 (7,17)、
            // 也不用 AutoScaleMode.None 关掉机制，换字体/换 DPI 时基准自动跟随。
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = CurrentAutoScaleDimensions;

            // 高度 576 = 原 530 + 46：日志栏由单行 22px 改为多行 68px（见 BuildUi 中 _txtLog），
            // 其余控件坐标一律未动，故新增的 46px 全部落在窗口底部、不挤动任何既有控件。
            // 【t37】高度 566 = 532 宽不变；高度随功能区删行收紧 34px（600 → 566），
            // 最底端仍保留一行「Bilibili 感谢链接」（y=538..558，距底边 8px）。
            // 【2026-09-26 · 方案C】客户区高度由 566 **再加 34** → 600：
            //   下半区面板高度 134 → 168（PANEL_LOWER），日志 _txtLog.Top 458 → 492（高 68 不变），
            //   感谢链接 _lnkThanks.Top 538 → 572（572..592，距底边仍 8px），
            //   面板内分隔线 454 → 488（该线始终 = 日志顶 - 4，结构关系未变），
            //   物品数量行坐标**未动**（其真实位置由 OnLoad 的 AlignItemRow 按 CY=437 重排）。
            ClientSize = new Size(532, 600);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = STEAM_GRAD_BOTTOM;          // 渐变由 OnPaintBackground 绘制，此处为兜底底色
            ForeColor = STEAM_TEXT;

            BuildUi();
            BuildFeatures();

            _timer = new Timer();
            _timer.Interval = 250;
            _timer.Tick += new EventHandler(OnTick);
            _timer.Start();

            Shown += new EventHandler(OnShown);
            FormClosing += new FormClosingEventHandler(OnClosing);
        }

        // ------------------------------------------------------------------
        // UI
        // ------------------------------------------------------------------

        /// <summary>新建标签（默认高度按字号自适应；9pt 结果为 20px，与历史布局完全一致）。</summary>
        private Label MakeLabel(string text, int x, int y, int w, Color c, float size, bool bold)
        {
            // 【T8-F4 处置】默认高度原为写死 20px（只够 9pt，字号一大就会裁字，缺防御）。
            // 改为按字号自适应：h ≈ ceil(字号 × 1.9) + 2
            //   9pt  → ceil(17.1)+2 = 20  ← 与旧值完全相同，**既有控件坐标零改动**
            //   13pt → ceil(24.7)+2 = 27（标题另有显式 28px 重载，不受影响）
            int h = (int)Math.Ceiling(size * 1.9) + 2;
            return MakeLabel(text, x, y, w, h, c, size, bold);
        }

        /// <summary>
        /// 新建标签（指定高度）。**字号大于 9pt 时必须显式给足 h**：
        /// Label 把自己的矩形当裁剪区，高度不足时文字上下都会被切掉。
        /// 实测（Microsoft YaHei UI）：9pt 需 17px，13pt 加粗需 25px —— 故标题固定给 28px。
        /// 另显式设 TextAlign = MiddleLeft，让文字在给定高度内垂直居中，不依赖控件默认值。
        /// </summary>
        private Label MakeLabel(string text, int x, int y, int w, int h, Color c, float size, bool bold)
        {
            Label l = new Label();
            l.Text = text;
            l.Left = x; l.Top = y; l.Width = w; l.Height = h;
            l.ForeColor = c;
            l.Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            l.BackColor = Color.Transparent;
            l.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(l);
            return l;
        }

        /// <summary>
        /// 【t24】Steam 主题复选框：WinForms 的 CheckBox 在 Flat/Standard 样式下都会用**系统色**
        /// 填充复选框方框 —— 在本程序自绘的渐变背景上会表现为白色或浅灰的实心方块（方块补丁），
        /// 而且禁用态由系统用黑字渲染、ForeColor 不生效（用户实机反馈的「几乎不可见」根因）。
        /// 这里改为完全自绘：方框内部用「本行渐变同色」的底色（视觉上等于透明，无补丁），
        /// 描边 #8B929A，勾选态用 Steam 强调蓝 #1A9FFF + 白色对勾 —— 颜色 100% 可控。
        /// 交互（Click / CheckedChanged / Checked）沿用 CheckBox 原生实现，未改任何语义。
        /// </summary>
        private class SteamCheckBox : CheckBox
        {
            private const int BOX = 13;     // 方框边长（与原系统方框同尺寸）
            private const int GAP = 6;      // 方框与文字的间距

            public SteamCheckBox()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                // ① 底色 = 本行所处位置的渐变同色（由创建方设置），与窗体渐变差异 < 2 个色阶
                using (SolidBrush bg = new SolidBrush(BackColor))
                    g.FillRectangle(bg, ClientRectangle);

                // ② 方框
                Rectangle r = new Rectangle(1, (Height - BOX) / 2, BOX, BOX);
                if (Checked)
                {
                    using (SolidBrush b = new SolidBrush(Enabled ? STEAM_ACCENT : STEAM_TEXT_DIM))
                        g.FillRectangle(b, r);
                    using (Pen p = new Pen(Color.White, 2f))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.DrawLines(p, new Point[] {
                            new Point(r.Left + 3, r.Top + 6),
                            new Point(r.Left + 5, r.Top + 9),
                            new Point(r.Left + 10, r.Top + 3) });
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
                    }
                }
                else
                {
                    using (Pen p = new Pen(Enabled ? STEAM_TEXT_DIM : STEAM_BORDER))
                        g.DrawRectangle(p, r.Left, r.Top, r.Width - 1, r.Height - 1);
                }

                // ③ 文字（ForeColor 由 RefreshFeatureAvailability 按状态设置：可用 #C6D4DF / 警示橙）
                Rectangle tr = new Rectangle(BOX + GAP, 0, Width - BOX - GAP, Height);
                Color fg = Enabled ? ForeColor : STEAM_BORDER;
                TextRenderer.DrawText(g, Text, Font, tr, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>
        /// 【t28】覆盖在 ComboBox 之上的自绘面板：把滚轮消息**转发**给被覆盖的 ComboBox。
        /// 背景：Windows 的「悬停时滚动非活动窗口」会把 WM_MOUSEWHEEL 派发给鼠标下的窗口
        /// （即本 Panel 而非 ComboBox）；不转发就丢掉 ComboBox 的原生滚轮语义
        /// （未展开时切换选中项 / 展开时在列表中滚动）—— 那是 t27 自绘覆盖引入的交互回归。
        /// lParam 原样透传：WM_MOUSEWHEEL 的 lParam 是屏幕坐标，ComboBox 按滚轮方向处理，不受影响。
        /// </summary>
        private class WheelForwardPanel : Panel
        {
            private const int WM_MOUSEWHEEL = 0x020A;
            private readonly Control _target;

            public WheelForwardPanel(Control target) { _target = target; }

            [System.Runtime.InteropServices.DllImport("user32.dll",
                CharSet = System.Runtime.InteropServices.CharSet.Auto)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_MOUSEWHEEL && _target != null && _target.IsHandleCreated)
                {
                    SendMessage(_target.Handle, m.Msg, m.WParam, m.LParam);
                    return;
                }
                base.WndProc(ref m);
            }
        }

        /// <summary>
        /// 【2026-09-26 · 方案C】框2 / 框3 的自绘下拉箭头覆盖层。
        /// 根因与 <see cref="_pnlDropArrow"/> 相同：ComboBox 的按钮区由系统主题绘制，
        /// 深色主题下是浅色亮块（本轮实机截图实测框2 箭头区为**纯白**），BackColor 压不住。
        /// 做法：同尺寸自绘 Panel 精确覆盖该区域 —— 底色/边框/箭头全可控；
        /// 继承 <see cref="WheelForwardPanel"/> ⇒ 滚轮消息原样转发给目标 ComboBox，不丢原生滚轮语义；
        /// 点击转发为 DroppedDown。**只改绘制，不改任何取值逻辑。**
        /// </summary>
        private class SteamArrowPanel : WheelForwardPanel
        {
            private readonly ComboBox _combo;
            private bool _hot = false;

            public SteamArrowPanel(ComboBox target)
                : base(target)
            {
                _combo = target;
                Width = 24;
                BackColor = STEAM_PANEL;
                Cursor = Cursors.Hand;
                HookComboState(target, new EventHandler(OnComboStateChanged));   // 【T25-1】焦点/展开 → 重绘
            }

            private void OnComboStateChanged(object sender, EventArgs e) { Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                bool act = _hot || (_combo != null && (_combo.Focused || _combo.DroppedDown));
                using (SolidBrush bg = new SolidBrush(act ? STEAM_GRAD_TOP : STEAM_PANEL))
                    g.FillRectangle(bg, ClientRectangle);
                using (Pen border = new Pen(act ? STEAM_TEXT_DIM : STEAM_BORDER))
                    g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
                int cx = Width / 2, cy = Height / 2;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Color.White))
                    g.FillPolygon(b, new Point[] {
                        new Point(cx - 7, cy - 4), new Point(cx + 7, cy - 4), new Point(cx, cy + 6) });
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                _hot = true; Invalidate(); base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                _hot = false; Invalidate(); base.OnMouseLeave(e);
            }

            protected override void OnClick(EventArgs e)
            {
                if (_combo != null && _combo.Enabled)
                {
                    _combo.Focus();
                    _combo.DroppedDown = true;
                }
                base.OnClick(e);
            }
        }

        /// <summary>
        /// 【T25-1 修复】给自绘覆盖层订阅目标 ComboBox 的**焦点 / 展开**四个事件 → 触发重绘。
        /// 语义与框1 的 `OnDropStateChanged` 完全等价（框1 也订阅这四个事件），
        /// 差别只在状态量：框1 用 `_dropFaceHot` / `_dropFaceActive`，新框各自用自己的 `_hot` 与
        /// 实时读取的 `Focused || DroppedDown`，**绝不共用框1 的状态量**。
        /// 不做这一步的直接后果（t25 实测）：聚焦或展开时 `OnPaint` 根本不会被触发，
        /// 画面停留在常态色 —— 三框聚焦态取色不一致。
        /// </summary>
        private static void HookComboState(ComboBox target, EventHandler h)
        {
            if (target == null || h == null) return;
            target.GotFocus += h;
            target.LostFocus += h;
            target.DropDown += h;
            target.DropDownClosed += h;
        }

        /// <summary>
        /// 【收尾 · 样式统一 2026-09-26】框2 / 框3 的自绘「文字区」覆盖层 —— 与框1 的
        /// <see cref="_pnlDropFace"/> **同规格、同四态语言**。
        /// 根因（框1 的 t27 已查明）：`DropDownList` 样式的 ComboBox 在「下拉已收起、控件仍持有焦点」时，
        /// WinForms 会用 `SystemColors.Highlight`(#0078D7) 填满整个客户区，`BackColor`/`ForeColor`/`FlatStyle`
        /// 全都压不住 —— 深色面板上就是一大块系统蓝。
        /// 做法：同尺寸自绘 Panel 精确覆盖文字区，原控件退化为纯数据源；
        /// 继承 <see cref="WheelForwardPanel"/> ⇒ 滚轮消息原样转发，不丢原生滚轮语义；点击转发展开。
        /// ⚠ hot / 激活状态是**本实例私有**的，绝不与框1 共用（否则悬停一个框会点亮另一个）。
        /// </summary>
        private class SteamFacePanel : WheelForwardPanel
        {
            private readonly ComboBox _combo;
            private readonly string _emptyText;
            private bool _hot = false;

            public SteamFacePanel(ComboBox target, string emptyText)
                : base(target)
            {
                _combo = target;
                _emptyText = (emptyText == null) ? "" : emptyText;
                BackColor = STEAM_PANEL;
                Cursor = Cursors.Hand;
                if (target != null) target.SelectedIndexChanged += new EventHandler(OnComboChanged);
                HookComboState(target, new EventHandler(OnComboStateChanged));   // 【T25-1】焦点/展开 → 重绘
            }

            private void OnComboChanged(object sender, EventArgs e) { Invalidate(); }

            private void OnComboStateChanged(object sender, EventArgs e) { Invalidate(); }

            /// <summary>当前应显示的文本：选中项 ToString()；空列表给占位文本（占位态用次级色）。</summary>
            public string CurrentText(out bool placeholder)
            {
                placeholder = false;
                string t = "";
                if (_combo != null)
                {
                    object sel = _combo.SelectedItem;
                    if (sel != null) t = sel.ToString();
                    else if (_combo.Text != null) t = _combo.Text;
                }
                if (t == null) t = "";
                if (t.Length == 0) { t = _emptyText; placeholder = true; }
                return t;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                // 四态语言与框1 的 `OnDropFacePaint` 逐条一致：
                //   底色：悬停 = #2A475E，其余 = #23262E
                //   边框：悬停或激活（聚焦 / 展开中）= #8B929A，其余 = #3D4450
                bool act = (_combo != null && (_combo.Focused || _combo.DroppedDown));
                using (SolidBrush bg = new SolidBrush(_hot ? STEAM_GRAD_TOP : STEAM_PANEL))
                    g.FillRectangle(bg, ClientRectangle);
                // 左 / 上 / 下三边（右边由箭头按钮的左边框接管，两条边拼成完整矩形，缝处即分隔线）
                using (Pen b = new Pen((_hot || act) ? STEAM_TEXT_DIM : STEAM_BORDER))
                {
                    g.DrawLine(b, 0, 0, Width - 1, 0);
                    g.DrawLine(b, 0, Height - 1, Width - 1, Height - 1);
                    g.DrawLine(b, 0, 0, 0, Height - 1);
                }
                bool placeholder;
                string txt = CurrentText(out placeholder);
                Color fg = placeholder ? STEAM_TEXT_DIM : Color.White;
                TextRenderer.DrawText(g, txt, this.Font,
                    new Rectangle(6, 0, Width - 12, Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                _hot = true; Invalidate(); base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                _hot = false; Invalidate(); base.OnMouseLeave(e);
            }

            protected override void OnClick(EventArgs e)
            {
                if (_combo != null && _combo.Enabled)
                {
                    _combo.Focus();
                    _combo.DroppedDown = true;
                }
                base.OnClick(e);
            }
        }

        /// <summary>把按钮刷成 Steam 面板样式（Flat 深色底 + #3D4450 描边 + 悬停/按下反馈）。</summary>
        private static void ApplySteamButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = STEAM_PANEL;
            b.ForeColor = STEAM_TEXT;
            b.FlatAppearance.BorderColor = STEAM_BORDER;
            b.FlatAppearance.MouseOverBackColor = STEAM_GRAD_TOP;
            b.FlatAppearance.MouseDownBackColor = STEAM_ACCENT;
        }

        private void BuildUi()
        {
            // 标题：13pt 加粗实测需 25px 高，必须显式给 28px，否则底端笔画被 Label 裁掉
            // （(16,8,400,28) 底边 = 36，仍在状态栏 Top=38 之上，不影响下方任何控件）
            // 颜色取 Steam 主文本色 #FFFFFF：原先的品牌绿在蓝色渐变上色相跳跃、显得突兀，
            // 改用 Steam 自己的页头做法（白字 + 细分隔线）后与主题融为一体。
            // 标题：12pt(16px，落在 Steam 字号阶梯 12/13/14/16 上) + 主文本色 + 分隔线建立层级。
            // 不用高饱和色：深色底上的高饱和亮色会产生光晕(halation)、小字尤甚 ——
            // Steam 的层级一律由「字号 + 字重 + 明度」建立，颜色只承载语义。
            // 【审核轮 2026-09-26】_lblTitle 字段删除（赋值后从不读取）。
            // 【收尾轮 2026-09-26】连局部变量也一并去掉：MakeLabel 内部已 Controls.Add，
            //   返回值本就无人读取 —— 留个 `Label lblTitle =` 只是"删了字段但留了个壳"。
            MakeLabel("守墓人 2  内存修改器", 16, 8, 400, 28, Color.White, 12f, true);
            // 状态栏：Steam 主文本色 #C6D4DF（在渐变顶端 6.4:1；原先的 #AAAAAA 只有 4.19:1，不达标）
            _lblStatus = MakeLabel("正在连接游戏……", 16, 38, 500, STEAM_TEXT, 9f, false);

            _progress = new ProgressBar();
            _progress.Left = 16; _progress.Top = 60; _progress.Width = 500; _progress.Height = 6;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Visible = false;
            Controls.Add(_progress);

            MakeLabel("功能", 16, 74, 100, STEAM_TEXT, 9f, true);
            MakeLabel("当前值", 300, 74, 90, STEAM_TEXT, 9f, true);
            MakeLabel("锁定为", 396, 74, 70, STEAM_TEXT, 9f, true);

            _btnRescan = new Button();
            _btnRescan.Text = "刷新";
            // 【t29】按钮行改为在内容网格内均匀分散：3 个等宽按钮（152px）+ 2 个等距间隙（22px）
            //   = 152*3 + 22*2 = 500 = 内容宽度（16..516），两端与内容边界对齐、视觉重量均衡。
            // 【2026-09-25】功能区少了一行（原「周点赞上限」项删除）⇒ 下方所有元素整体上移 34px，
            //   避免留下空洞；客户区高度同步 600 → 566。功能行 y 由 BuildFeatures 的循环自动重排。
            _btnRescan.Left = 16; _btnRescan.Top = 362; _btnRescan.Width = 152; _btnRescan.Height = 30;
            ApplySteamButton(_btnRescan);
            _btnRescan.Click += new EventHandler(OnRescanClick);
            Controls.Add(_btnRescan);

            _btnApplyOnce = new Button();
            _btnApplyOnce.Text = "立即写入";
            _btnApplyOnce.Left = 190; _btnApplyOnce.Top = 362; _btnApplyOnce.Width = 152; _btnApplyOnce.Height = 30;
            ApplySteamButton(_btnApplyOnce);
            _btnApplyOnce.Click += new EventHandler(OnApplyOnceClick);
            Controls.Add(_btnApplyOnce);

            Button btnAllOff = new Button();
            btnAllOff.Text = "取消全部锁定";
            btnAllOff.Left = 364; btnAllOff.Top = 362; btnAllOff.Width = 152; btnAllOff.Height = 30;
            ApplySteamButton(btnAllOff);
            btnAllOff.Click += new EventHandler(OnAllOffClick);
            Controls.Add(btnAllOff);

            // 【t26】「切换存档后请刷新数据」提示已删除 —— 程序具备读档/返回主菜单的自动检测
            // 与自动重定位（MonitorLifecycle 每秒校验锚+gameState），无需再让玩家手动刷新。

            // ---- 物品数量（Item 层）----
            // 【规格更新 2026-09-26】区块标题「物品数量修改」→「物品修改」：本区现在有两行 ——
            //   上行改数量（+0x40，4 字节）、下行换种类（+0x10，8 字节指针），各有独立的应用按钮。
            //   标题右侧放「应用后自动按 Tab 打开背包」开关（默认勾选，样式与功能区的自绘复选框一致）。
            MakeLabel("物品修改（上行改数量，下行换种类）", 16, 404, 230, STEAM_TEXT, 9f, true);

            _chkAutoRefresh = new SteamCheckBox();
            _chkAutoRefresh.Text = "应用后自动按 Tab 打开背包";
            _chkAutoRefresh.Left = 249; _chkAutoRefresh.Top = 404; _chkAutoRefresh.Width = 267;
            _chkAutoRefresh.Height = 20;
            _chkAutoRefresh.Checked = true;                 // 默认勾选（抢焦点会打断操作，故给开关）
            _chkAutoRefresh.BackColor = STEAM_PANEL;        // 方框内部用本行底色填充 ⇒ 视觉上无补丁
            _chkAutoRefresh.ForeColor = STEAM_TEXT;
            Controls.Add(_chkAutoRefresh);

            // 物品数量修改区：全部控件压在同一行（y≈458）
            // 排列：物品下拉 → 当前数量 → 修改为 → 应用
            // （「查询」按钮已于 2026-09-23 移除，腾出的宽度全部让给下拉框，便于预览完整物品名）
            _cmbItemId = new ComboBox();
            _cmbItemId.Left = 16; _cmbItemId.Top = 459; _cmbItemId.Width = 258;
            // 选物品只能从列表里选（DropDownList）：玩家手打的任意文字会绕开「中文名 → 物品 ID」的
            // 对应关系，导致「应用」按钮报「该物品不在背包里」；改为不可编辑后该歧义路径彻底消失。
            // 显示文本改为「中文名 × 数量」（不再附带英文 ID，普通玩家看不懂 itemId）。
            _cmbItemId.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbItemId.BackColor = STEAM_PANEL;
            _cmbItemId.ForeColor = Color.White;
            _cmbItemId.FlatStyle = FlatStyle.Flat;
            _cmbItemId.SelectedIndexChanged += new EventHandler(OnItemSelectionChanged);
            Controls.Add(_cmbItemId);

            // 【t25】自绘下拉箭头：WinForms 的 ComboBox 按钮区由系统主题绘制（实测 #F0F0F0 浅灰），
            // 不接受 BackColor ⇒ 在深色面板上是一块刺眼的亮块。这里用一块与下拉框同色的自绘
            // Panel 精确覆盖该区域，点击转发为 DroppedDown —— 只改绘制，不改任何取值逻辑。
            // 【t27】自绘下拉主体：覆盖 ComboBox 文字区。根因实测 —— DropDownList 样式的 ComboBox
            // 在「下拉已收起但控件仍持有焦点」时，WinForms 会用 SystemColors.Highlight(#0078D7)
            // 填满整个客户区（实测四态：常态/展开中/失焦 = #23262E 正常，Esc 收起后的聚焦态 = #0078D7）。
            // 该行为无公开属性可关，故用同尺寸自绘面板覆盖，视觉 100% 可控；
            // ComboBox 仍是唯一数据源（SelectedItem / Text 取值逻辑一字未改）。
            _pnlDropFace = new WheelForwardPanel(_cmbItemId);
            _pnlDropFace.BackColor = STEAM_PANEL;
            _pnlDropFace.Cursor = Cursors.Hand;
            _pnlDropFace.Paint += new PaintEventHandler(OnDropFacePaint);
            _pnlDropFace.Click += new EventHandler(OnDropFaceClick);
            _pnlDropFace.MouseEnter += new EventHandler(OnDropFaceEnter);
            _pnlDropFace.MouseLeave += new EventHandler(OnDropFaceLeave);
            Controls.Add(_pnlDropFace);
            _cmbItemId.GotFocus += new EventHandler(OnDropStateChanged);
            _cmbItemId.LostFocus += new EventHandler(OnDropStateChanged);
            _cmbItemId.DropDown += new EventHandler(OnDropStateChanged);
            _cmbItemId.DropDownClosed += new EventHandler(OnDropStateChanged);

            _pnlDropArrow = new WheelForwardPanel(_cmbItemId);
            _pnlDropArrow.Width = 24;
            _pnlDropArrow.BackColor = STEAM_PANEL;
            _pnlDropArrow.Cursor = Cursors.Hand;
            _pnlDropArrow.Paint += new PaintEventHandler(OnDropArrowPaint);
            _pnlDropArrow.Click += new EventHandler(OnDropArrowClick);
            _pnlDropArrow.MouseEnter += new EventHandler(OnDropArrowEnter);
            _pnlDropArrow.MouseLeave += new EventHandler(OnDropArrowLeave);
            Controls.Add(_pnlDropArrow);

            _lblItemCurCap = MakeLabel("当前数量", 334, 461, 58, STEAM_TEXT, 9f, false);
            _lblItemCurrent = MakeLabel("-", 392, 461, 34, STEAM_OK, 9f, true);

            _lblItemLockCap = MakeLabel("修改为", 430, 461, 44, STEAM_TEXT, 9f, false);
            // 要让数字在框内上下左右都居中：WinForms 单行 TextBox 的文本恒定贴顶（TextAlign 只管水平），
            // 所以外层用一个带边框的 Panel 充当"框"，内部放无边框 TextBox，并让它在 Panel 内垂直居中。
            _pnlItemLock = new Panel();
            _pnlItemLock.Width = 46; _pnlItemLock.Height = 24;
            _pnlItemLock.BackColor = STEAM_PANEL;
            // 【t27】边框改自绘 #3D4450（原 FixedSingle 是系统浅灰，与下拉/按钮的边框语言不一致）
            _pnlItemLock.BorderStyle = BorderStyle.None;
            _pnlItemLock.Paint += new PaintEventHandler(OnLockBoxPaint);
            Controls.Add(_pnlItemLock);

            _txtItemLock = new TextBox();
            _txtItemLock.Text = "99";
            _txtItemLock.TextAlign = HorizontalAlignment.Center;
            _txtItemLock.BackColor = STEAM_PANEL;
            _txtItemLock.ForeColor = Color.White;
            _txtItemLock.BorderStyle = BorderStyle.None;
            _pnlItemLock.Controls.Add(_txtItemLock);

            _btnItemApply = new Button();
            _btnItemApply.Text = "应用";
            _btnItemApply.Left = 524; _btnItemApply.Top = 459; _btnItemApply.Width = 58; _btnItemApply.Height = 24;
            ApplySteamButton(_btnItemApply);
            _btnItemApply.Click += new EventHandler(OnItemApplyClick);
            Controls.Add(_btnItemApply);

            // 日志栏：多行 + 自动换行 + 垂直滚动（原为单行覆盖式 500x22，
            // 长句会被硬裁 —— 实测「点赞…卖 1 件商品即可刷新显示」需 843px，只显示得下 500px）。
            // 高度 68px ≈ 3~4 行；客户区高度同步由 530 加到 576，不影响其它控件坐标。
            // 【2026-09-26 · 方案C】Top 458 → 492（整体下移 34px，高 68 不变）：
            //   为面板内新增的「种类 / 星级」行（459..485）腾位，行间距仍为 4px（分隔线 488）。
            _txtLog = new TextBox();
            _txtLog.Left = 16; _txtLog.Top = 492; _txtLog.Width = 500; _txtLog.Height = 68;
            _txtLog.Multiline = true;
            _txtLog.WordWrap = true;
            // 【t25】滚动条同样由系统绘制（白色，深色面板上是第二块亮斑）⇒ 关掉系统滚动条；
            // 多行 TextBox 仍支持鼠标滚轮滚动，且 Log() 每次都会自动滚到最新一行。
            _txtLog.ScrollBars = ScrollBars.None;
            _txtLog.ReadOnly = true;
            _txtLog.BackColor = STEAM_PANEL;
            _txtLog.ForeColor = STEAM_TEXT;
            // 【t25】日志栏并入「下半区面板」：去掉自身边框（BorderStyle.None）+ 底色取面板色，
            // 与面板无缝衔接 —— 否则会在面板内形成「框中框」，破坏单一层级。
            _txtLog.BorderStyle = BorderStyle.None;
            _txtLog.Text = "准备中……";
            Controls.Add(_txtLog);

            // ---- 【t37】最底端：Bilibili 感谢链接 ----
            // 位于日志栏（底 560）下方的新增区域：y=572..592，距客户区底边 8px。
            // 链接色取 Steam 强调蓝 #1A9FFF（在该处渐变底 #1B2838 上对比度 5.29:1，≥4.5）；
            // 悬停提亮为白色并显示下划线，符合链接的可发现性。
            _lnkThanks = new LinkLabel();
            _lnkThanks.Text = "东皇钟@Bilibili  感谢充电支持";
            // 【2026-09-26 · 方案C】Top 538 → 572（随客户区 +34 同步下移；底边 592，距底边仍 8px）
            _lnkThanks.Left = 16; _lnkThanks.Top = 572; _lnkThanks.Width = 500; _lnkThanks.Height = 20;
            _lnkThanks.Font = new Font("Microsoft YaHei UI", 9f);
            _lnkThanks.BackColor = Color.Transparent;
            _lnkThanks.LinkColor = STEAM_ACCENT;          // #1A9FFF
            _lnkThanks.ActiveLinkColor = Color.White;     // 悬停提亮
            _lnkThanks.VisitedLinkColor = STEAM_ACCENT;
            _lnkThanks.LinkBehavior = LinkBehavior.HoverUnderline;
            _lnkThanks.TextAlign = ContentAlignment.MiddleLeft;
            _lnkThanks.LinkArea = new LinkArea(0, _lnkThanks.Text.Length);   // 整行可点
            _lnkThanks.LinkClicked += new LinkLabelLinkClickedEventHandler(OnThanksLinkClicked);
            Controls.Add(_lnkThanks);

            // ---- 【2026-09-26 · 方案C 前置】物品种类修改：只读三级联动（框2 / 框3 / 回显）----
            // 与「物品数量修改」行同一套网格（左 16 / 右 516），纵向落在物品行与日志之间；
            // 真实坐标由 OnLoad 的 AlignKindRow() 按 CY=472 统一重排。
            BuildKindRow();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 必须在句柄创建之后再对齐：ComboBox 高度由字体决定，
            // 句柄未创建时 Height 只是默认值（远小于实际高度），拿它当基准会把按钮/标签压扁。
            AlignItemRow();
            AlignKindRow();      // 【方案C】种类 / 星级行的同基准对齐（同样需要句柄已创建）
        }

        /// <summary>
        /// 物品区一行控件的统一高度与横向中轴。
        /// WinForms 的 ComboBox 高度由字体决定、不可直接设置，因此以它的实际高度为基准，
        /// 让按钮 / 文本框 / 标签全部与它等高，并把所有元素的中轴对齐到同一条水平线。
        /// </summary>
        private void AlignItemRow()
        {
            const int CY = 437;          // 公共横向中轴
            const int GAP = 3;           // 元素间隙（紧凑）
            const int LEFT = 16;
            const int RIGHT = 516;       // 与上方功能区/下方日志框同一条右边界（相对窗口客户区）

            int h = _cmbItemId.Height;   // 以 ComboBox 的实际高度为准（OnLoad 后为真实值）
            if (h < 24) h = 26;          // 下限保护：过小会让按钮文字被裁、标签字显得残缺
            int top = CY - h / 2;

            // 标签宽度按实际文字精确收缩，消除标签内部的空白
            _lblItemCurCap.Width = TextRenderer.MeasureText(_lblItemCurCap.Text, _lblItemCurCap.Font).Width + 2;
            _lblItemLockCap.Width = TextRenderer.MeasureText(_lblItemLockCap.Text, _lblItemLockCap.Font).Width + 2;
            _lblItemCurrent.Width = 22;  // 数值列固定窄宽（够放 3 位数），避免数值变化触发整行重排抖动

            // 先量出固定部分，剩余宽度全部让给下拉框（便于完整预览物品名）
            int fixedW = _lblItemCurCap.Width + _lblItemCurrent.Width
                       + _lblItemLockCap.Width + _pnlItemLock.Width + _btnItemApply.Width + GAP * 5;
            int cmbW = RIGHT - LEFT - fixedW;
            if (cmbW < 120) cmbW = 120;

            // 从左到右依次紧密排布，右边界落在 RIGHT 上
            int x = LEFT;
            _cmbItemId.Left = x; _cmbItemId.Width = cmbW; x += cmbW + GAP;
            _lblItemCurCap.Left = x; x += _lblItemCurCap.Width + GAP;
            _lblItemCurrent.Left = x; x += _lblItemCurrent.Width + GAP;
            _lblItemLockCap.Left = x; x += _lblItemLockCap.Width + GAP;
            _pnlItemLock.Left = x; x += _pnlItemLock.Width + GAP;
            _btnItemApply.Left = x;

            // 高度统一 + 垂直居中：
            //   ComboBox 高度不可设 → 以它为准；Label 默认 TopLeft → 显式 MiddleLeft；
            //   单行 TextBox 文本贴顶 → 用外层 Panel 包一个无边框 TextBox 来达成框内居中。
            _cmbItemId.Top = top;
            // 自绘「主体 + 箭头」两块覆盖在 ComboBox 之上：主体覆盖文字区（压掉系统高亮填充），
            // 箭头贴右端。两者同顶、同高、边框拼成一个完整矩形，并置于 ComboBox 上方。
            _pnlDropFace.Left = _cmbItemId.Left;
            _pnlDropFace.Top = top;
            _pnlDropFace.Height = h;
            _pnlDropFace.Width = _cmbItemId.Width - _pnlDropArrow.Width;
            if (_pnlDropFace.Width < 40) _pnlDropFace.Width = 40;
            _pnlDropArrow.Left = _cmbItemId.Left + _cmbItemId.Width - _pnlDropArrow.Width;
            _pnlDropArrow.Top = top;
            _pnlDropArrow.Height = h;
            _pnlDropFace.BringToFront();
            _pnlDropArrow.BringToFront();
            _pnlDropFace.Invalidate();
            _pnlDropArrow.Invalidate();
            _lblItemCurCap.TextAlign = ContentAlignment.MiddleLeft;
            _lblItemCurrent.TextAlign = ContentAlignment.MiddleLeft;
            _lblItemLockCap.TextAlign = ContentAlignment.MiddleLeft;
            _lblItemCurCap.Height = h; _lblItemCurCap.Top = top;
            _lblItemCurrent.Height = h; _lblItemCurrent.Top = top;
            _lblItemLockCap.Height = h; _lblItemLockCap.Top = top;
            _pnlItemLock.Height = h; _pnlItemLock.Top = top;
            // 无边框 TextBox 在 Panel 内垂直居中（Panel 自身高度对齐同行控件）
            int lineH = _txtItemLock.Font.Height;
            _txtItemLock.Left = 3;                                    // 左右各留 3px 内边距（自绘边框 1px + 2px 呼吸）
            _txtItemLock.Width = _pnlItemLock.Width - 6;
            if (_txtItemLock.Width < 10) _txtItemLock.Width = _pnlItemLock.Width - 2;
            _txtItemLock.Height = lineH;
            _txtItemLock.Top = (_pnlItemLock.ClientSize.Height - lineH) / 2;
            if (_txtItemLock.Top < 0) _txtItemLock.Top = 0;
            _btnItemApply.Height = h; _btnItemApply.Top = top;
        }

        // ==================================================================
        // 【2026-09-26 · 方案C】「物品种类修改」三级联动 + 种类行写入
        //
        //   框1 = _cmbItemId（既有，背包中的物品）—— 行为一字未改，只增加「预置同种类」联动
        //   框2 = _cmbKind    （目标种类）—— 分组键 = 中文名 + ItemType 数值
        //   框3 = _cmbVariant （星级 / 属性）—— 所选种类下全部 id 的变体
        //   按钮 = _btnKindApply（种类行自己的「应用」，只写 Item + 0x10）
        //   （原「结果回显 _lblKindPick」已于 2026-09-26 收尾轮删除：它显示的是内部英文 id）
        //
        //   两个「应用」互不干扰：数量行只写 +0x40（4 字节），种类行只写 +0x10（8 字节）。
        //   写入成功且读回校验通过后，可选地切前台 + 连发两次 Tab（见 BagRefreshWorker）。
        //
        //   覆盖层共三层（自下而上）：ComboBox 本体 → 文字区 SteamFacePanel → SteamArrowPanel
        //   →（若置灰）面纱 veil 压在最上层。三者都是"只改绘制、不改取值"。
        //
        // 数据来源全部是既有只读字典（_itemNames / _itemQuality / _itemType / _itemAttrs），
        // 读取一律经 _nameGate；字符串拼装在 UI 线程完成（毫秒级，不阻塞、不扫内存）。
        // ==================================================================

        /// <summary>框2 / 框3 与回显的控件创建（坐标仅占位，真实布局见 <see cref="AlignKindRow"/>）。</summary>
        private void BuildKindRow()
        {
            // ⚠ 标签宽度按**实机实测**的中文 9pt 字宽（TextRenderer.MeasureText：
            //   「目标种类」= 56px、「星级」= 32px）+ 2px 余量。两轮实机验收抓到的缺陷：
            //   「目标种类」先后按 48 / 56px 被裁、「星级」按 26 / 30px 被裁成「星」。
            //   本行还要放下种类行自己的「应用」按钮（58px，与数量行按钮同宽同列），
            //   故标签取「种类」（34px）：语义由上行标题「物品修改（上行改数量，下行换种类）」交代。
            _lblKindCap = MakeLabel("种类", 16, 459, 34, STEAM_TEXT, 9f, false);

            _cmbKind = NewDarkCombo(53, 459, 130);
            _cmbKind.SelectedIndexChanged += new EventHandler(OnKindSelectionChanged);
            Controls.Add(_cmbKind);

            _lblVariantCap = MakeLabel("星级", 186, 459, 38, STEAM_TEXT, 9f, false);

            _cmbVariant = NewDarkCombo(227, 459, 104);      // 宽度由 AlignKindRow 自适应（此处仅初值）
            Controls.Add(_cmbVariant);

            // 下拉箭头自绘覆盖层：框2/框3 的按钮区同样由系统主题绘制（实机截图为**纯白亮块**），
            // 在深色面板上是一块刺眼补丁 ⇒ 与 _pnlDropArrow 同法覆盖（点击转发展开、滚轮原样转发）。
            _pnlKindArrow = new SteamArrowPanel(_cmbKind);
            Controls.Add(_pnlKindArrow);
            _pnlVarArrow = new SteamArrowPanel(_cmbVariant);
            Controls.Add(_pnlVarArrow);

            // 【收尾 · 样式统一】文字区覆盖层：`DropDownList` 的 ComboBox「已收起但仍持焦点」时会被
            // 系统用 Highlight(#0078D7) 填满客户区 ⇒ 与框1 的 _pnlDropFace 同规格再盖一层。
            // 空列表给占位文本（次级色），让「无数据」与「有数据」一眼可分。
            _pnlKindFace = new SteamFacePanel(_cmbKind, "（未读取到目标种类）");
            Controls.Add(_pnlKindFace);
            _pnlVarFace = new SteamFacePanel(_cmbVariant, "（无可用属性）");
            Controls.Add(_pnlVarFace);

            // 两块禁用「面纱」：框2（数据未就绪）/ 框3（该物品不分星级）。
            // 均为自绘，底色与文字 100% 可控 —— 杜绝系统禁用态在深色面板上画浅灰底 / 画不可见文字。
            _pnlKindVeil = NewVeil();
            _pnlVarVeil = NewVeil();

            // 【2026-09-26 收尾轮】原「结果回显」标签（334..455，显示内部英文 id ）已删除，
            //   该宽度转给框3（见 AlignKindRow 的动态宽度计算）。

            // 种类行自己的「应用」：只改 id（+0x10），与数量行的按钮互不干扰，右边界与数量行按钮齐平
            _btnKindApply = new Button();
            _btnKindApply.Text = "应用";
            _btnKindApply.Left = 458; _btnKindApply.Top = 459; _btnKindApply.Width = 58; _btnKindApply.Height = 24;
            ApplySteamButton(_btnKindApply);
            _btnKindApply.Click += new EventHandler(OnKindApplyClick);
            Controls.Add(_btnKindApply);

            SetKindBlocked("数据未就绪");
            SetVariantBlocked("请先选择种类");
        }

        /// <summary>深色主题只读下拉框（DropDownList + 面板底 + 白字 + Flat）。</summary>
        private ComboBox NewDarkCombo(int x, int y, int w)
        {
            ComboBox c = new ComboBox();
            c.Left = x; c.Top = y; c.Width = w;
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.BackColor = STEAM_PANEL;
            c.ForeColor = Color.White;
            c.FlatStyle = FlatStyle.Flat;
            return c;
        }

        /// <summary>禁用态自绘面纱（覆盖在禁用下拉框之上，保证禁用也能看清文字）。</summary>
        private Panel NewVeil()
        {
            Panel p = new Panel();
            p.BackColor = STEAM_INPUT_OFF;
            p.BorderStyle = BorderStyle.None;
            p.Cursor = Cursors.Default;
            p.Paint += new PaintEventHandler(OnVeilPaint);
            p.Visible = false;
            Controls.Add(p);
            return p;
        }

        /// <summary>面纱绘制：更暗的禁用底 + 1px 边框 + #9AA3AE 文字（对底色 6.67:1）。</summary>
        private void OnVeilPaint(object sender, PaintEventArgs e)
        {
            Panel p = sender as Panel;
            if (p == null) return;
            using (SolidBrush bg = new SolidBrush(STEAM_INPUT_OFF))
                e.Graphics.FillRectangle(bg, p.ClientRectangle);
            using (Pen b = new Pen(STEAM_BORDER))
                e.Graphics.DrawRectangle(b, 0, 0, p.Width - 1, p.Height - 1);
            string txt = p.Tag == null ? "" : p.Tag.ToString();
            TextRenderer.DrawText(e.Graphics, txt, this.Font,
                new Rectangle(6, 0, p.Width - 12, p.Height), STEAM_TEXT_OFF,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        /// <summary>
        /// 种类 / 星级行的统一布局（与 <see cref="AlignItemRow"/> 同一套网格与基准）：
        ///   左边界 16、右边界 516、间隙 3、控件高度 = 既有 ComboBox 的实际高度、公共中轴 CY = 472。
        ///   横向分配：种类(34) | 框2(130) | 星级(38) | 框3(自适应，吃掉剩余宽度) | 应用(58) + 4×3 间隙。
        ///   （2026-09-26 收尾轮：原「回显」格已删除，其宽度转给框3。）
        ///   右侧「应用」按钮与数量行的 `_btnItemApply` **同宽（58）同右边界（516）**，两行按钮严格同列。
        /// </summary>
        private void AlignKindRow()
        {
            const int CY = 472;
            const int GAP = 3;
            const int LEFT = 16;
            const int RIGHT = 516;
            const int BTN_W = 58;        // 与 AlignItemRow 的 _btnItemApply 同宽

            int h = _cmbItemId.Height;
            if (h < 24) h = 26;
            int top = CY - h / 2;

            int x = LEFT;
            _lblKindCap.Left = x; x += _lblKindCap.Width + GAP;
            _cmbKind.Left = x; x += _cmbKind.Width + GAP;
            _lblVariantCap.Left = x; x += _lblVariantCap.Width + GAP;
            // 先钉住右侧按钮（右边界 516），框3 吃掉中间剩余宽度。
            // 【2026-09-26 收尾轮】原「结果回显」标签占位已删除 ⇒ 框3 由固定 104px 改为自适应
            //   （实测 227..455 = 228px，约原来 2.2 倍）：星级 / 红白属性文本更不容易被省略号截断。
            _btnKindApply.Left = RIGHT - BTN_W;
            _btnKindApply.Width = BTN_W;
            _cmbVariant.Left = x;
            _cmbVariant.Width = _btnKindApply.Left - GAP - x;
            if (_cmbVariant.Width < 104) _cmbVariant.Width = 104;   // 下限 = 原固定宽度

            _lblKindCap.Top = top; _lblKindCap.Height = h;
            _lblKindCap.TextAlign = ContentAlignment.MiddleLeft;
            _lblVariantCap.Top = top; _lblVariantCap.Height = h;
            _lblVariantCap.TextAlign = ContentAlignment.MiddleLeft;
            _btnKindApply.Top = top; _btnKindApply.Height = h;

            _cmbKind.Top = top;
            _cmbVariant.Top = top;
            // z-order（自下而上）：ComboBox 本体 → 文字区 face → 箭头 arrow → 置灰面纱 veil
            PlaceFace(_pnlKindFace, _cmbKind, _pnlKindArrow, top, h);
            PlaceFace(_pnlVarFace, _cmbVariant, _pnlVarArrow, top, h);
            PlaceArrow(_pnlKindArrow, _cmbKind, top, h);
            PlaceArrow(_pnlVarArrow, _cmbVariant, top, h);
            PlaceVeil(_pnlKindVeil, _cmbKind, top, h);
            PlaceVeil(_pnlVarVeil, _cmbVariant, top, h);
            // 面纱必须在**最上层**（禁用时把 face 与 arrow 一起盖住，不留亮块、不漏字）
            if (_pnlKindVeil != null && _pnlKindVeil.Visible) _pnlKindVeil.BringToFront();
            if (_pnlVarVeil != null && _pnlVarVeil.Visible) _pnlVarVeil.BringToFront();
        }

        /// <summary>
        /// 把文字区覆盖层对齐到目标下拉框：宽度 = 控件宽 − 箭头宽，右边紧贴箭头的左边框
        /// （两条边拼成一个完整矩形，缝处即分隔线）——与框1 的 `_pnlDropFace` / `_pnlDropArrow` 同规格。
        /// </summary>
        private static void PlaceFace(Panel face, ComboBox target, Panel arrow, int top, int h)
        {
            if (face == null || target == null) return;
            int aw = (arrow == null) ? 24 : arrow.Width;
            face.Top = top;
            face.Height = h;
            face.Left = target.Left;
            face.Width = target.Width - aw;
            if (face.Width < 40) face.Width = 40;
            face.BringToFront();
            face.Invalidate();
        }

        /// <summary>把自绘下拉箭头对齐到目标下拉框右端（宽 24，与既有 _pnlDropArrow 同规格）。</summary>
        private static void PlaceArrow(Panel arrow, ComboBox target, int top, int h)
        {
            if (arrow == null || target == null) return;
            arrow.Top = top;
            arrow.Height = h;
            arrow.Left = target.Left + target.Width - arrow.Width;
            arrow.BringToFront();
            arrow.Invalidate();
        }

        /// <summary>把面纱对齐到目标下拉框（同左、同顶、同宽、同高）。</summary>
        private static void PlaceVeil(Panel veil, ComboBox target, int top, int h)
        {
            if (veil == null || target == null) return;
            veil.Left = target.Left;
            veil.Top = top;
            veil.Width = target.Width;
            veil.Height = h;
            if (veil.Visible) veil.BringToFront();
            veil.Invalidate();
        }

        /// <summary>框2 置灰/解禁（reason 非空即置灰；文字由面纱自绘）。</summary>
        private void SetKindBlocked(string reason)
        {
            if (_cmbKind != null) _cmbKind.Enabled = (reason == null || reason.Length == 0);
            SetVeil(_pnlKindVeil, reason);
            InvalidateFaces();
        }

        /// <summary>框3 置灰/解禁。禁用时仍为原生 Enabled=false（真正展不开），文字由面纱自绘。</summary>
        private void SetVariantBlocked(string reason)
        {
            if (_cmbVariant != null) _cmbVariant.Enabled = (reason == null || reason.Length == 0);
            SetVeil(_pnlVarVeil, reason);
            InvalidateFaces();
        }

        /// <summary>重绘框2/框3 的文字区覆盖层（选中项或列表变化后必须调用，否则文本停留在旧值）。</summary>
        private void InvalidateFaces()
        {
            if (_pnlKindFace != null) _pnlKindFace.Invalidate();
            if (_pnlVarFace != null) _pnlVarFace.Invalidate();
        }

        private static void SetVeil(Panel veil, string reason)
        {
            if (veil == null) return;
            bool blocked = (reason != null && reason.Length > 0);
            veil.Tag = blocked ? reason : null;
            veil.Visible = blocked;
            if (blocked) veil.BringToFront();
            veil.Invalidate();
        }

        /// <summary>
        /// 【按需重建】框2 只在**数据版本变化**时重建：版本 = 类型表 / 星级表 / 名表条数。
        /// 入口是 SetInventory 与 RefillInventoryNames（覆盖热刷新、冷刷新、预热回填、换档），
        /// 版本未变时直接返回 ⇒ 不会在玩家每次选择时重建框2（那会打断当前选择）。
        /// </summary>
        private void EnsureKindList()
        {
            if (InvokeRequired) { BeginInvoke(new Action(EnsureKindList)); return; }
            // 【t30 · R5 订正】原实现把 `_loc.AliasTableCount` 也拼进版本串，理由是「别名表异步就绪时
            //   NameTableCount 不变 ⇒ 框2 不会重建 ⇒ 43 条补不进去」。该理由**不成立**：
            //   UI 侧 `_loc` 在缓存命中 / 预热成功路径下原先根本不建别名表（实测恒为 0），
            //   而 43 条入框2 靠的是 `NameTableCount` 变化（名表缓存本身已含别名单条目）。
            //   ⇒ 该字段无效、且别名表补建就绪时会带来一次多余重建，已删除。
            //   若将来确需按别名表状态重建框2，应读**预热线程实例**的状态（需跨实例暴露），
            //   而不是 UI 侧 `_loc`（后者在缓存命中路径下语义不同）。
            string v = ItemTypeCount + "/" + ItemQualityCount + "/" + NameTableCount;
            if (v == _kindDataVersion && _kinds.Count > 0) return;
            _kindDataVersion = v;
            RebuildKindList();
        }

        /// <summary>
        /// 重建框2「目标种类」。分组键 = **中文名 + ItemType 数值**；显示文本三条规则：
        ///   ① 默认 = 中文名（不含星级/红白后缀）；
        ///   ② 同一中文名下 ItemType 多于一个 ⇒ 该中文名的所有条目追加 " · " + ItemTypeName(type)；
        ///   ③ 组内 id 多于一个、且这些 id 的 ItemSuffix 全同（例如都不分星级且红白均 0）
        ///      ⇒ 拆成「每 id 一条」并在名称后追加 " (id)"，否则玩家在框2 里无法区分。
        /// 数据缺失（名字 / 星级 / 类型任一查不到）一律**跳过**该 id：不抛异常、不上屏半成品。
        /// </summary>
        private void RebuildKindList()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RebuildKindList)); return; }

            // ① 快照：持锁只做一次字典遍历，字符串工作全部在锁外
            List<string> ids = new List<string>();
            Dictionary<string, int> types = new Dictionary<string, int>();
            lock (_nameGate)
            {
                foreach (KeyValuePair<string, int> kv in _itemType)
                {
                    if (kv.Key == null || kv.Key.Length == 0) continue;
                    ids.Add(kv.Key);
                    types[kv.Key] = kv.Value;
                }
            }
            ids.Sort(StringComparer.Ordinal);

            // ② 锁外逐 id 取名 / 后缀 / 星级（DisplayName / ItemSuffix 内部各自加锁）
            Dictionary<string, KindEntry> acc = new Dictionary<string, KindEntry>();
            Dictionary<string, HashSet<int>> typesOfName = new Dictionary<string, HashSet<int>>();
            Dictionary<string, string> suffixOf = new Dictionary<string, string>();
            int noZhCount = 0;      // 【方案A 取证】走原始 id 兜底的 id 数
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                int t = types[id];
                string disp = DisplayName(id);
                // 【方案A 兜底 2026-09-26】查不到中文名时 DisplayName 会返回**原始 id**：
                //   旧实现在此 `continue` 跳过 ⇒ 框2 只列有中文名的种类（实测 593 条），
                //   无中文名的物品**根本无法选作目标**。现在改为**不跳过**、直接用 id 作种类名，
                //   使框2 覆盖全库。id 兜底项的显示名 = id 本身（唯一），因此各自成组，
                //   不会与中文名分组混淆；「同名不同 ItemType」「同组多 id 且后缀全同」两条
                //   既有显示规则保持不变。
                if (disp == null || disp.Length == 0) continue;
                if (disp == id) noZhCount++;      // 【方案A 取证】无中文名 ⇒ 走 id 兜底
                int[] q;
                lock (_nameGate) { _itemQuality.TryGetValue(id, out q); }
                if (q == null || q.Length < 2) continue;                        // 星级数据缺失 ⇒ 跳过

                string suf = ItemSuffix(id);
                string zh = StripSuffix(disp, suf);

                string key = zh + "\u0001" + t.ToString(CultureInfo.InvariantCulture);
                KindEntry e;
                if (!acc.TryGetValue(key, out e))
                {
                    e = new KindEntry("", zh, t, new List<string>());
                    acc[key] = e;
                }
                e.Ids.Add(id);
                suffixOf[id] = suf;

                HashSet<int> ts;
                if (!typesOfName.TryGetValue(zh, out ts))
                {
                    ts = new HashSet<int>();
                    typesOfName[zh] = ts;
                }
                ts.Add(t);
            }

            // ③ 定显示文本（含「后缀全同 ⇒ 按 id 拆分」）
            List<KindEntry> list = new List<KindEntry>();
            foreach (KindEntry e in acc.Values)
            {
                e.Ids.Sort(StringComparer.Ordinal);
                string label = e.Base;
                HashSet<int> ts;
                if (typesOfName.TryGetValue(e.Base, out ts) && ts.Count > 1)
                    label = label + " · " + ItemTypeName(e.Type);

                bool sameSuffix = false;
                if (e.Ids.Count > 1)
                {
                    sameSuffix = true;
                    string first = suffixOf[e.Ids[0]];
                    for (int i = 1; i < e.Ids.Count; i++)
                    {
                        if (suffixOf[e.Ids[i]] != first) { sameSuffix = false; break; }
                    }
                }

                if (sameSuffix)
                {
                    for (int i = 0; i < e.Ids.Count; i++)
                    {
                        List<string> one = new List<string>();
                        one.Add(e.Ids[i]);
                        list.Add(new KindEntry(label + " (" + e.Ids[i] + ")", e.Base, e.Type, one));
                    }
                }
                else
                {
                    e.Text = label;
                    list.Add(e);
                }
            }
            list.Sort(CompareKindEntry);

            // ④ 回填（按显示文本保留玩家当前选择）
            string keep = "";
            KindEntry cur = _cmbKind.SelectedItem as KindEntry;
            if (cur != null) keep = cur.Text;

            _kinds.Clear();
            for (int i = 0; i < list.Count; i++) _kinds.Add(list[i]);

            _suppressKind = true;
            _cmbKind.BeginUpdate();
            _cmbKind.Items.Clear();
            for (int i = 0; i < _kinds.Count; i++) _cmbKind.Items.Add(_kinds[i]);
            _cmbKind.EndUpdate();
            int sel = -1;
            if (keep.Length > 0)
            {
                for (int i = 0; i < _kinds.Count; i++)
                    if (_kinds[i].Text == keep) { sel = i; break; }
            }
            _cmbKind.SelectedIndex = sel;
            _suppressKind = false;

            SetKindBlocked(_kinds.Count > 0 ? null : "数据未就绪");
            FillVariantList();      // 框2 变了 ⇒ 同步重建框3 与回显

            if (_kinds.Count == 0)
            {
                // 只在「三张表都齐了却仍聚合不出条目」时才提示（真异常）。
                // 预热过程中的中间态（名表未装 / 类型表未填）不再误报噪声日志
                //（实机两轮都抓到该误报：提示出现在「种类列表已成功建立」之前）。
                if (!_kindStarvedLogged && _namesReady && NameTableCount > 0
                    && ItemTypeCount > 0 && ItemQualityCount > 0)
                {
                    _kindStarvedLogged = true;
                    Log("物品种类列表暂时不可用（星级数据尚未读取完成），稍后会自动再读一次。");
                }
            }
            else
            {
                _kindStarvedLogged = false;
            }

            // ---------------- 【t30 · R6④】取证汇总日志（**默认关闭**）----------------
            // 原实现在每次框2 重建时都拼装一条含全部实测数字的长日志并上屏 —— 属「取证代码进生产」。
            // 现降为调试开关：只有 GK2TRAINER_EVIDENCE=1 时才会执行（关闭时零开销）。
            // 日志栏是**单行覆盖式**（见 tools\Watch-TrainerLog.ps1 的说明）：多条连续 Log 会
            // 互相覆盖 ⇒ 把全部实测数字组装成**一条**输出，只在内容变化时记录一次。
            if (EvidenceMode && ids.Count > 0)      // 【F3 订正】数据未就绪时不再吐「全库 0 个 id」过早行
            {
                int aliasCount = (_loc == null) ? -1 : _loc.AliasTableCount;
                if (ids.Count != _kindCoverageIds || noZhCount != _kindCoverageNoZh
                    || _kinds.Count != _kindCoverageKinds || aliasCount != _kindCoverageAlias
                    || _statAliasAdded != _kindCoverageStatAdded)
                {
                    _kindCoverageIds = ids.Count;
                    _kindCoverageNoZh = noZhCount;
                    _kindCoverageKinds = _kinds.Count;
                    _kindCoverageAlias = aliasCount;
                    _kindCoverageStatAdded = _statAliasAdded;

                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    sb.Append("【方案A 实测】全库 ").Append(ids.Count)
                      .Append(" 个 id；无中文名 ").Append(noZhCount)
                      .Append(" 个（按原始 id 兜底）⇒ 框2 条目 ").Append(_kinds.Count);
                    sb.Append("；别名表 ").Append(_statAliasText);
                    // 【F3 订正】原先无条件打印对照数字：缓存命中路径下对照实验不跑
                    //   ⇒ 会吐出「名表 682 条（对照无别名链 -1 条 ⇒ 别名链新增 -1 条）」
                    //   自相矛盾的一行。现在只在对照确实跑过（≥0）时才打印。
                    if (_statNameWithAlias >= 0) sb.Append("；名表 ").Append(_statNameWithAlias).Append(" 条");
                    else sb.Append("；名表 未知");
                    if (_statNameBase >= 0 && _statAliasAdded >= 0)
                        sb.Append("（对照无别名链 ").Append(_statNameBase)
                          .Append(" 条 ⇒ 别名链新增 ").Append(_statAliasAdded).Append(" 条）");
                    else
                        sb.Append("（本次路径未跑对照实验 ⇒ 对照数字见报告 §7.3）");
                    sb.Append("；回归抽样 cabbage:1=").Append(DisplayName("cabbage:1"))
                      .Append(" / cabbage:2=").Append(DisplayName("cabbage:2"))
                      .Append(" / bones_1_1:2=").Append(DisplayName("bones_1_1:2"));
                    // 同名不同 ItemType 的消歧标注：全库只影响「外科医生的失误」6 条
                    string parts = "";
                    for (int i = 0; i < _kinds.Count; i++)
                    {
                        string tx = _kinds[i].Text;
                        if (tx == null || tx.IndexOf("外科医生的失误") < 0) continue;
                        if (parts.Length > 0) parts += " | ";
                        parts += tx + "=[" + string.Join(",", _kinds[i].Ids.ToArray()) + "]";
                    }
                    if (parts.Length == 0) parts = "(未出现)";
                    sb.Append("；部件条目：").Append(parts);
                    Log(sb.ToString());
                }
            }
        }

        /// <summary>框2 选中变化 ⇒ 重建框3 + 重算置灰 + 更新回显（**不重建框2**）。</summary>
        private void OnKindSelectionChanged(object sender, EventArgs e)
        {
            if (_suppressKind) return;
            FillVariantList();
        }

        /// <summary>
        /// 重建框3：列出所选种类下**全部 id** 的变体；置灰规则**逐 id 判定**（绝不按中文名判定）：
        ///   · 该种类只有一个 id 且 qualityType == 0 ⇒ 置灰 +「不分星级」；
        ///   · 该种类只有一个 id 且 qualityType == 1 ⇒ 置灰 +「只有一种星级」（只有一个选项，展开无意义）；
        ///   · 该种类多于一个 id ⇒ 框3 可用，列出全部变体。
        /// </summary>
        private void FillVariantList()
        {
            KindEntry k = (_cmbKind == null) ? null : (_cmbKind.SelectedItem as KindEntry);

            _cmbVariant.BeginUpdate();
            _cmbVariant.Items.Clear();
            _cmbVariant.EndUpdate();

            if (k == null)
            {
                SetVariantBlocked("请先选择种类");
                return;
            }

            if (k.Ids.Count <= 1)
            {
                string only = (k.Ids.Count == 1) ? k.Ids[0] : "";
                // 提示文案取「仅一种星级 / 不分星级」（2026-09-26 起框3 宽 228px，四字绰绰有余）
                SetVariantBlocked(QualityTypeOf(only) == 1 ? "仅一种星级" : "不分星级");
                return;
            }

            List<string> texts = new List<string>();
            for (int i = 0; i < k.Ids.Count; i++) texts.Add(VariantText(k.Ids[i]));
            for (int i = 0; i < k.Ids.Count; i++)
            {
                string t = texts[i];
                for (int j = 0; j < k.Ids.Count; j++)      // 兜底：变体文本重复时补 id，保证可区分
                {
                    if (j == i) continue;
                    if (texts[j] == t) { t = t + " (" + k.Ids[i] + ")"; break; }
                }
                _cmbVariant.Items.Add(new VariantEntry(k.Ids[i], t));
            }
            SetVariantBlocked(null);
            _cmbVariant.SelectedIndex = 0;
        }

        /// <summary>框2 + 框3 当前选择对应的目标物品 id（写入判定专用；2026-09-26 起不再上屏）。</summary>
        private string CurrentKindPickId()
        {
            VariantEntry v = (_cmbVariant == null) ? null : (_cmbVariant.SelectedItem as VariantEntry);
            if (v != null) return v.Id;
            KindEntry k = (_cmbKind == null) ? null : (_cmbKind.SelectedItem as KindEntry);
            if (k != null && k.Ids.Count == 1) return k.Ids[0];
            return "";
        }

        // ==================================================================
        // 【规格更新 2026-09-26】种类行写入：只改 Item.id（+0x10，**8 字节指针**）
        //
        //   作用对象 = 框1 当前选中的那个 Item（与数量行**同一个对象、不同字段**）：
        //     数量行 `OnItemApplyClick` → 只写 +0x40（count，4 字节）
        //     种类行 `OnKindApplyClick` → 只写 +0x10（id 指针，8 字节）
        //   两个处理器各有独立的守卫、判定、提示与日志，**不共用任何可变状态**。
        // ==================================================================

        /// <summary>Item 实例快照字节数（B 实测实例大小 0x50 = 80 字节）。</summary>
        private const int ITEM_SNAPSHOT_BYTES = 0x50;

        /// <summary>
        /// 种类行「应用」：把框1 选中物品的 id 指针换成框2 + 框3 选中的目标 id 指针。
        /// ⛔ 只写 +0x10 一个字段。**绝不碰** +0x18（definition）/ +0x20（cachedId）——
        ///    源码 `ObjectLinkedToDefinition&lt;T&gt;.Definition` 的 getter 以 `cachedId != id` 为失效判据，
        ///    会自己重查并回填这两处；手工把 cachedId 改成新 id 会**关掉该自愈**、造成「id 新 / 属性旧」。
        /// ⛔ 也绝不碰 +0x40（count，由数量行按钮独占）。
        /// </summary>
        private void OnKindApplyClick(object sender, EventArgs e)
        {
            try
            {
                if (_loc == null || !_mem.IsOpen) { Log("还没有连接游戏，请先点「刷新」。"); return; }
                if (_scanBusy) { Log("正在读取游戏数据，请稍候再试。"); return; }
                if (!CanWrite()) { Log("游戏数据尚未就绪，请稍候或点「刷新」。"); return; }

                if (_selItemAddr == 0 || _selItemId == null || _selItemId.Length == 0)
                { Log("请先在第一行选中要修改的物品。"); return; }

                string targetId = CurrentKindPickId();
                if (targetId.Length == 0) { Log("请先选好要换成的种类与星级。"); return; }
                if (targetId == "empty") { Log("「empty」不是可以换的种类，请另选一个。"); return; }
                if (targetId == _selItemId) { Log("目标种类与当前物品相同，无需修改。"); return; }

                // 目标指针**只能**取自游戏堆里已存在的 id 字符串；取不到就拒绝（绝不用 0 或旧指针凑）
                long targetPtr;
                lock (_nameGate) { _itemDefIdPtr.TryGetValue(targetId, out targetPtr); }
                if (targetPtr == 0) { Log("暂时拿不到目标物品的数据，请点「刷新」后再试。"); return; }

                string note;
                if (!SwapItemId(_selItemAddr, targetId, targetPtr, out note))
                { Log("更换未成功：" + note + "。"); return; }

                // 成功路径**不回滚**（回滚会让玩家的修改白做）。
                // 日志口径：只说「已修改」；自动刷新由后台线程在真正发键之后再补一条，
                // **不得**宣称「界面已刷新 / 已生效」（无法确认游戏真的重绘成功）。
                Log("已把「" + DisplayName(_selItemId) + "」换成「" + DisplayName(targetId) + "」。");
                if (_chkAutoRefresh != null && _chkAutoRefresh.Checked) StartBagRefresh(_pid);
                else Log("请手动重开一次背包查看结果。");
            }
            catch (Exception ex)
            {
                _lastErrorNote = ex.GetType().Name;
                SaveErrorNote();
                Log("更换未成功，请重新点「刷新」后再试一次。");
            }
        }

        /// <summary>
        /// 【写入核心】把 <paramref name="itemAddr"/> 的 `Item.id`（+0x10，8 字节指针）换成
        /// <paramref name="targetIdPtr"/>。步骤严格对齐 E 文档 §4 安全契约：
        ///   ① 写前 vtable 校验（`GameResLocator.GetClassName` 必须解析出 "Item"）；
        ///   ② 80 字节整实例快照（回滚用）；
        ///   ③ **目标指针内容复核**（F2）：`ReadMonoString(targetIdPtr)` 必须 == 目标 id ——
        ///      写后读回只证明"指针写进去了"，不证明"它指向目标 id"；
        ///   ④ **只写 8 字节**，且**显式检查**写入 API 返回值（只读句柄上调用写 API 会静默返回 false）；
        ///   ⑤ 写后复读必须等于目标指针，否则**立即回滚**并逐字节校验回滚结果；
        ///   ⑥ 写后**重读 +0x00** 与写前快照里的 vtable 比对（T22-5）：不一致 ⇒ 对象已被移动/回收
        ///      ⇒ 回滚 + 报失败（绝不静默成功）；
        ///   ⑦ 顺带确认 +0x40（count）一个字节都没变（证明两行按钮互不干扰）。
        /// 全程不构造任何 MonoString；除"回滚写回 80 字节快照"外，绝不写 +0x18 / +0x20 / +0x40。
        /// </summary>
        private bool SwapItemId(long itemAddr, string targetId, long targetIdPtr, out string note)
        {
            note = "";
            if (itemAddr == 0 || targetIdPtr == 0) { note = "地址无效"; return false; }

            // ① 写前：vtable → 类名必须是 Item（对象未被回收 / 未被移动）
            long vt = _mem.ReadLong(itemAddr);
            if (vt == 0 || _loc.GetClassName(vt) != "Item")
            { note = "该物品的对象已经变化，请点「刷新」后再试"; return false; }

            // ② 80 字节整实例快照 + 写前读一次 +0x10
            byte[] snap = _mem.ReadBytes(itemAddr, ITEM_SNAPSHOT_BYTES);
            if (snap == null || snap.Length < ITEM_SNAPSHOT_BYTES) { note = "读取物品数据失败"; return false; }
            long oldPtr = BitConverter.ToInt64(snap, GameResLocator.ITEM_ITEMID);
            int oldCount = BitConverter.ToInt32(snap, GameResLocator.ITEM_COUNT);

            // ③ 目标指针内容复核（F2）：指针不该被盲信 —— 读它指向的 MonoString 看是不是目标 id。
            //    不相等 ⇒ 指针已失效/被别的内容占用 ⇒ 拒绝写入（绝不用它去覆盖玩家物品的 id）。
            if (targetId != null && targetId.Length > 0)
            {
                string atPtr = _loc.ReadMonoString(targetIdPtr);
                if (atPtr == null || atPtr != targetId)
                { note = "目标物品的数据不可信（指针指向的不是该物品），请点「刷新」后再试"; return false; }
            }

            // ④ 只写 8 字节；写入 API 的返回值必须为真（否则是"看起来成功其实没写"）
            if (!_mem.WriteBytes(itemAddr + GameResLocator.ITEM_ITEMID,
                                 BitConverter.GetBytes(targetIdPtr)))
            { note = "写入被拒绝（可能没有写权限），请以管理员身份重开本工具后再试"; return false; }

            // ⑤ 写后复读：必须等于目标指针；不等 ⇒ 立即回滚
            long back = _mem.ReadLong(itemAddr + GameResLocator.ITEM_ITEMID);
            if (back != targetIdPtr)
            {
                string rb = RollbackItem(itemAddr, snap);
                note = "写后校验不一致（读回值不是目标物品）," + rb;
                return false;
            }

            // ⑥ 写后重读 +0x00，与写前快照里的 vtable 比对（T22-5）：
            //    E §4 契约第 2 条要求"写入前后各读一次 +0x00 比对" —— 不一致说明对象已被移动/回收，
            //    此时即便 +0x10 读回等于目标指针也不能算成功（我们可能写在了旧地址上）⇒ 回滚 + 报失败。
            if (_mem.ReadLong(itemAddr) != BitConverter.ToInt64(snap, 0))
            {
                string rbV = RollbackItem(itemAddr, snap);
                note = "写入后该物品的对象已变化（vtable 不同）," + rbV;
                return false;
            }

            // ⑦ 数量字段（+0x40，4 字节）必须一个字节都没动 —— 两行按钮互不干扰的自证
            if (_mem.ReadInt(itemAddr + GameResLocator.ITEM_COUNT) != oldCount)
            {
                string rb2 = RollbackItem(itemAddr, snap);
                note = "数量字段被意外改动," + rb2;
                return false;
            }

            if (oldPtr == targetIdPtr) note = "目标与当前相同";   // 理论上已被调用方拦掉
            return true;
        }

        /// <summary>
        /// 回滚：把 80 字节快照整体写回，然后逐字节复读校验。
        /// 允许 +0x18..+0x27（definition / cachedId）在此期间被游戏 getter 自愈改掉
        /// ——那两处**本来就会**随 `cachedId != id` 失效判据自动重查，其余字节必须与快照完全一致。
        /// </summary>
        private string RollbackItem(long itemAddr, byte[] snap)
        {
            if (!_mem.WriteBytes(itemAddr, snap)) return "回滚失败，请重开背包确认物品状态";
            byte[] after = _mem.ReadBytes(itemAddr, ITEM_SNAPSHOT_BYTES);
            if (after == null || after.Length < snap.Length) return "回滚后无法复读，请重开背包确认物品状态";
            int diff = 0;
            bool onlySelfHeal = true;
            for (int i = 0; i < snap.Length; i++)
            {
                if (after[i] == snap[i]) continue;
                diff++;
                if (i < 0x18 || i > 0x27) onlySelfHeal = false;
            }
            if (diff == 0) return "已回滚（80 字节与写前快照逐字节一致）";
            if (onlySelfHeal) return "已回滚（仅 definition/cachedId 被游戏自愈改动）";
            return "回滚后校验不一致，请重开背包确认物品状态";
        }

        // ------------------------------------------------------------------
        // 【规格更新 2026-09-26】写入成功后的自动刷新：切前台 + 连发两次 Tab
        //   ⚠ 只在「写入成功且读回校验通过」之后调用；任何失败路径都不发按键。
        //   ⚠ 全程在后台线程：合计约 0.8~1.0 s 的等待，绝不能阻塞 UI 线程。
        // ------------------------------------------------------------------

        /// <summary>Win32 模拟输入出口（只用于「打开背包」这一个动作，不发送任何其它按键）。</summary>
        private static class Input32
        {
            public const int SW_RESTORE = 9;
            public const byte VK_TAB = 0x09;
            public const uint KEYEVENTF_KEYUP = 0x0002;

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);
            // 用 keybd_event 而不是 SendKeys：SendKeys 发给前台窗口、且从非 UI 线程调用有已知坑
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        }

        private void StartBagRefresh(int pid)
        {
            if (pid <= 0) { Log("未能自动切换窗口，请手动按 Tab 打开背包查看结果。"); return; }
            System.Threading.Thread t = new System.Threading.Thread(
                new System.Threading.ThreadStart(delegate { BagRefreshWorker(pid); }));
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 后台线程：把游戏窗口切到前台并按两次 Tab。**两次**的理由（必须保留）：
        /// 背包**若本来是打开的**，第一次 Tab 是「关闭」——关闭动作不会触发 `UIItemCell.Draw`，
        /// 也就不会走 `Definition` getter 自愈；第二次 Tab 才是「打开」，必定触发一次绘制。
        /// 两次可保证**无论初始开 / 关都至少发生一次「打开」**，且结束时停在打开状态，玩家正好看到结果。
        /// </summary>
        private void BagRefreshWorker(int pid)
        {
            string fail = "";
            try
            {
                System.Diagnostics.Process proc = System.Diagnostics.Process.GetProcessById(pid);
                proc.Refresh();
                IntPtr h = proc.MainWindowHandle;
                if (h == IntPtr.Zero)
                {
                    fail = "未能自动切换窗口，请手动按 Tab 打开背包查看结果。";
                }
                else
                {
                    Input32.ShowWindow(h, Input32.SW_RESTORE);
                    Input32.SetForegroundWindow(h);
                    System.Threading.Thread.Sleep(180);      // 等前台切换生效
                    Input32.keybd_event(Input32.VK_TAB, 0, 0, UIntPtr.Zero);
                    Input32.keybd_event(Input32.VK_TAB, 0, Input32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    System.Threading.Thread.Sleep(350);
                    Input32.keybd_event(Input32.VK_TAB, 0, 0, UIntPtr.Zero);
                    Input32.keybd_event(Input32.VK_TAB, 0, Input32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                _lastErrorNote = ex.GetType().Name;
                fail = "未能自动切换窗口，请手动按 Tab 打开背包查看结果。";
            }
            LogAsync(fail.Length > 0 ? fail : "已发送 Tab 打开背包。");
        }

        /// <summary>后台线程写日志：切回 UI 线程；句柄未就绪时静默丢弃（不影响写入结果）。</summary>
        private void LogAsync(string msg)
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action<string>(LogAsync), new object[] { msg }); return; }
                Log(msg);
            }
            catch (Exception)
            {
                // 窗口已关闭等边缘情况：丢弃这条提示即可
            }
        }


        /// <summary>
        /// 框1（背包物品）选中变化 ⇒ 把框2「目标种类」预置为**同一种类**（同中文名 + 同 ItemType），
        /// 方便玩家「同种类换星级」。定位不到时**不预置、不报错** —— 预置纯属便利功能，
        /// 任何异常都不得影响既有的物品数量修改逻辑。
        /// </summary>
        private void PresetKindForItem(string id)
        {
            try
            {
                if (_cmbKind == null || _kinds.Count == 0) return;
                if (id == null || id.Length == 0) return;
                int t;
                if (!TryGetItemType(id, out t)) return;
                string disp = DisplayName(id);
                if (disp == null || disp.Length == 0 || disp == id) return;
                string zh = StripSuffix(disp, ItemSuffix(id));
                for (int i = 0; i < _kinds.Count; i++)
                {
                    if (_kinds[i].Type != t || _kinds[i].Base != zh) continue;
                    if (_cmbKind.SelectedIndex == i) FillVariantList();
                    else _cmbKind.SelectedIndex = i;
                    return;
                }
            }
            catch (Exception)
            {
                // 预置失败：静默（不弹框、不打断既有选择逻辑）
            }
        }

        /// <summary>纯中文名：把 <see cref="DisplayName"/> 的结果剥掉 <see cref="ItemSuffix"/>。</summary>
        private static string StripSuffix(string disp, string suf)
        {
            if (suf == null || suf.Length == 0) return disp;
            if (disp.Length > suf.Length && disp.EndsWith(suf, StringComparison.Ordinal))
                return disp.Substring(0, disp.Length - suf.Length);
            return disp;
        }

        /// <summary>
        /// 框3 每项文本：星级词 + 红白（红白均 0 时不显示红白，与 <see cref="ItemSuffix"/> 同口径）。
        /// 星级取自 <c>_itemQuality</c> 的 [quality, qualityType] **运行时字段**，不是解析 id；
        /// 无星级 ⇒「无星级」；未知数值不猜测，原样标注。
        /// </summary>
        private string VariantText(string id)
        {
            int[] q;
            lock (_nameGate) { _itemQuality.TryGetValue(id, out q); }
            string star = "无星级";
            if (q != null && q.Length >= 2 && q[1] == 1)
            {
                if (q[0] == 1) star = "铜星";
                else if (q[0] == 2) star = "银星";
                else if (q[0] == 3) star = "金星";
                else star = "星级" + q[0].ToString(CultureInfo.InvariantCulture);
            }
            string skull = SkullText(id);
            if (skull.Length == 0) return star;
            return star + " " + skull;
        }

        /// <summary>取该 id 的 qualityType（0 = 不分星级 / 1 = 有星级）；数据缺失按 0 处理。</summary>
        private int QualityTypeOf(string id)
        {
            if (id == null || id.Length == 0) return 0;
            int[] q;
            lock (_nameGate) { _itemQuality.TryGetValue(id, out q); }
            if (q == null || q.Length < 2) return 0;
            return q[1];
        }

        /// <summary>取该 id 的 ItemType 数值（加锁读）；查不到返回 false。</summary>
        private bool TryGetItemType(string id, out int t)
        {
            t = 0;
            if (id == null || id.Length == 0) return false;
            lock (_nameGate) { return _itemType.TryGetValue(id, out t); }
        }

        /// <summary>框2 排序：先按显示文本（默认中文比较），文本相同再按组内首个 id（序数序）保证稳定。</summary>
        private static int CompareKindEntry(KindEntry a, KindEntry b)
        {
            int c = string.Compare(a.Text, b.Text, StringComparison.CurrentCulture);
            if (c != 0) return c;
            string x = (a.Ids.Count > 0) ? a.Ids[0] : "";
            string y = (b.Ids.Count > 0) ? b.Ids[0] : "";
            return string.CompareOrdinal(x, y);
        }

        /// <summary>框2 条目：一个「种类」= 一个 (中文名 + ItemType) 分组（必要时按 id 拆分，见 RebuildKindList）。</summary>
        private class KindEntry
        {
            public string Text;         // 上屏文本
            public string Base;         // 纯中文名（无星级/红白后缀、无类型与 id 标注）——框1→框2 预置时比对
            public int Type;            // ItemType 数值
            public List<string> Ids;    // 组内 id（升序）
            public KindEntry(string text, string baseName, int type, List<string> ids)
            {
                Text = text; Base = baseName; Type = type; Ids = ids;
            }
            public override string ToString() { return Text; }
        }

        /// <summary>框3 条目：一个物品 id + 它的变体文本（星级 / 红白）。</summary>
        private class VariantEntry
        {
            public string Id;
            public string Display;
            public VariantEntry(string id, string display) { Id = id; Display = display; }
            public override string ToString() { return Display; }
        }

        private void BuildFeatures()
        {
            int y = 96;
            foreach (string[] def in FeatureDefs)
            {
                Feature f = new Feature();
                f.Key = def[0];
                f.ItemId = def.Length > 4 ? def[4] : null;
                // 物品型功能（ItemId 非空）没有 GameRes 资源键;资源型 ResKey 缺省 = Key
                f.ResKey = f.ItemId != null ? null : (def.Length > 3 && def[3] != null ? def[3] : def[0]);
                f.Caption = def[1];
                f.DefaultLock = float.Parse(def[2], CultureInfo.InvariantCulture);

                CheckBox chk = new SteamCheckBox();
                chk.Text = f.Caption;
                chk.Left = 16; chk.Top = y; chk.Width = 270; chk.Height = 22;
                chk.ForeColor = STEAM_TEXT;
                // 【t24】底色的取法：自绘方框内部用它填充 ⇒ 与所在位置的渐变同色，
                // 视觉上等于透明，既不会出现系统默认的白色实心方块，也不会出现深色补丁。
                chk.BackColor = STEAM_PANEL;
                // 「背包里还没有」不再用禁用态表达（禁用态由系统画黑字、ForeColor 不生效、几乎不可见）；
                // 改为恒可点 + 点击时拦截并提示 —— 见 OnFeatureCheckedChanged。
                chk.CheckedChanged += new EventHandler(OnFeatureCheckedChanged);
                Controls.Add(chk);
                f.Chk = chk;

                f.Lbl = MakeLabel("-", 300, y + 2, 90, STEAM_OK, 9f, false);

                TextBox txt = new TextBox();
                txt.Left = 396; txt.Top = y; txt.Width = 120; txt.Height = 22;
                txt.Text = def[2];
                txt.BackColor = STEAM_PANEL;
                txt.ForeColor = Color.White;
                txt.BorderStyle = BorderStyle.FixedSingle;
                Controls.Add(txt);
                f.Txt = txt;

                _features.Add(f);
                y += 34;
            }
        }

        // ------------------------------------------------------------------
        // 定位
        // ------------------------------------------------------------------

        private void OnShown(object sender, EventArgs e)
        {
            // ① 清理上次异常退出留下的会话缓存目录（毫秒级）
            StartOrphanCleanup();
            // ② 进程相关 / 会变动数据的检索
            StartLocate();
            // ③ 启动预热：进程无关的纯数据（物品名表）扫描一次后落盘缓存
            StartWarmup();
        }
        private void OnRescanClick(object sender, EventArgs e) { StartLocate(); }

        /// <summary>
        /// 【T12】本次定位的触发源（**仅用于选择日志文案，不参与任何状态判定**）：
        ///   0 = 首次启动 / 用户点「刷新」；1 = 读档或返回主菜单后自动重定位；2 = 检测到（新）游戏进程自动重连。
        /// 为什么需要它：读档重定位成功时，若沿用与首次启动**逐字相同**的成功日志，会被 T7-F2 的
        /// 同文本去重静默丢弃 → 玩家最后看到的仍是过时的「尚未进入游戏」（t12 实证）。
        /// </summary>
        private volatile int _locateTrigger = 0;

        private void StartLocate() { StartLocate(0); }

        private void StartLocate(int trigger)
        {
            if (_scanBusy) { Log("正在读取游戏数据，请稍候再试。"); return; }
            // trigger < 0 ⇒ 保持上一次的触发源。用于「等待游戏开始」之后的连续自动重试：
            // 读档回主菜单期间的逐次重试必须继续算作「读档触发」，不能在成功时被误报成「重连」。
            if (trigger >= 0) _locateTrigger = trigger;
            _scanBusy = true;
            System.Threading.Thread t = new System.Threading.Thread(new System.Threading.ThreadStart(LocateWorker));
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>写一行日志（追加式，可回溯最近 LOG_MAX_LINES 条；旧行自动滚出）。</summary>
        private void Log(string s) { LogCore(s, false); }

        /// <summary>
        /// 【T12】状态转换类日志：**绕过同文本去重**，即使文本与既有行完全相同也必定输出。
        /// 适用：进程出生/死亡、读档回落、自动重定位完成这类"阶段切换"事件 ——
        /// 它们是玩家判断"修改器是否跟上游戏"的唯一线索，不能被去重静默吞掉。
        /// （普通结果型日志仍走 Log() 去重，见下。）
        /// </summary>
        private void LogEvent(string s) { LogCore(s, true); }

        /// <summary>
        /// 【2026-09-26 收尾轮】取证类日志：**只在证据模式（<c>GK2TRAINER_EVIDENCE=1</c>）下上屏**。
        /// 背景：日志栏是**玩家可见**区域，但历史多轮为排障陆续新增的「别名表 / 词表 / 缓存校验 /
        /// 内部自检 PASS」一类明细直接走了 <see cref="Log(string)"/> —— 实机抓取到一次启动 7 行日志里
        /// 3 行是内部取证（最长一行 100+ 字符），违反 t4 确立的「界面与日志一律玩家语言」纪律。
        /// 故统一收敛到本出口：默认不上屏（玩家看不到），排障时开同一个既有开关即可全部复现。
        /// 关闭时字符串拼接仍会发生（代价为一次启动几十次短拼接，可忽略），但**不写日志栏、不占 30 行缓冲**。
        /// </summary>
        private void EvidenceLog(string s) { if (EvidenceMode) Log(s); }

        private void LogCore(string s, bool bypassDedup)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, bool>(LogCore), new object[] { s, bypassDedup }); return; }
            // 【T7-F2 / T8-F2 修复 + T12 修正】同文本去重（不只相邻），**但状态转换类日志例外**。
            // 背景（t7 评审实测）：同一轮流程反复刷新时，「背包共 N 件物品，数据已同步。」这类
            // 结果行会以**完全相同的文本**每轮各追加一条 —— 连续 6 轮后 18 行中有 12 行是重复对，
            // LOG_MAX_LINES=30 的环形缓冲被同质行填满，关键行被挤出，排障可回溯性退化。
            // T12 修正：去重**只作用于普通结果型日志**；状态转换类事件（阶段切换）一律经
            // LogEvent() 输出，因此不会出现"重定位成功却没有任何反馈"的情况。
            if (!bypassDedup)
            {
                for (int i = 0; i < _logLines.Count; i++)
                    if (string.Equals(_logLines[i], s, StringComparison.Ordinal)) return;
            }
            _logLines.Add(s);
            while (_logLines.Count > LOG_MAX_LINES) _logLines.RemoveAt(0);
            _txtLog.Lines = _logLines.ToArray();
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }

        /// <summary>
        /// 【T7-F4 修复 + T10-F2 加固】把最近一次异常写到**本次会话的临时目录**，给
        /// <see cref="_lastErrorNote"/> 一个真实读出口（原实现只写不读 → C-4「异常可见化」不可见）。
        /// 安全边界（T10-F2 起为**双重保险**）：
        ///   · <see cref="_lastErrorNote"/> 只承载**异常类型名**（如 <c>InvalidOperationException</c>），
        ///     调用点一律不写 <c>ex.Message</c> —— 消息文本可能含十进制数值/偏移，一律不落盘；
        ///   · 落盘前再做一次**强过滤**：十六进制（<c>0x…</c>）与**连续 4 位以上十进制数字**
        ///     一并替换为 <c>#</c>，覆盖将来若有人误改回写消息的情况；
        ///   · 只写 %TEMP%\GK2Trainer\&lt;会话&gt;\last_error.txt（与词表缓存同一会话目录，退出即清理）；
        ///   · 该文件**不参与定位**、不被读取，只供排障查看；
        ///   · 任何失败静默忽略（诊断本身绝不影响功能）。
        /// 上屏部分仍保持玩家化：只提示「未成功，请重试」，不向玩家暴露类型名。
        /// </summary>
        private void SaveErrorNote()
        {
            try
            {
                string note = _lastErrorNote;
                if (string.IsNullOrEmpty(note)) return;
                // 强过滤：十六进制地址 + 长十进制数字（≥4 位）一律抹除，确保任何情况下不含地址/偏移数值
                string safe = System.Text.RegularExpressions.Regex.Replace(
                    note, @"0[xX][0-9A-Fa-f]+", "#");
                safe = System.Text.RegularExpressions.Regex.Replace(safe, @"\d{4,}", "#");
                string dir = System.IO.Path.Combine(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GK2Trainer"),
                    NameCacheStore.SessionTag());
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, "last_error.txt"), safe, new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        private void LocateWorker()
        {
            System.Diagnostics.Stopwatch swRefresh = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 【t19】冷刷新期间一律视为未就绪：在拿到「新锚 + 新选组」之前，
                // 界面上的锁定项与「应用」按钮置灰，避免用户拿旧数据误操作（勾了没效果）。
                // 注意：**只降状态用于置灰，不清 _groupSelected** ——
                // 后者是「上一次选组成功」的记忆，热路径要靠它判断能否复用；
                // 若在这里清掉，每次点刷新都会被直接重新定位（实测回归）。
                _lifeState = LC_NOT_READY;
                RefreshFeatureAvailability();
                SetStatus("正在连接游戏……", STEAM_WAIT);
                int pid = FindGamePid();
                if (pid == 0)
                {
                    _lifeState = LC_NO_PROCESS;
                    SetStatus("等待游戏启动……", STEAM_WAIT);
                    return;   // 新进程出现时由 MonitorLifecycle 自动重连
                }
                // 【t3 C-5/A-4】三态探测：权限不足导致的「枚举失败」不能当成「版本不支持」，
                // 否则会出现「状态栏报不支持、界面数据却可用」的自相矛盾（t3 实测观测 1）。
                ProcessMemory.ModuleProbe monoProbe = ProcessMemory.ProbeModule(pid, "mono-2.0-bdwgc.dll");
                if (monoProbe == ProcessMemory.ModuleProbe.Absent)
                    monoProbe = ProcessMemory.ProbeModule(pid, "mono.dll");
                if (monoProbe == ProcessMemory.ModuleProbe.Absent)
                {
                    // 确实没有 Mono 运行时 → 可能是 IL2CPP 后端。面向玩家只说「版本不受支持」；
                    // 【A-4】必须同时置退避并记日志，否则 ③ 分支会以 1.5 s 周期反复枚举模块。
                    _lifeState = LC_NOT_READY;
                    _notReadyRetries++;
                    _nextNotReadyRetryUtc = DateTime.UtcNow.AddMilliseconds(FAILED_RETRY_MS);
                    SetStatus("游戏版本不受支持，请确认是正式版《守墓人 2》。", STEAM_WARN);
                    Log("当前游戏版本不受支持，本修改器仅支持正式版《守墓人 2》。");
                    return;
                }
                if (monoProbe == ProcessMemory.ModuleProbe.EnumerationFailed)
                {
                    // 读不到模块列表（多为未以管理员身份运行）→ 不轻易判死：
                    // 继续以只读方式连接，成败交给后续逐级结构校验裁定（宁可不定位，不可选错）。
                    Log("暂时无法读取游戏运行库信息，将以实际数据校验为准。");
                }
                if (!_mem.Open(pid))
                {
                    SetStatus("无法连接游戏，请右键修改器选择「以管理员身份运行」。",
                        STEAM_WARN);
                    return;
                }
                // 进程是否变化（PID 不变 = 同一次游戏启动内的再次刷新）
                bool pidChanged = (_pid != pid);
                bool processChanged = (_loc == null) || pidChanged;
                _pid = pid;

                // 词表索引只在进程重连时作废（进程无关数据）；存档锚在换档/失效时由校验触发重解析
                if (_loc != null && processChanged) _loc.ClearLocalizedNameCache();
                if (processChanged)
                {
                    // 【P0-⑤】同一游戏进程内复用上次标定好的原子 vtable：vtable 在同一次进程运行内
                    // 稳定（读档/换档不改变它），因此换档触发的再次冷刷新可省掉一次全堆自举。
                    // 游戏重启（PID 变）时旧 vtable 必然失效 → 丢弃提示，重新自举。
                    if (pidChanged) _atomVTableHint = 0;
                    _loc = new GameResLocator(_mem, _atomVTableHint);
                }
                // 【t30 · R1①】`_loc` 可能晚于预热线程建立（缓存命中路径下预热先返回）
                //   ⇒ 这里再挂一次补建。幂等：已在建 / 别名表已就绪时立即返回，不重复扫描。
                if (_namesReady) StartLocAliasBackfill();

                // ---------------- 【热路径】锚可复用 **且资源组已选定** → 不做任何全堆扫描 ----------------
                // 只按缓存地址重读数值（微秒级）；不可复用时往下走冷路径**重新解析**。
                // 【P0a 语义边界】IsAnchorStillValid() = 身份 ∧ 结构，此处用作**复用前的廉价校验**：
                //   两者都成立才敢直接复用缓存（结构不可读时复用会读到不可信数据）；
                //   失败后果是冷路径重新解析，**不是**换档重载 —— 换档判定在 MonitorLifecycle ②′。
                // 【t19 修正】资源组未选定时**直接重新定位** —— 这是用户「点刷新即可自救」的
                // 唯一路径：旧版只看锚是否有效，锚一通就永远走热路径，选组再也不执行。
                bool anchorValid = !processChanged && _loc.IsAnchorStillValid();
                bool inGame = _loc.IsInGameSession();   // 主菜单下引用链全通(旧档残留),必须显式排除
                if (anchorValid && _groupSelected && !inGame)
                {
                    // 主菜单下点刷新:不回落到热路径(会显示旧档数据),走冷路径的「未开始游戏」退避
                    anchorValid = false;
                    Log("尚未进入游戏，进入游戏后会自动读取。");
                }
                if (anchorValid && !_groupSelected)
                {
                    Log("正在读取当前存档……");
                }
                if (anchorValid && _groupSelected && inGame)
                {
                    if (_playerAtoms != null)
                    {
                        for (int i = 0; i < _playerAtoms.Count; i++)
                        {
                            GameResAtom a = _playerAtoms[i];
                            if (a == null || a.Address == 0) continue;
                            a.Value = _mem.ReadFloat(a.Address + GameResLocator.ATOM_VALUE);
                        }
                    }
                    List<GameResLocator.ItemRef> hotItems = _loc.ListInventoryItems();
                    EnsureItemNames(hotItems);
                    SetInventory(hotItems);
                    // 热路径成功：状态回到就绪并恢复交互（开头为置灰曾降到 NOT_READY）
                    _lifeState = LC_READY;
                    RefreshFeatureAvailability();
                    swRefresh.Stop();
                    SetStatus("已连接游戏：背包 " + hotItems.Count + " 件物品，数据已同步。", STEAM_TEXT);
                    // 【T12】状态转换类完成反馈用专属文案 + 绕过去重：
                    //  触发源 1（读档/回主菜单后自动重定位）与 2（检测到游戏进程自动重连）时，
                    //  若沿用首次启动的原文本会被去重吞掉 → 玩家看不到"已恢复"。
                    LogCompletion(hotItems.Count);
                    return;
                }

                // ---------------- 【冷路径】完整现场解析（启动 / 换档 / 地址校验失败）----------------
                // 【主菜单闸】Instance 已记录且 gameState=MainMenu ⇒ 引用链即便全通也只是旧档残留,
                // 直接等待,不做任何扫描(含 FindAllAtoms 的 ~5s)。
                if (_loc.GetGameState() == 0)
                {
                    _lifeState = LC_NOT_READY;
                    SetStatus("等待游戏开始", STEAM_WAIT);
                    Log("尚未进入游戏，进入游戏后会自动读取。");
                    SetInventory(new List<GameResLocator.ItemRef>());
                    return;
                }
                _loc.InvalidateInventory();
                SetStatus("正在读取当前存档，请稍候……", STEAM_WAIT);

                bool anchorOk = _loc.RefreshCurrentSaveAnchor();
                if (anchorOk)
                {
                    // 【主菜单检测 2026-09-24】主菜单下引用链全通(旧档残留不释放),
                    // 锚解析与资源组都会"成功"——但 gameState != InGame ⇒ 数据是旧档的,不得就绪。
                    if (!_loc.IsInGameSession())
                    {
                        _lifeState = LC_NOT_READY;
                        _notReadyRetries++;
                        int dmenu = NotReadyDelayMs(_notReadyRetries);
                        _nextNotReadyRetryUtc = DateTime.UtcNow.AddMilliseconds(dmenu);
                        SetStatus("等待游戏开始", STEAM_WAIT);
                        Log("尚未进入游戏，进入游戏后会自动读取。");
                        SetInventory(new List<GameResLocator.ItemRef>());
                        return;
                    }
                    // 【t19 修正】锚链通过**不等于**就绪：还要等资源组选组唯一命中。
                    // 旧版在此直接置 LC_READY，于是「锚通但选组失败」被误判为就绪 →
                    // 转入热路径后再无选组机会 → 功能区 8 项永久 [未找到]（只能重启修改器）。
                    _lifeState = LC_NOT_READY;
                    _notReadyRetries = 0;
                    _nextNotReadyRetryUtc = DateTime.MinValue;
                    Log("已找到当前存档，正在确认数据……");
                }
                else
                {
                    // 区分「尚未就绪」与「真失败」：
                    // 实测（2026-09-23 游戏重启场景）启动后约 77 s 时锚链仍不通过、
                    // 更晚（约 2 分钟后）即成功 —— 启动早期的失败属「未就绪」，
                    // 归入「等待游戏开始」并由监控自动退避重试；只有反复重试仍不通过，
                    // 才升级为「拒绝定位（宁可不定位不可选错）」并输出完整日志。
                    ClearAnchoredData();
                    // 【面向玩家】原先这里把锚诊断串（static+0x28 / slot / 引用链…）拼进状态栏与日志，
                    // 已按「玩家看得懂」要求移除；诊断数据仍由 _loc.AnchorLog 在内存中保留，供排障工具读取。
                    // 【2026-09-24】启动期(从未进档,static+0x28 空)重试无意义且与游戏加载抢资源:
                    // 间隔固定 10 s;曾进档(读档加载中,槽指向新 PD)保持短序列以便快速恢复。
                    bool startupPhase = true;
                    if (_loc.AnchorStaticData > 0x10000)
                    {
                        long slot = _mem.ReadLong(_loc.AnchorStaticData + GameResLocator.STATIC_SLOT_PLAYERDATA);
                        if (slot > 0x10000) startupPhase = false;
                    }
                    if (_notReadyRetries < NOT_READY_MAX_RETRIES)
                    {
                        _lifeState = LC_NOT_READY;
                        _notReadyRetries++;
                        int delay = startupPhase ? 10000 : NotReadyDelayMs(_notReadyRetries);
                        _nextNotReadyRetryUtc = DateTime.UtcNow.AddMilliseconds(delay);
                        // 面向玩家：只说「还在加载、稍后自动重试」，诊断细节不再上屏
                        SetStatus("等待游戏开始", STEAM_WAIT);
                        Log("游戏还在加载，稍后会自动读取。");
                    }
                    else
                    {
                        // 【t3 A-1 修复】真失败进入独立失败态，绝不复用 LC_READY：
                        // 置 LC_READY 会被「锚已失效 → 立即重定位」分支命中并清零重试计数，
                        // 变成永不停止的重启循环；LC_FAILED 下写内存判据恒为 false。
                        //
                        // 【T13-F1】本行是**状态转换的终态**（NOT_READY → FAILED），必须绕过去重
                        // 用 LogEvent —— 否则若同一文本曾出现过，玩家将看不到「已经停下来保护存档」
                        // 这个关键结论。
                        //
                        // 与之相对，NOT_READY 期间的日志**有意保持走 Log() 去重**，按语义分两类
                        // （T17-F4：措辞按实际分类精确化，原先笼统写「7 处退避过渡」并不准确）：
                        //   · **退避过渡**（2 处，每次退避重试都会重新输出）：
                        //       「游戏还在加载，稍后会自动读取。」
                        //       「游戏数据尚未加载完成，稍后会自动读取。」
                        //     → 必须去重，否则退避重试会把日志栏刷屏（正是 T7-F2 要修的噪声形态）。
                        //   · **中间态 / 过程提示**（5 处，仅在"停留在该状态"时重复输出）：
                        //       「尚未进入游戏，进入游戏后会自动读取。」×3
                        //       「正在读取当前存档……」、「已找到当前存档，正在确认数据……」
                        //     → 同样去重：重复内容对玩家无增量信息，当前态由状态栏实时反映；
                        //       真正需要"必定可见"的是转换两端（阶段切换 + 终态），那两类已走 LogEvent。
                        // 分工原则：**状态栏负责 NOT_READY 中间态；日志层只保证转换两端与终态可见。**
                        _lifeState = LC_FAILED;
                        _nextNotReadyRetryUtc = DateTime.UtcNow.AddMilliseconds(FAILED_RETRY_MS);
                        LogEvent("未能确认当前存档，已暂停修改以保护存档，请重新进入游戏。");
                        SetStatus("未能确认当前存档，请重新进入游戏后再点「刷新」。", STEAM_WARN);
                    }
                    SetInventory(new List<GameResLocator.ItemRef>());
                    return;
                }

                // 【2026-09-24】资源组定位 = 结构链直达（唯一路径）:PlayerData+0x80 → GameRes+0x10
                // → resValues 就是游戏自己的玩家资源容器,天然=当前存档、毫秒级、读档安全。
                // （旧「全堆扫描+地址连续聚组+闭包选组」已移除:读档场景实测 atom 不再地址连续,
                //   该判据 0 命中 → 永不就绪无限循环;旧源码留档 __backup_resgroup_20260924。
                //   结构链失败（游戏半初始化/未来结构变化）一律回落「等待游戏开始」退避重试——
                //   宁可不定位,不可选错。）
                List<GameResAtom> chainGroup = _loc.BuildResGroupViaAnchor();
                bool chainHasMoney = false, chainHasEnergy = false;
                foreach (GameResAtom g in chainGroup)
                {
                    if (g.TypeName == "money") chainHasMoney = true;
                    else if (g.TypeName == "energy") chainHasEnergy = true;
                }
                if (!(chainGroup.Count >= 16 && chainHasMoney && chainHasEnergy))
                {
                    _lifeState = LC_NOT_READY;
                    _notReadyRetries++;
                    int d2 = NotReadyDelayMs(_notReadyRetries);
                    _nextNotReadyRetryUtc = DateTime.UtcNow.AddMilliseconds(d2);
                    SetStatus("等待游戏开始", STEAM_WAIT);
                    Log("游戏数据尚未加载完成，稍后会自动读取。");
                    SetInventory(new List<GameResLocator.ItemRef>());
                    return;
                }
                ApplyGroupList(chainGroup);
                _groupSelected = true;
                _atomVTableHint = _loc.GameResAtomVTable;   // 【P0-⑤】记住本次标定的 vtable
                Log("已读取当前存档的数据（共 " + chainGroup.Count + " 项）。");
                _lifeState = LC_READY;                 // ★ 锚链通过 且 选组唯一命中 → 才置就绪

                // 资源就绪后顺带载入玩家背包（Item 层）
                List<GameResLocator.ItemRef> items = _loc.ListInventoryItems();
                // 中文物品名：整表已就绪 / 会话缓存 / 实时内存提取（都不会重复付 4 秒）
                EnsureItemNames(items);
                SetInventory(items);

                // 【科学】分解产物存放在研究台(survey_wgo)等 WGO 容器,不在主背包。
                // 定位必须在本路径唯一的 RefreshFeatureAvailability() 之前完成,
                // 使科学与其它功能区条目同批点亮（用户实测:晚一拍会单独灰几十 ms）。
                // 成功/失败均不单独输出日志:失败时条目置灰并显示「(需要至少获得1点)」。
                bool inBag = false;
                foreach (GameResLocator.ItemRef r in items) if (r.ItemId == "science") { inBag = true; break; }
                if (!inBag && items.Count > 0)
                {
                    long sci = 0;
                    try { sci = _loc.FindItemViaWorld("science"); } catch { }     // 结构链(唯一路径)
                    if (sci != 0) System.Threading.Interlocked.Exchange(ref _sciItemAddr, sci);
                }

                RefreshFeatureAvailability();          // 就绪 → 恢复交互（全部条目一次性点亮,含科学）

                swRefresh.Stop();
                SetStatus("刷新完成：当前存档数据已同步。", STEAM_TEXT);
                // 【T12】就绪完成反馈（按触发源选文案；读档/重连场景绕过去重）
                LogCompletion(items.Count);
            }
            catch (Exception ex)
            {
                // 【t3 C-4】保留异常类型名（排障可用），玩家可见文案保持简洁；
                // 同时清掉半初始化的定位数据，避免「状态栏报错但界面仍显示旧数据」。
                _lastErrorNote = ex.GetType().Name;   // 【T10-F2】只记类型名，绝不写进消息文本
                SaveErrorNote();          // 【T7-F4】诊断出口：会话临时目录（地址已过滤）
                ClearAnchoredData();
                _gameProc = null;
                SetProgress(false);
                SetStatus("刷新出错，请重新点「刷新」。", STEAM_WARN);
                Log("读取游戏数据时出错，请重新点「刷新」。");
            }
            finally
            {
                _scanBusy = false;
            }
        }

        /// <summary>
        /// 【T12】就绪完成反馈（热/冷路径共用）。按**本次定位的触发源**选择文案：
        ///   · 触发源 0（首次启动 / 用户点「刷新」）→ 沿用原文本，仍走去重（保持 F2 修复成果：
        ///     连续多轮刷新不堆积重复行）；
        ///   · 触发源 1（读档 / 返回主菜单后自动重定位）→ **专属文案**「已回到游戏，数据已更新。」
        ///     与回落提示「检测到读档或返回主菜单，正在重新读取当前存档……」形成闭环，
        ///     并经 LogEvent **绕过去重**（否则会被首次启动的同文本行吞掉）；
        ///   · 触发源 2（检测到游戏进程自动重连 / 等待游戏开始后的自动就绪）→ 专属文案
        ///     「已连接游戏，数据已同步。」同样绕过去重 —— 用词与状态栏
        ///     「已连接游戏：背包 N 件物品，数据已同步。」保持一致（T13-F3：同一场景、同一套说法）。
        /// **本方法只决定日志文案，不参与任何状态判定**（保持状态机语义不变）。
        /// </summary>
        private void LogCompletion(int itemCount)
        {
            string tail = (itemCount > 0) ? ("背包 " + itemCount + " 件物品。") : "请按 Tab 打开一次背包，再点「刷新」。";
            int trig = _locateTrigger;
            if (trig == 1)
                LogEvent("已回到游戏，数据已更新。" + tail);
            else if (trig == 2)
                LogEvent("已连接游戏，数据已同步。" + tail);
            else if (itemCount > 0)
                Log("刷新完成：背包 " + itemCount + " 件物品。");
            else
                Log("没有找到背包数据，请按 Tab 打开一次背包，再点「刷新」。");
        }

        private void SetStatus(string text, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, Color>(SetStatus), new object[] { text, color }); return; }
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        private void SetProgress(bool visible)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetProgress), new object[] { visible }); return; }
            _progress.Visible = visible;
            _progress.Style = visible ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        }

        /// <summary>
        /// 选组：**首选「当前存档锚」**（MainGame::&lt;PlayerData&gt; 现场解析，零硬编码）——
        /// 该组的原子必须被当前存档对象闭包引用，且恰好 1 组命中才采信。
        /// 锚不可用或命中不唯一 → 回退后备启发式，并在日志明示「未确证」。
        ///
        /// 已删除的判据（2026-09-23 实测证伪）：
        ///   ① HUD 富文本交叉校验 —— 既选错（用户与 verifier 双重实测）
        ///      又昂贵（3.1 s 全堆扫富文本），已彻底移除；
        ///   ② 「最新投票者地址 / 票数 / 容量」三级比较 —— 读档后票数与基址都会落到旧对象上。
        /// </summary>
        /// <summary>直接以给定资源组生效（结构链路径用,不再依赖扫描聚组的组下标）。</summary>
        private void ApplyGroupList(List<GameResAtom> player)
        {
            _playerAtoms = player;
            Dictionary<string, GameResAtom> map = new Dictionary<string, GameResAtom>();
            for (int i = 0; i < player.Count; i++)
                if (!map.ContainsKey(player[i].TypeName)) map[player[i].TypeName] = player[i];
            _res = map;
            RefreshFeatureAvailability();
        }

        /// <summary>在背包物品缓存里找指定 itemId 的 Item 地址;缓存缺失/过期时按需补扫（毫秒级,仅读背包 List）。</summary>
        private long FindBagItemAddr(string itemId)
        {
            if (_loc == null || !_mem.IsOpen || itemId == null) return 0;
            long a = LookupBagCache(itemId);
            if (a != 0) return a;
            if (_scanBusy) return 0;
            if (_bagItemCache != null)
            {
                // 缓存里没有该物品:每 ~40 轮（10 s）补扫一次,等玩家真正获得它
                if (++_bagRescanCounter < 40) return 0;
                _bagRescanCounter = 0;
            }
            try
            {
                List<GameResLocator.ItemRef> items = _loc.ListInventoryItems();
                if (items != null && items.Count > 0) _bagItemCache = items;
            }
            catch { }
            return LookupBagCache(itemId);
        }

        private long LookupBagCache(string itemId)
        {
            if (_bagItemCache == null) return 0;
            foreach (GameResLocator.ItemRef r in _bagItemCache)
                if (r != null && r.ItemId == itemId && r.Address != 0) return r.Address;
            return 0;
        }

        private void RefreshFeatureAvailability()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefreshFeatureAvailability)); return; }
            // 【t19】非就绪态（等待游戏启动 / 等待游戏开始 / 选组未完成）一律禁用交互，
            // 避免用户「勾了没效果」；就绪后才按资源是否命中逐项启用。
            bool interactive = (_lifeState == LC_READY) && _groupSelected && _res.Count > 0;
            if (_btnApplyOnce != null) _btnApplyOnce.Enabled = interactive;
            // 【D-8/R-5 修复】物品「应用」按钮与输入框统一在此判定（含「背包是否为空」），
            // 避免 SetInventory 单独点亮造成「按钮可点但状态尚未就绪」的旁路。
            bool itemInteractive = interactive && _invItems != null && _invItems.Count > 0;
            if (_btnItemApply != null) _btnItemApply.Enabled = itemInteractive;
            if (_txtItemLock != null) _txtItemLock.Enabled = itemInteractive;
            foreach (Feature f in _features)
            {
                GameResAtom a;
                // 物品型功能（如 科学/science）：背包里存在该物品才可用（数量 >0 时游戏才创建 Item）
                if (f.ItemId != null)
                {
                    bool hasItem = FindBagItemAddr(f.ItemId) != 0
                        || System.Threading.Volatile.Read(ref _sciItemAddr) != 0;
                    f.Missing = !hasItem;
                    f.Usable = hasItem && interactive;
                    // 【t24】Chk 恒为 Enabled：禁用态由系统用黑字渲染、ForeColor 不生效，
                    // 在深色渐变上几乎不可见（用户实机反馈的根因）。可读性改由 ForeColor +
                    // 点击拦截（OnFeatureCheckedChanged）表达，写入安全不变式不受影响。
                    f.Chk.Enabled = true;
                    f.Txt.Enabled = f.Usable;
                    if (!hasItem)
                    {
                        _suppressChk = true; f.Chk.Checked = false; _suppressChk = false;
                        f.Chk.Text = f.Caption + "（背包里还没有，先获得 1 个再改）";
                        f.Chk.ForeColor = STEAM_WARN;          // 醒目警示橙，与可用态一眼可分
                        f.Lbl.Text = "-";
                        f.Lbl.ForeColor = STEAM_TEXT;
                    }
                    else
                    {
                        f.Chk.Text = f.Caption;
                        f.Chk.ForeColor = STEAM_TEXT;
                        f.Lbl.ForeColor = STEAM_OK;
                    }
                    continue;
                }
                bool has = _res.TryGetValue(f.ResKey, out a);
                f.Missing = !has;
                f.Usable = has && interactive;
                f.Chk.Enabled = true;
                f.Txt.Enabled = f.Usable;
                if (!has)
                {
                    _suppressChk = true; f.Chk.Checked = false; _suppressChk = false;
                    f.Chk.Text = f.Caption + "（未读取到）";
                    f.Chk.ForeColor = STEAM_WARN;
                    f.Lbl.Text = "-";
                    f.Lbl.ForeColor = STEAM_TEXT;
                }
                else
                {
                    f.Chk.Text = f.Caption;
                    f.Chk.ForeColor = STEAM_TEXT;
                    f.Lbl.ForeColor = STEAM_OK;
                }
            }
        }

        /// <summary>
        /// 【t24】功能项勾选拦截：条目不可用时（未就绪 / 未读取到 / 背包里还没有），
        /// 勾选动作立即还原并给出玩家可读提示 —— 取代原先的「禁用态置灰」
        /// （禁用态由系统用黑字渲染、ForeColor 不生效，在深色渐变上几乎不可见）。
        /// **只影响 UI 勾选**：真正的写入保护不变，仍在 OnTick 的 canWrite / f.Missing 判断里。
        /// </summary>
        private void OnFeatureCheckedChanged(object sender, EventArgs e)
        {
            if (_suppressChk) return;
            CheckBox c = sender as CheckBox;
            if (c == null || !c.Checked) return;
            for (int i = 0; i < _features.Count; i++)
            {
                Feature f = _features[i];
                if (f.Chk != c) continue;
                if (f.Usable) return;                       // 可用 → 正常勾选锁定
                _suppressChk = true; c.Checked = false; _suppressChk = false;
                if (f.Missing)
                {
                    Log(f.ItemId != null
                        ? f.Caption + "：背包里还没有这个物品，先在游戏里获得 1 个再改。"
                        : f.Caption + "：暂时没读取到这项数据，请点「刷新」后再试。");
                }
                else
                {
                    Log("还没读取到游戏数据，请先进入游戏并点「刷新」。");
                }
                return;
            }
        }

        // ------------------------------------------------------------------
        // 物品数量（Item 层：玩家背包内实际存在的物品）
        //   ItemCount 链路已被证伪，UI 不再使用；数量取自 Item + 0x40。
        // ------------------------------------------------------------------
        /// <summary>把一批库存物品填入下拉（可在后台线程调用）。</summary>
        private void SetInventory(List<GameResLocator.ItemRef> items)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<List<GameResLocator.ItemRef>>(SetInventory), new object[] { items });
                return;
            }
            _invItems = items;

            string keep = _selItemId;
            long keepAddr = _selItemAddr;      // 【必修缺陷修复】优先按地址保留选中（同 id 多件时才分得清）
            _suppressSel = true;
            _cmbItemId.BeginUpdate();
            _cmbItemId.Items.Clear();
            int keepIdx = -1, keepIdxById = -1;
            for (int i = 0; i < items.Count; i++)
            {
                string d = DisplayName(items[i].ItemId) + " × " + items[i].Count;
                _cmbItemId.Items.Add(new InvEntry(items[i].ItemId, d, items[i].Address));
                if (keepAddr != 0 && items[i].Address == keepAddr) keepIdx = i;
                else if (keep.Length > 0 && items[i].ItemId == keep && keepIdxById < 0) keepIdxById = i;
            }
            if (keepIdx >= 0) _cmbItemId.SelectedIndex = keepIdx;
            else if (keepIdxById >= 0) _cmbItemId.SelectedIndex = keepIdxById;
            _cmbItemId.EndUpdate();
            if (_cmbItemId.SelectedIndex < 0 && items.Count > 0) _cmbItemId.SelectedIndex = 0;
            _suppressSel = false;

            // 按钮/输入框可用性统一由 RefreshFeatureAvailability 决定（含就绪态判定），
            // 不再在此单独点亮 —— 那是「状态未就绪却能点应用」的旁路来源（t3 R-5/D-8）。
            RefreshFeatureAvailability();
            OnItemSelectionChanged(null, null);
            // 【方案C】种类/星级是**全库**数据，与本次背包内容无关；但「刷新 / 换档 / 预热回填」
            // 都会经过这里 ⇒ 挂在此处可覆盖全部数据到齐的时机（版本未变时零成本返回）。
            EnsureKindList();
        }

        /// <summary>下拉选中项变化：刷新「当前数量」显示。</summary>
        /// <summary>「修改为」输入框外框：自绘 1px #3D4450，与下拉控件/按钮同边框语言。</summary>
        private void OnLockBoxPaint(object sender, PaintEventArgs e)
        {
            Panel p = sender as Panel;
            if (p == null) return;
            using (Pen b = new Pen(STEAM_BORDER))
                e.Graphics.DrawRectangle(b, 0, 0, p.Width - 1, p.Height - 1);
        }

        /// <summary>
        /// 自绘下拉主体：与右侧箭头**同底色、同 1px 边框、同高度**。
        /// 四状态语言：常态（#23262E + #3D4450）· 聚焦/展开中（边框提亮 #8B929A）· 悬停（底色 #2A475E + 边框提亮）。
        /// </summary>
        private void OnDropFacePaint(object sender, PaintEventArgs e)
        {
            Panel p = sender as Panel;
            if (p == null) return;
            bool hot = _dropFaceHot;
            using (SolidBrush bg = new SolidBrush(hot ? STEAM_GRAD_TOP : STEAM_PANEL))
                e.Graphics.FillRectangle(bg, p.ClientRectangle);
            // 左/上/下三边（右边由箭头按钮的左边框接管，两条边拼成一个完整矩形，缝处即分隔线）
            using (Pen b = new Pen((hot || _dropFaceActive) ? STEAM_TEXT_DIM : STEAM_BORDER))
            {
                e.Graphics.DrawLine(b, 0, 0, p.Width - 1, 0);
                e.Graphics.DrawLine(b, 0, p.Height - 1, p.Width - 1, p.Height - 1);
                e.Graphics.DrawLine(b, 0, 0, 0, p.Height - 1);
            }
            InvEntry sel = _cmbItemId.SelectedItem as InvEntry;
            string txt = (sel != null && sel.Display != null && sel.Display.Length > 0)
                ? sel.Display
                : (_cmbItemId.Text == null ? "" : _cmbItemId.Text);
            // 空列表（游戏未连接 / 未进档 / 背包为空）时给占位文本：避免一个空白框看不出状态，
            // 也让「无数据」与「有数据」一眼可分（占位用次级色，对比度 4.81:1 仍达标）。
            Color fg = Color.White;
            if (txt.Length == 0) { txt = "（未读取到物品）"; fg = STEAM_TEXT_DIM; }
            TextRenderer.DrawText(e.Graphics, txt, this.Font,
                new Rectangle(6, 0, p.Width - 12, p.Height), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        private void OnDropFaceEnter(object sender, EventArgs e)
        {
            _dropFaceHot = true;
            if (_pnlDropFace != null) _pnlDropFace.Invalidate();
            if (_pnlDropArrow != null) _pnlDropArrow.Invalidate();
        }

        private void OnDropFaceLeave(object sender, EventArgs e)
        {
            _dropFaceHot = false;
            if (_pnlDropFace != null) _pnlDropFace.Invalidate();
            if (_pnlDropArrow != null) _pnlDropArrow.Invalidate();
        }

        /// <summary>点击主体 = 展开下拉列表（与点箭头同义，仅转发，不参与取值逻辑）。</summary>
        private void OnDropFaceClick(object sender, EventArgs e)
        {
            _cmbItemId.Focus();
            _cmbItemId.DroppedDown = true;
        }

        /// <summary>ComboBox 焦点 / 展开状态变化 → 主体与箭头同步重绘（保证四态语言一致）。</summary>
        private void OnDropStateChanged(object sender, EventArgs e)
        {
            _dropFaceActive = _cmbItemId.Focused || _cmbItemId.DroppedDown;
            if (_pnlDropFace != null) _pnlDropFace.Invalidate();
            if (_pnlDropArrow != null) _pnlDropArrow.Invalidate();
        }

        /// <summary>
        /// 自绘下拉按钮：完整 1px 边框 + **加粗**白色 ▼（t27：11×8 → 14×10，用户反馈"再加粗一点"）。
        /// 底色与边框随四态与主体保持一致。
        /// </summary>
        private void OnDropArrowPaint(object sender, PaintEventArgs e)
        {
            Panel p = sender as Panel;
            if (p == null) return;
            bool act = (_dropFaceHot || _dropFaceActive);
            using (SolidBrush bg = new SolidBrush(act ? STEAM_GRAD_TOP : STEAM_PANEL))
                e.Graphics.FillRectangle(bg, p.ClientRectangle);
            using (Pen border = new Pen(act ? STEAM_TEXT_DIM : STEAM_BORDER))
                e.Graphics.DrawRectangle(border, 0, 0, p.Width - 1, p.Height - 1);
            int cx = p.Width / 2, cy = p.Height / 2;
            // 【T25-4 修复】保存旧的 SmoothingMode 并在绘制结束后恢复：原实现只设不还原，
            // 会污染同一个 Graphics 上后续的绘制（本文件其它自绘都做了保存/恢复）。
            // 只改绘图状态管理，绘制内容与观感一字不变。
            System.Drawing.Drawing2D.SmoothingMode old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color.White))
                e.Graphics.FillPolygon(b, new Point[] {
                    new Point(cx - 7, cy - 4), new Point(cx + 7, cy - 4), new Point(cx, cy + 6) });
            e.Graphics.SmoothingMode = old;
        }

        /// <summary>箭头区悬停：与主体共用同一个悬停态（整块一起提亮）。</summary>
        private void OnDropArrowEnter(object sender, EventArgs e) { OnDropFaceEnter(sender, e); }

        private void OnDropArrowLeave(object sender, EventArgs e) { OnDropFaceLeave(sender, e); }

        /// <summary>点击自绘箭头 = 展开下拉列表（转发给 ComboBox，不参与任何取值逻辑）。</summary>
        private void OnDropArrowClick(object sender, EventArgs e)
        {
            _cmbItemId.Focus();
            _cmbItemId.DroppedDown = true;
        }

        /// <summary>
        /// 【t37】点击最底端的 Bilibili 感谢链接 → 用系统默认浏览器打开作者主页。
        /// 全程 try/catch：无默认浏览器 / 被策略拦截 / Process.Start 抛异常时静默兜底，
        /// 绝不因外链问题影响修改器运行（不弹框、不中断写入循环）。
        /// </summary>
        private void OnThanksLinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                _lnkThanks.LinkVisited = true;
                System.Diagnostics.Process.Start("https://space.bilibili.com/238444");
            }
            catch (Exception)
            {
                // 浏览器启动失败：静默兜底（不影响任何功能）
            }
        }

        private void OnItemSelectionChanged(object sender, EventArgs e)
        {
            if (_suppressSel) return;
            // 自绘主体显示的就是选中项文本，选中变化时必须同步重绘
            if (_pnlDropFace != null) _pnlDropFace.Invalidate();
            InvEntry sel = _cmbItemId.SelectedItem as InvEntry;
            if (sel == null) { _selItemId = ""; _selItemAddr = 0; _lblItemCurrent.Text = "-"; return; }
            _selItemId = sel.Id;
            // 【方案C】把「目标种类」预置为当前物品所属的同一种类（同中文名 + 同 ItemType）；
            // 定位不到时不预置、不报错。纯附加联动，不影响下面任何既有取值逻辑。
            PresetKindForItem(sel.Id);
            GameResLocator.ItemRef r = FindInvItemByAddr(sel.Address);
            if (r == null) { _selItemAddr = 0; _lblItemCurrent.Text = "-"; return; }
            _selItemAddr = r.Address;
            _lblItemCurrent.Text = r.Count.ToString();
        }

        /// <summary>
        /// 按 **Item 实例地址** 在本次背包枚举结果里取回条目。
        /// 【必修缺陷修复 2026-09-26】旧实现 `FindInvItem(string id)` 按 id 查、**恒定返回第一件**，
        /// 背包里有同 id 多件（如两格木板）时作用对象会错 —— D 轮实测：选「木板×22」改到了「木板×30」。
        /// 地址才是这一格的身份；地址随下拉项 <see cref="InvEntry.Address"/> 一起来自同一次枚举。
        /// ⚠ 地址只在本进程内有效，不落盘、不上屏。
        /// </summary>
        private GameResLocator.ItemRef FindInvItemByAddr(long addr)
        {
            if (addr == 0) return null;
            for (int i = 0; i < _invItems.Count; i++)
                if (_invItems[i].Address == addr) return _invItems[i];
            return null;
        }

        // 【已删除 · t5 死代码清理】本节原有 3 个「背包轻量重枚举」成员（入口 + 工作线程 + 应用）：
        //   界面上的「查询」按钮已于 2026-09-23 移除，它们**没有任何事件绑定**，属 UI 死链；
        //   且其中一个还留有「⚠ 兜底定位…」旧文案（与玩家化要求方向相反）。
        //   职责已被「刷新」（全量重定位）+「下拉选中 + 250 ms 定时器」（自动读数量）完全覆盖。
        //   （按项目既有约定不列名，原文见 backup_src_t5\TrainerForm.cs）

        /// <summary>下拉项：显示「中文名 × 数量」（Id 仍是英文物品 ID，仅内部使用，不上屏）。</summary>
        /// <remarks>
        /// 【必修缺陷修复 2026-09-26】新增 <see cref="Address"/>：背包里可能有**多件同 id 物品**
        /// （例如两格「木板」，数量分别是 22 与 30）。旧实现一律用 `FindInvItem(id)` 按 id 查，
        /// **恒定返回第一件** ⇒ 玩家选「木板×22」，实际被改的是「木板×30」（D 轮实测复现）。
        /// Item 的**实例地址**才是这一格的身份，id 不是。
        /// 地址随 <see cref="SetInventory"/> 重建下拉项而同步更新，本字段不落盘、不上屏。
        /// </remarks>
        private class InvEntry
        {
            public string Id;
            public string Display;
            public long Address;
            public InvEntry(string id, string display, long address) { Id = id; Display = display; Address = address; }
            public override string ToString() { return Display; }
        }

        /// <summary>物品名对照表条目（中文名恒定来自游戏运行时内存提取）。</summary>
        private class ItemEntry
        {
            public string Id;
            public string Zh;
            public ItemEntry(string id, string zh) { Id = id; Zh = zh; }
        }

        // 内存完全不可用时的内置兜底表，保证下拉不为空（4 项）。
        private static readonly string[][] FallbackItemNames = new string[][]
        {
            new string[] { "faith",   "信仰" },
            new string[] { "science", "科学" },
            new string[] { "money",   "金钱" },
            new string[] { "energy",  "能量" },
        };

        /// <summary>
        /// 内存直读（物品名的唯一来源）：连上游戏进程后调用（后台线程），
        /// 从游戏运行时内存的 Dictionary&lt;string,string&gt; 词表取中文名；
        /// 完全取不到时才填 4 项兜底。
        /// 整表只扫一次（实测 4.0~4.6 s）—— 启动预热已建表或缓存命中时不会走到这里。
        /// </summary>
        private void LoadItemNamesFromMemory(List<GameResLocator.ItemRef> items)
        {
            if (_loc == null) return;

            // 背包里的 ID（去重）：内存直读的种子（越贴近背包，锚点命中越快）
            List<string> backpackIds = CollectBackpackIds(items);

            SetStatus("正在读取物品名称……", STEAM_WAIT);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            _loc.PrefetchLocalizedNames(backpackIds);
            sw.Stop();

            // 内存索引本身就是整张中文本地化词表，一次性装入
            int added = 0;
            if (_loc.LocalizedNameCount > 0)
            {
                Dictionary<string, string> memTable = new Dictionary<string, string>();
                _loc.CopyLocalizedNames(memTable);
                added = LoadNamesIntoTable(memTable);
            }

            if (added > 0)
            {
                // 整张词表已在内存里 —— 本会话后续的刷新不必再扫
                if (_loc.LocalizedNameCount >= NameCacheStore.MinEntries) _namesReady = true;
                Log("物品名称已加载完成。");
                return;
            }

            // 内存也拿不到（进程不可读 / 游戏未加载中文本地化）→ 4 项内置兜底
            if (NameTableCount == 0)
            {
                lock (_nameGate)
                {
                    for (int i = 0; i < FallbackItemNames.Length; i++)
                        _itemNames[FallbackItemNames[i][0]] =
                            new ItemEntry(FallbackItemNames[i][0], FallbackItemNames[i][1]);
                }
                Log("部分物品名称未能读取，将显示英文名称。");
            }
            else
            {
                Log("部分物品名称未能读取。");
            }
        }

        // ------------------------------------------------------------------
        // 物品名表缓存（进程无关的纯数据）
        //   一次游戏启动内不变、且与进程内存地址无关 → exe 启动时后台扫描一次
        //   → 落到系统临时目录 → 会话内「刷新」直接命中 → 退出时清理。
        //   刷新按钮只保留对「进程相关 / 游戏过程中会变动」数据的检索。
        // ------------------------------------------------------------------

        /// <summary>查找游戏进程 PID（精确名 → 模糊匹配），0 表示未找到。</summary>
        private static int FindGamePid()
        {
            string[] candidates = new string[] {
                "GraveyardKeeper2", "GraveyardKeeper", "Graveyard Keeper 2"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                int pid = ProcessMemory.FindPid(candidates[i]);
                if (pid != 0) return pid;
            }
            return ProcessMemory.FindPidFuzzy(new string[] { "Graveyard", "Keeper" }, true);
        }

        /// <summary>背包内 itemId 去重收集（实时内存提取的种子）。</summary>
        private static List<string> CollectBackpackIds(List<GameResLocator.ItemRef> items)
        {
            List<string> ids = new List<string>();
            if (items == null) return ids;
            for (int i = 0; i < items.Count; i++)
            {
                string id = items[i].ItemId;
                if (id == null || id.Length == 0) continue;
                if (ids.Contains(id)) continue;
                ids.Add(id);
            }
            return ids;
        }

        // --- 显示名表访问（加锁）--------------------------------------------
        // UI 线程（DisplayName）与预热 / 刷新后台线程会并发访问同一个字典，
        // 故所有读写都走这四个助手，避免 Dictionary 并发写导致异常或损坏。

        private int NameTableCount
        {
            get { lock (_nameGate) { return _itemNames.Count; } }
        }

        private bool NameTableLookup(string id, out ItemEntry e)
        {
            lock (_nameGate) { return _itemNames.TryGetValue(id, out e); }
        }

        /// <summary>把 id → 中文名装入显示名表（已有的不覆盖），返回新增条数。</summary>
        private int LoadNamesIntoTable(IDictionary<string, string> table)
        {
            if (table == null || table.Count == 0) return 0;
            int added = 0;
            lock (_nameGate)
            {
                foreach (KeyValuePair<string, string> kv in table)
                {
                    if (kv.Key == null || kv.Key.Length == 0) continue;
                    if (kv.Value == null || kv.Value.Length == 0) continue;
                    if (_itemNames.ContainsKey(kv.Key)) continue;
                    _itemNames[kv.Key] = new ItemEntry(kv.Key, kv.Value);
                    added++;
                }
            }
            return added;
        }

        /// <summary>
        /// 把 id → [redSkulls, whiteSkulls] 装入属性表（已有的不覆盖），返回新增条数。
        /// 【2026-09-26】与 `LoadNamesIntoTable` 并列，供「预热扫描」与「属性缓存命中」两条路径复用。
        /// </summary>
        private int LoadAttrsIntoTable(IDictionary<string, int[]> attrs)
        {
            if (attrs == null || attrs.Count == 0) return 0;
            int added = 0;
            lock (_nameGate)
            {
                foreach (KeyValuePair<string, int[]> kv in attrs)
                {
                    if (kv.Key == null || kv.Key.Length == 0) continue;
                    if (kv.Value == null || kv.Value.Length < 2) continue;
                    if (_itemAttrs.ContainsKey(kv.Key)) continue;
                    _itemAttrs[kv.Key] = kv.Value;
                    added++;
                }
            }
            return added;
        }

        /// <summary>属性表条数（加锁读；用于判断是否需要补扫）。</summary>
        private int ItemAttrCount
        {
            get { lock (_nameGate) { return _itemAttrs.Count; } }
        }

        /// <summary>
        /// 把 id → [quality, qualityType] 装入星级表（已有的不覆盖），返回新增条数。
        /// 【2026-09-26 · 方案C 前置】与 <see cref="LoadAttrsIntoTable"/> 并列、同门。
        /// </summary>
        private int LoadQualityIntoTable(IDictionary<string, int[]> quals)
        {
            if (quals == null || quals.Count == 0) return 0;
            int added = 0;
            lock (_nameGate)
            {
                foreach (KeyValuePair<string, int[]> kv in quals)
                {
                    if (kv.Key == null || kv.Key.Length == 0) continue;
                    if (kv.Value == null || kv.Value.Length < 2) continue;
                    if (_itemQuality.ContainsKey(kv.Key)) continue;
                    _itemQuality[kv.Key] = kv.Value;
                    added++;
                }
            }
            return added;
        }

        /// <summary>星级表条数（加锁读）。</summary>
        private int ItemQualityCount
        {
            get { lock (_nameGate) { return _itemQuality.Count; } }
        }

        /// <summary>
        /// 把 id → ItemType 数值装入类型表（已有的不覆盖），返回新增条数。
        /// 【2026-09-26 · 方案C】与 <see cref="LoadQualityIntoTable"/> 并列、同门。
        /// </summary>
        private int LoadTypeIntoTable(IDictionary<string, int> types)
        {
            if (types == null || types.Count == 0) return 0;
            int added = 0;
            lock (_nameGate)
            {
                foreach (KeyValuePair<string, int> kv in types)
                {
                    if (kv.Key == null || kv.Key.Length == 0) continue;
                    if (_itemType.ContainsKey(kv.Key)) continue;
                    _itemType[kv.Key] = kv.Value;
                    added++;
                }
            }
            return added;
        }

        /// <summary>类型表条数（加锁读）。</summary>
        private int ItemTypeCount
        {
            get { lock (_nameGate) { return _itemType.Count; } }
        }

        /// <summary>
        /// 【方案C】ItemType 数值 → **界面辅助标签**（中文）。
        /// ⚠ 这些中文是**本工具自定的显示标签**，不是游戏本地化原文（游戏内 ItemType 不直接上屏）——
        /// 仅用于在「中文名 + 类型」分组键里把同名不同类的条目区分开（全库只影响「外科医生的失误」6 条）。
        /// 数值 → 名称的对应关系取自反编译源码 `public enum ItemType`（None=0 … Bag=400、Demon=666），
        /// 属**静态确定**；中文标签本身属**工程约定**，不得宣称与游戏等价。
        /// </summary>
        private static string ItemTypeName(int t)
        {
            switch (t)
            {
                case 0: return "无类型";
                case 1: return "斧";
                case 2: return "铲";
                case 3: return "镐";
                case 4: return "锤";
                case 5: return "钓竿";
                case 10: return "手";
                case 11: return "剑";
                case 12: return "护甲";
                case 13: return "脑";
                case 14: return "心";
                case 15: return "血肉";
                case 16: return "骨";
                case 18: return "鱼饵";
                case 20: return "布道";
                case 22: return "弓";
                case 23: return "手术器具";
                case 24: return "试剂";
                case 25: return "小工具";
                case 26: return "书";
                case 27: return "护符";
                case 28: return "烤肉";
                case 30: return "防腐";
                case 35: return "头骨";
                case 36: return "内脏";
                case 37: return "皮肤";
                case 45: return "项圈";
                case 50: return "长矛";
                case 400: return "背包";
                case 666: return "恶魔";
                default: return "类型" + t;
            }
        }

        /// <summary>ItemDef 地址表条数（加锁读）。地址表为空 ⇒ 方案C 写入路径不可用，需现场补扫。</summary>
        private int ItemDefAddrCount
        {
            get { lock (_nameGate) { return _itemDefAddr.Count; } }
        }

        /// <summary>
        /// 【方案C 前置】从一次已完成的 `FindAllItemDefIds` 现场把**地址表**搬进本窗体。
        /// ⚠ 纯进程内拷贝：调用方须保证同一次进程会话；**这些值绝不写任何缓存文件**（红线③）。
        /// 每次整表重建时整体替换（不是"已有的不覆盖"）—— 地址随重扫变化，旧地址必须丢弃。
        /// </summary>
        private void LoadItemDefAddrFrom(GameResLocator loc)
        {
            if (loc == null) return;
            lock (_nameGate)
            {
                _itemDefAddr.Clear();
                _itemDefIdPtr.Clear();
                foreach (KeyValuePair<string, long> kv in loc.ItemDefInstAddr)
                {
                    if (kv.Key == null || kv.Key.Length == 0) continue;
                    _itemDefAddr[kv.Key] = kv.Value;
                }
                foreach (KeyValuePair<string, long> kv in loc.ItemDefIdPtr)
                {
                    if (kv.Key == null || kv.Key.Length == 0) continue;
                    _itemDefIdPtr[kv.Key] = kv.Value;
                }
            }
        }

        /// <summary>
        /// 【2026-09-26】只补扫红白骷髅属性（**不扫词表**）：名表缓存命中、但属性缓存缺失时调用。
        /// 实测 `FindAllItemDefIds` 只枚举 ItemDef 实例（词表提取是另一个方法），成本远低于整表提取。
        /// 失败一律静默 —— 没有属性只是少一个标注，界面照常可用。
        /// </summary>
        private void TryFillAttrsOnly(int pid, string fp)
        {
            try
            {
                ProcessMemory mem = new ProcessMemory();
                if (!mem.Open(pid)) return;
                try
                {
                    GameResLocator loc = new GameResLocator(mem);
                    loc.FindAllItemDefIds();                        // 只为填充 loc.ItemDefAttrs
                    // 【2026-09-26 · 方案C 前置】星级与地址表同一遍扫描顺带产出，一并搬回
                    // （即使红白属性为空也照搬：星级菜单的置灰判定不依赖红白）。
                    LoadQualityIntoTable(new Dictionary<string, int[]>(loc.ItemDefQuality));
                    LoadTypeIntoTable(new Dictionary<string, int>(loc.ItemDefType));
                    LoadItemDefAddrFrom(loc);
                    Dictionary<string, int[]> attrs = new Dictionary<string, int[]>(loc.ItemDefAttrs);
                    if (attrs.Count == 0) return;
                    LoadAttrsIntoTable(attrs);
                    string note;
                    NameCacheStore.TryWriteAttrs(fp, attrs, out note);   // 下次启动即可命中属性缓存
                }
                finally { mem.Close(); }
            }
            catch (Exception ex) { _lastErrorNote = ex.GetType().Name; }
        }

        /// <summary>把候选键（若在词表中且尚未入表）加入名表；供 IntersectNames 复用。</summary>
        private static void AddNameCandidate(Dictionary<string, string> t, Dictionary<string, string> whole, string key)
        {
            if (t == null || whole == null || key == null || key.Length == 0) return;
            if (t.ContainsKey(key)) return;
            string zh;
            if (whole.TryGetValue(key, out zh) && zh != null && zh.Length > 0) t[key] = zh;
        }

        /// <summary>
        /// 词表 ∩ 物品 id 全集 = 物品名表（精简掉 UI / 任务 / 对话等非物品键）。
        /// 【2026-09-26 补】星级变体的**备用键**也必须入表：
        ///   游戏 `ItemDef.GetHeader()` 对 qualityType==Star 的物品先查完整 id，
        ///   查不到则回退 `id.Split(':')[0]`；而游戏中文词表里几乎没有 ":N" 键
        ///   （9387 条中仅 9 条含冒号）—— 也就是说**备用键才是词表里真实存在的那一条**，
        ///   而备用键本身不是任何 ItemDef 的 id，旧实现只做精确匹配 ⇒ 被交集过滤掉，
        ///   星级物品在缓存路径下永远查不到中文名（用户报告的「未汉化」）。
        ///   备用键的选取依据是游戏**原始本地化资源** lng_zh_cn 的 aliases1/aliases2 别名表
        ///   （8697 条，从 resources.assets 提取，见 evidence\loc_lng_zh_cn_aliases.csv）：
        ///       bones_1_1:2 -> bones      skull_0_2:2 -> skull     heart_1_1:1 -> heart
        ///       brain_0_2:2 -> brain      guts_1_0:1  -> guts      skin_0_0:1  -> skin_1_1
        ///   三类候选键与别名表在 814 个 ItemDef 上逐条比对结果见 coverage_report.txt。
        /// 【2026-09-26 · P3 删除】原在此另有两条**超出游戏规则**的候选键：基名再取第一个下划线
        ///   段（`bones_1_1:2` → `bones`）与 `该段 + "_1_1"`。这两条是本工具自创的猜测型规则
        ///   （游戏 `GetHeader()` 只回退到 `id.Split(':')[0]`），复算证明：别名表就绪时它们
        ///   一条也接不住（命中 0），仅在别名表不可用的退化会话里接住 36 条人体部件星级变体 ——
        ///   而"可能猜对几个、可能静默猜错"的代价不值这个收益 ⇒ 一并删除，见 P3 报告。
        /// </summary>
        private static Dictionary<string, string> IntersectNames(Dictionary<string, string> whole, List<string> itemIds,
                                                                 GameResLocator loc)
        {
            Dictionary<string, string> t = new Dictionary<string, string>();
            if (whole == null || itemIds == null) return t;
            for (int i = 0; i < itemIds.Count; i++)
            {
                string id = itemIds[i];
                string zh;
                if (whole.TryGetValue(id, out zh) && zh != null && zh.Length > 0)
                    t[id] = zh;
                else
                    AddByAliasChain(t, whole, loc, id);        // 【方案A】别名表跟链

                // 星级变体（形如 "xxx:N"）的备用键一并入表
                int colon = id.IndexOf(':');
                if (colon > 0)
                {
                    string baseId = id.Substring(0, colon);
                    AddNameCandidate(t, whole, baseId);                       // xxx_0_0
                    AddByAliasChain(t, whole, loc, baseId);                   // 【方案A】基名也走别名链
                }
            }
            return t;
        }

        /// <summary>
        /// 【方案A】把 key 经**运行时别名表**跟链后的结果并入名表：
        ///   tail = 别名链尾（无别名命中时 == key）；tail 在中文词表里 ⇒ t[key] = whole[tail]。
        /// 语义与游戏 <c>LLBase.L(key)</c> 一致（别名优先、跟链到不动点、命中即不再查原键）。
        /// 【t30 · R3 订正】原注释断言「实测『别名键 100% 不在词表』」——**不准确**。
        /// 独立复算（`evidence\verify_t27\recompute_t27.txt`）得 8697 条中**有 1 条例外**：
        /// `38_village_mailbox_certificate`（词表值「领取村长证明。」vs 别名链尾「深入森林」）。
        /// 当前**无任何 ItemDef 的链尾指向它**（该键本身也不在 814 全集内、未写入名表）
        /// ⇒ 对 814 个物品的影响为 **0**。
        /// ⚠ 但这意味着「直查优先 ⇔ 别名优先」的等价性**并非无条件**（依赖数据巧合）：
        /// 若未来某个 id 的别名链尾落在这类「同时在词表」的键上，两条路径会给出不同显示 ——
        /// **届时必须重新评估直查优先的等价性**（必要时改为严格按 `LLBase.L` 的别名优先）。
        /// loc 为空 / 别名表未建立时静默跳过（回退到接入前的行为）。
        /// </summary>
        private static void AddByAliasChain(Dictionary<string, string> t, Dictionary<string, string> whole,
                                            GameResLocator loc, string key)
        {
            if (t == null || whole == null || loc == null || key == null || key.Length == 0) return;
            if (t.ContainsKey(key)) return;
            if (!loc.IsAliasTableReady) return;
            string tail = loc.ResolveAliasChain(key);
            if (tail == null || tail.Length == 0 || tail == key) return;
            string zh;
            if (whole.TryGetValue(tail, out zh) && zh != null && zh.Length > 0) t[key] = zh;
        }

        /// <summary>
        /// 刷新路径的物品名装载：整表已就绪 → 缓存命中 → 直接返回（不阻塞）→ 实时提取 → 兜底。
        /// 物品名恒定来自游戏运行时内存；整表已就绪或缓存命中时**不会**再做整表提取
        /// （那一步实测 3.9~4.3 s 物理下限）。
        /// 【P0-② 性能】**不再等待启动预热**：旧实现在这里用「轮询等待预热结束」阻塞刷新线程，
        /// 使冷刷新就绪时间被预热时长（实测 7.6~10.2 s）绑架 —— 这正是「冷刷新 9.8 s 里
        /// 有 2.2~4.4 s 在等预热」的根因。现在预热完成后由预热线程自己回填界面，
        /// 冷刷新先用物品编号显示、稍后自动变中文名。
        /// </summary>
        private void EnsureItemNames(List<GameResLocator.ItemRef> items)
        {
            // ① 整表已就绪（本会话已装载 / 预热直接把表留在内存）
            if (_namesReady)
            {
                Log("物品名称已加载完成。");
                return;
            }

            // ② 缓存（毫秒级；含跨会话持久缓存）—— 「扫描一次 → 之后直接命中」的主路径；
            //    指纹不符 / 文件损坏 / 目录不可写 / 词表为空都会安全失败
            if (_gameFingerprint == null && _pid != 0)
                _gameFingerprint = NameCacheStore.GameFingerprint(_pid);
            if (TryLoadCacheIntoTable(_gameFingerprint))
            {
                Log("物品名称已加载完成。");
                return;
            }

            // ③ 【P0-②】预热正在后台读取 → 立即返回，绝不阻塞刷新（稍后自动回填中文名）
            if (_warmupRunning)
            {
                Log("物品名称正在后台读取，稍后会自动显示。");
                return;
            }

            // ④ 最后一级：实时内存提取 + 4 项兜底
            LoadItemNamesFromMemory(items);
        }

        /// <summary>
        /// 尝试用会话缓存装表（毫秒级）。失败（文件缺失 / 指纹不符 / 损坏 / 条目过少 / 目录不可写）
        /// 时返回 false，由调用方回退到实时内存提取 —— 不抛异常、不弹框、不影响界面可用。
        /// </summary>
        private bool TryLoadCacheIntoTable(string fingerprint)
        {
            Dictionary<string, string> table;
            string note;
            if (!NameCacheStore.TryRead(fingerprint, out table, out note)) return false;
            if (note == null) return false;
            if (table.Count < NameCacheStore.MinEntries) return false;

            // 【t34 · T1 修复】**名表缓存不得被 UI 实例的建立时序否决**。
            //   原实现在「名表已读出、条数已过检」之后才写 `if (_loc == null) return false;`
            //   ⇒ 连名表缓存一起废掉：`StartWarmup()` 必然早于 `_loc` 的创建（在 `LocateWorker` 里），
            //   于是预热线程每次都走现场全扫（词表 + 别名表 ≈ 8~9 s + `IntersectNames`），
            //   暖启动由「缓存命中 5 s 内」退化到 17.02 s（评审 t33 finding T1，high）。
            //   名表缓存的可信度由 `NameCacheStore.TryRead` 内的**本文件自洽校验**（标识 / 类型 /
            //   指纹 / 语言 / 条数下限 / 声明与实际条数一致）**独立保证**，与 `_loc` 何时建立无关。
            //   【2026-09-26 · P2】原注释写的是"由 `alias=` 门限独立保证" —— 该字段与门限已删除，
            //   判据改为上列各项（取消的只是跨文件互验，不是"不校验"）。
            //   别名表 import 改为「`_loc != null` 时执行，否则交给 `StartLocAliasBackfill`」
            //   （后台补 import + 建词表，见 `LocAliasBackfillWorker`；import 毫秒级、建词表 ≈4 s，
            //   全程不阻塞 UI）。
            if (_loc != null)
            {
                List<string> aliasFrom, aliasTo;
                string aliasNote, cacheVerify;
                // 【2026-09-26 · P2】原先把名表缓存头的 `alias=` 作**交叉基准**传入，用于检出
                //   「正文截短 + count 同步改小」的残缺表。该字段与判据均已取消（用户裁决：
                //   持久缓存各自自洽）⇒ 这里不再多读一次名表缓存。**已知边界**：自洽残缺表
                //   不再被检出（预期降级）；缓解手段 = 任何拒绝都能靠 P1 的写回通路自愈。
                if (!NameCacheStore.TryReadAlias(fingerprint,
                        out aliasFrom, out aliasTo, out cacheVerify, out aliasNote))
                {
                    EvidenceLog("别名表缓存不可用（" + aliasNote + "），本次改为现场重建名表与别名表。");
                    return false;
                }
                string importNote;
                if (!_loc.TryImportAliasTable(aliasFrom, aliasTo, out importNote))
                {
                    EvidenceLog("别名表缓存内容校验未过（" + importNote + "），本次改为现场重建名表与别名表。");
                    return false;
                }
                // importNote 必须**可见**：它含「词表为空 ⇒ 后两项跳过（未验证）」这一关键状态
                //   （缓存命中路径下 `_loc` 没有词表，③④ 确实没验）。缓存自带的 verify 字段
                //   也一并输出，避免"这张表到底验没验过"变成不可观测。
                // 【2026-09-26 收尾轮】"可见" = **证据模式下可见**（EvidenceLog）：该行是纯内部明细
                //   （别名表行数 / 词表状态 / 验证标记），玩家读不懂，不再占用玩家日志栏。
                EvidenceLog("别名表 " + importNote + "；缓存来源验证="
                    + (cacheVerify == null || cacheVerify.Length == 0 ? "未标注" : cacheVerify));
                // 【R1④ 解耦】导入成功即尝试补验（词表若已就绪就真的跑；未就绪则留给下次机会）
                RunAliasVerificationOnce(_loc, "缓存命中导入后");
            }
            else
            {
                // `_loc` 尚未建立（预热线程早于 `LocateWorker`）：**名表照常装载**（见下），
                //   别名表 import 与词表建立留给 `_loc` 建立后的 `StartLocAliasBackfill`。
            }

            LoadNamesIntoTable(table);
            // 【t34 · T1/T2】标记「名表来自缓存」：这是**唯一**允许后台补建 UI 侧词表的路径
            //   （T2 的死结由此解开），冷启动 / 预热扫描路径不做无谓重扫。
            _namesFromCache = true;
            // 【2026-09-26】顺带装载红白骷髅属性（**独立**缓存文件）。
            // 缺失不影响名表可用性 —— 只是这条路径上暂时没有红白标注，界面照常可用。
            Dictionary<string, int[]> attrs;
            string anote;
            if (NameCacheStore.TryReadAttrs(fingerprint, out attrs, out anote) && attrs != null)
                LoadAttrsIntoTable(attrs);
            // 【F3 订正】缓存路径也要给出名表条数（原实现留给汇总日志一个 -1，日志自相矛盾）
            _statNameWithAlias = NameTableCount;
            _namesReady = true;
            return true;
        }

        /// <summary>
        /// 【P0-②】预热完成后回填界面：把下拉里的物品名由 ID 换成中文名。
        /// 冷刷新不再等待预热（见 EnsureItemNames），因此需要预热自己把结果推上界面。
        /// 跨线程 → 走 UI 线程；复用 SetInventory 以保留当前选中项。
        /// </summary>
        private void RefillInventoryNames()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefillInventoryNames)); return; }
            // 【方案C】框2/框3 的数据源是**全库 814 条**（与背包无关）⇒ 必须在「背包为空就早退」
            // 之前按需重建；否则未进档 / 背包为空时种类列表永远不会出现。
            // EnsureKindList 自带版本判重：数据没变时只是一次字符串比较，不会打断玩家选择。
            EnsureKindList();
            if (_invItems == null || _invItems.Count == 0) return;
            SetInventory(_invItems);
        }

        // --- 启动预热 / 孤儿清理 --------------------------------------------

        private void StartOrphanCleanup()
        {
            System.Threading.Thread t = new System.Threading.Thread(
                new System.Threading.ThreadStart(OrphanCleanupWorker));
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>清理本工具专属临时目录下上次异常退出留下的孤儿会话目录。</summary>
        private void OrphanCleanupWorker()
        {
            try
            {
                // 清理结果不再上屏（临时文件目录属实现细节，玩家无需知道）
                NameCacheStore.CleanupOrphans();
            }
            catch { }
        }

        private void StartWarmup()
        {
            if (_warmupRunning || _namesReady) return;
            _warmupRunning = true;
            System.Threading.Thread t = new System.Threading.Thread(
                new System.Threading.ThreadStart(WarmupWorker));
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 启动预热（后台线程，不阻塞 UI）：
        ///   ① 已有可用缓存（含跨会话持久缓存）→ 直接装载，不扫内存；
        ///   ② 否则扫描一次：ItemDef id 全集 + 整张中文本地化词表
        ///      → 取交集得到物品名表 → 写缓存（含持久缓存）→ 回填界面。
        /// 【t3 C-1】扫描异常不再静默：写入诊断并明确提示，避免「看起来成功却拿不到名字」。
        /// 游戏未启动 / 非中文 / 扫描失败时安全跳过，不抛异常、不弹框、不影响界面可用。
        /// </summary>
        private void WarmupWorker()
        {
            try
            {
                if (_namesReady) return;
                int pid = FindGamePid();
                if (pid == 0) return;
                string fp = NameCacheStore.GameFingerprint(pid);
                _gameFingerprint = fp;

                // ① 缓存可用：毫秒级装载
                if (TryLoadCacheIntoTable(fp))
                {
                    // 【2026-09-26】属性缓存缺失时的补扫：名表缓存是**持久**的（老版本就写过），
                    // 而属性缓存是本次新引入的独立文件 ⇒ 首次启用时「名表命中、属性为空」，
                    // 若不补扫，下拉会一直没有红白标注（实测首次启动即如此）。
                    // 补扫只枚举 ItemDef（不扫词表），成本远低于整表提取。
                    // 【2026-09-26 · 方案C】补扫条件加上「星级表空」与「地址表空」：
                    //   星级表可由属性缓存之外的路径补齐，但**地址表永不落盘**（红线③）
                    //   ⇒ 每次缓存命中路径都必须现场重扫一遍才能拿到 ItemDef 实例地址。
                    if (ItemAttrCount == 0 || ItemQualityCount == 0 || ItemTypeCount == 0 || ItemDefAddrCount == 0)
                        TryFillAttrsOnly(pid, fp);
                    // 【t30 · R1①】缓存命中路径同样要建立 UI 侧词表 + 别名表（后台线程、不阻塞 UI）：
                    //   否则 `LookupNameKeyWithAlias` 的别名分支在**主路径**上是死代码（评审 t29 R1）；
                    //   补建完成后会做一次一致性自检（R1④）与「名表缺条 ⇒ 别名链补齐并重写缓存」的自愈（R1③）。
                    StartLocAliasBackfill();
                    SetStatus("物品名称已加载完成。", STEAM_TEXT);
                    RefillInventoryNames();
                    return;
                }

                // ② 否则扫描一次（进程无关的纯数据）
                ProcessMemory mem = new ProcessMemory();
                if (!mem.Open(pid)) return;
                try
                {
                    SetStatus("正在读取物品名称（首次启动需要几秒）……", STEAM_WAIT);
                    GameResLocator loc = new GameResLocator(mem);
                    List<string> defIds = loc.FindAllItemDefIds();
                    loc.PrefetchLocalizedNames(new List<string>());
                    // 【方案A】词表建立后并列建立运行时别名表；失败则 IsAliasTableReady=false，
                    //   后续 IntersectNames 静默退回接入前的行为（不影响可用性）。
                    // 【t30 · R6④】别名表状态日志 + 对照实验降为调试开关（默认关闭）：
                    //   原实现为生成对照数字**常驻**执行第二遍 IntersectNames(...,null) 并上屏两条日志，
                    //   属「取证代码进生产」。开启方式：GK2TRAINER_EVIDENCE=1。
                    Dictionary<string, string> whole = new Dictionary<string, string>();
                    loc.CopyLocalizedNames(whole);
                    Dictionary<string, string> itemTable = IntersectNames(whole, defIds, loc);

                    if (EvidenceMode)
                    {
                        Log("别名表状态：" + loc.AliasTableStatus);
                        // 【方案A 对照实验】同一函数、同一输入，**唯一差异是 loc 是否为 null**：
                        //   loc = null ⇒ 接入前行为（无别名链）；loc = 真实 ⇒ 接入后行为。
                        //   两者条数差 = 别名链真正新增的条目数（不引用任何离线 CSV，运行时自证）。
                        Dictionary<string, string> baseTable = IntersectNames(whole, defIds, null);
                        int aliasAdded = itemTable.Count - baseTable.Count;
                        string aliasText = loc.IsAliasTableReady
                            ? (loc.AliasTableCount + " 条") : ("不可用（" + loc.AliasTableStatus + "）");
                        _statAliasText = aliasText;
                        _statNameBase = baseTable.Count;
                        _statNameWithAlias = itemTable.Count;
                        _statAliasAdded = aliasAdded;
                        Log("别名表 " + aliasText + "；名表 " + itemTable.Count
                            + " 条（对照：无别名链 " + baseTable.Count + " 条 ⇒ 别名链新增 "
                            + aliasAdded + " 条）");
                        if (aliasAdded > 0)
                        {
                            List<string> gained = new List<string>();
                            foreach (KeyValuePair<string, string> kv in itemTable)
                            {
                                if (gained.Count >= 8) break;
                                if (!baseTable.ContainsKey(kv.Key)) gained.Add(kv.Key);
                            }
                            Log("别名链新增示例：" + string.Join(", ", gained.ToArray()));
                        }
                    }

                    if (itemTable.Count < NameCacheStore.MinEntries)                    {
                        // 【t3 C-1】把「静默失效」变成可见提示：诊断在 loc.Diagnostics 里
                        Log("物品名称还没读全，稍后会自动再试一次。");
                        return;
                    }

                    // 【P1 修复 2026-09-26】无论写缓存成功与否都必须装载名表。
                    // 原实现只在 TryWrite **失败**时装载 ⇒ 写成功时（正是「缓存失效后首次启动」
                    // 的真实场景）名表只留在局部变量、_namesReady 保持 false，而 UI 线程侧的
                    // _loc 是另一个实例且 TryGetLocalizedName 不触发扫描 ⇒ 本会话内下拉列表
                    // 全程显示原始 id，必须手动点一次「刷新」才恢复。
                    // 由 t4 独立验证判定为必修缺陷（02_分析记录\_星级与读档机制_20260926\
                    // t4_独立验证_汉化修复_20260926.md）。
                    // 【t29 返工 · 别名表落盘】把现场建好的别名表导出：
                    //   ① 写**独立**别名表缓存（纯数据 + 游戏指纹 + 独立表结构版本号）；
                    //   ② 共享给 UI 侧 `_loc`（让 DisplayName 的别名链与下一会话的缓存命中路径都能用上）。
                    List<string> aliasFrom, aliasTo;
                    // 【2026-09-26 · P2】该局部量**只**用于界面统计 `_statAliasText`（现场行数）。
                    //   原先它还作为 `TryWrite` 的 `aliasEntries` 实参写进名表缓存头（`alias=`），
                    //   该形参与字段已删除 ⇒ 这里不再跨文件传递。
                    int aliasEntries = 0;
                    if (loc.TryExportAliasTable(out aliasFrom, out aliasTo))
                    {
                        aliasEntries = aliasFrom.Count;
                        string awNote;
                        // 现场建表时词表必然已在内存 ⇒ 四重验证已含 ③④ ⇒ 缓存标注 loc-checked
                        NameCacheStore.TryWriteAlias(fp, aliasFrom, aliasTo,
                                                     loc.LocalizedNameCount > 0, out awNote);
                        if (_loc != null)
                        {
                            string aiNote;
                            if (_loc.TryImportAliasTable(aliasFrom, aliasTo, out aiNote))
                                EvidenceLog("别名表 " + aiNote);
                        }
                        // 【R1④ 解耦】冷启动路径同样尝试一次补验（用有词表的 loc，必然能跑成）
                        RunAliasVerificationOnce(loc, "冷启动现场建表后");
                    }
                    if (_statNameWithAlias < 0) _statNameWithAlias = itemTable.Count;
                    _statAliasText = aliasEntries + " 条（现场）";

                    string wrNote;
                    NameCacheStore.TryWrite(fp, itemTable, out wrNote);   // 写失败不影响本会话可用性
                    LoadNamesIntoTable(itemTable);
                    // 【2026-09-26】红白骷髅属性：与 id 列表在**同一遍堆扫描**里顺带读到（零额外扫描成本），
                    // 装入内存表，并写**独立**属性缓存供后续会话的「缓存命中」路径使用。
                    Dictionary<string, int[]> attrs = new Dictionary<string, int[]>(loc.ItemDefAttrs);
                    LoadAttrsIntoTable(attrs);
                    // 【2026-09-26 · 方案C 前置】星级表 + 地址表（地址只在本进程内存，不落盘）
                    LoadQualityIntoTable(new Dictionary<string, int[]>(loc.ItemDefQuality));
                    LoadTypeIntoTable(new Dictionary<string, int>(loc.ItemDefType));
                    LoadItemDefAddrFrom(loc);
                    string anAttrNote;
                    NameCacheStore.TryWriteAttrs(fp, attrs, out anAttrNote);
                    _namesReady = true;
                    SetStatus("物品名称已加载完成。", STEAM_TEXT);
                    RefillInventoryNames();     // 【P0-②】把中文名推上界面
                }
                finally { mem.Close(); }
            }
            catch (Exception ex)
            {
                // 【t3 C-1】预热异常不再静默（旧版空 catch 会让「整表提取失效」完全无痕）
                // 【2026-09-26 收尾轮】玩家可见文案去掉**异常类型名**（英文类名对玩家无意义，
                //   已由 SaveErrorNote 落在会话目录 last_error.txt 供排障）；玩家只需知道后果与去向。
                _lastErrorNote = ex.GetType().Name;
                SaveErrorNote();
                Log("物品名称暂时没能读取，先用物品编号显示；稍后会自动再试一次。");
            }
            finally
            {
                _warmupRunning = false;
            }
        }

        // ==================================================================
        // 【t30 R1①③④ + t34 T1/T2】UI 侧词表 / 别名表：后台补建 + 名表自愈 + 一致性自检
        //
        // 背景（评审 t29 finding R1）：缓存命中路径下名表由缓存装载 ⇒
        //   ① `LookupNameKeyWithAlias` 的别名分支在**主路径**上曾是死代码；
        //   ② 若某次扫描会话别名表建表失败，会写出一张「缺 43 条、Magic 相同」的名表缓存，
        //      此后每次启动都命中它且**无自愈路径**。
        // 当前实现（含 t34 订正）：
        //   · `TryLoadCacheIntoTable`：`_loc` 已建立 ⇒ **直接 import 别名表缓存**（毫秒级）；
        //     `_loc` 未建立 ⇒ 只装名表并 `return true`，**不得否决名表缓存**（T1）；
        //     两条分支都会置 `_namesFromCache`；
        //   · `_loc` 建立后由 `LocAliasBackfillWorker` 补 import（若尚未）+ 建**词表**（≈4 s），
        //     判据是「**词表**未就绪」而不是「别名表已就绪」一票否决（T2：解开自检/自愈死结）；
        //   · 词表就绪后做一次**自愈**（补名表缺条并重写缓存）与**两次自检**
        //     （「别名表 vs 词表」补验 + 「名表直查 vs 别名链」一致性）。
        // ==================================================================

        /// <summary>
        /// 【t29 返工 · R1④ 解耦】两条路径（冷启动现场建表 / 缓存命中导入）**各**尝试一次
        /// 「别名表 vs 词表」补验，并顺势跑一次 t30 的「名表直查 vs 别名链」一致性自检。
        /// 守卫用 <c>_aliasSelfTestDone</c> 而**不是** <c>AliasTableCount &gt; 0</c> ——
        /// 原实现挂在 LocAliasBackfillWorker 上，被「别名表已就绪就跳过」挡住，
        /// 以致缓存命中主路径上自检**永不运行**（评审 t29 R1④ 的原话）。
        /// **本次跑不成（词表未就绪）时保持标志为 false**，留给下一次机会 ——
        /// 绝不把「跳过」当成「通过」。
        /// </summary>
        private void RunAliasVerificationOnce(GameResLocator loc, string where)
        {
            if (_aliasSelfTestDone) return;
            if (loc == null) return;
            string v;
            try { v = loc.VerifyAliasAgainstLocTable(); }
            catch (Exception ex) { EvidenceLog("【R1④ 自检】补验异常（已忽略）：" + ex.GetType().Name); return; }
            if (v == null) return;                 // 词表未就绪 ⇒ 这次没跑成，保留机会
            _aliasSelfTestDone = true;
            // 【2026-09-26 收尾轮】本方法**整体**是内部自检（R1④）：结论只服务于交付取证，
            //   对玩家没有任何可操作含义 ⇒ 三条日志一律走 EvidenceLog，不再占用玩家日志栏。
            EvidenceLog("【R1④ 自检】" + where + " —— " + v);
            try { AliasPathConsistencySelfTest(); }
            catch (Exception ex) { EvidenceLog("【R1④ 自检】一致性自检异常（已忽略）：" + ex.GetType().Name); }
        }

        /// <summary>
        /// 【t34 · T2 修复】是否可以进行 UI 侧补建。
        ///   判据从「**别名表**已就绪就跳过」改为「**词表**已就绪才跳过」：
        ///   缓存命中路径下 `TryImportAliasTable` 成功会让 `AliasTableCount = 8697`，
        ///   而 `_loc` **没有词表** ⇒ 原判据一票否决补建 ⇒ 词表永不建立 ⇒
        ///   ① `VerifyAliasAgainstLocTable` 永远返回 null（自检拿不到"通过"）、
        ///   ② `SelfHealNameTableFromAlias` 永不执行、③ `LookupNameKeyWithAlias` 的内存词表分支
        ///   在缓存命中会话里全程不可用 —— 即评审 t33 finding T2 的**死结**。
        ///   另加两条必要约束：只在「名表来自缓存」的路径补建（冷启动 / 预热扫描不重复劳动），
        ///   以及词表已就绪时立即返回（幂等）。
        /// </summary>
        private bool NeedLocAliasBackfill()
        {
            if (_loc == null) return false;
            if (_locBackfillRunning) return false;
            if (!_namesFromCache) return false;
            if (_loc.LocalizedNameCount > 0) return false;   // 词表已就绪 ⇒ 无事可做
            return true;
        }

        /// <summary>【R1①】后台补建 UI 侧词表 + 别名表；失败静默（不影响任何既有功能）。</summary>
        private void StartLocAliasBackfill()
        {
            if (!NeedLocAliasBackfill()) return;
            _locBackfillRunning = true;
            try
            {
                System.Threading.Thread t = new System.Threading.Thread(
                    new System.Threading.ThreadStart(LocAliasBackfillWorker));
                t.IsBackground = true;
                t.Start();
            }
            catch { _locBackfillRunning = false; }
        }

        private void LocAliasBackfillWorker()
        {
            try
            {
                GameResLocator loc = _loc;
                if (loc == null) return;
                // 【P1 · F-A 修复】记「本次别名表缓存**没能用上**」（文件缺失 / 被拒）。
                //   为 true ⇒ 下面的 `PrefetchLocalizedNames` 必然走现场读取 ⇒ 读成功后
                //   必须把表写回缓存；否则「只缺 .alias」或「.alias 被改坏」会被永久固化：
                //   每次启动都拒缓存、退回现场读取（多付 ≈8 s 并每次打一条告警），
                //   而**没有任何路径**能把它修回来。
                bool aliasCacheMissed = false;
                // 【t34 · T1】`_loc` 建立前无法 import ⇒ 别名表缓存在这里补上（毫秒级，不重扫）。
                //   补 import 后词表建立只需 ≈4 s（`EnsureAliasIndex` 见表已就绪会早退）。
                //   【2026-09-26 · P2】原先这里为了取「名表缓存头的 `alias=` 交叉基准」而**多读一次
                //   名表缓存**；该字段与判据已取消 ⇒ 这次多余读取一并删除。
                if (loc.AliasTableCount == 0 && _gameFingerprint != null && _gameFingerprint.Length > 0)
                {
                    List<string> af, at;
                    string av, an;
                    if (NameCacheStore.TryReadAlias(_gameFingerprint, out af, out at, out av, out an))
                    {
                        string inote;
                        if (loc.TryImportAliasTable(af, at, out inote))
                        {
                            EvidenceLog("别名表 " + inote + "（缓存命中后台补 import；缓存来源验证="
                                + (av == null || av.Length == 0 ? "未标注" : av) + "）");
                        }
                        else
                        {
                            // 【2026-09-26 · P2 可观测性补齐】原实现**只有成功分支打日志**，失败分支
                            //   完全静默 ⇒「`.alias` 内容级损坏（行数与 count 都自洽、但 >10% 非法
                            //   标识符）」在界面上不可见，只能靠旁证反推（P1 §4.4 的现场教训）。
                            //   这里只补**一条日志**，拒绝理由直接取自既有 `inote`，不新增任何机制。
                            //   ⚠ 如实登记：该场景**仍然不自愈** —— 导入失败会置 `_aliasIndexFailed`，
                            //   `EnsureAliasIndex` 首行早退 ⇒ 现场重建被挡 ⇒ 下面的写回块前置条件
                            //   （`loc.IsAliasTableReady`）不成立。这是 P1 §5.2 已登记的残余退化路径，
                            //   本轮按「如非必要勿增实体」不修（修它要新增清标志+重试控制流）。
                            EvidenceLog("别名表缓存内容校验未过（" + inote + "），本次退回现场读取。");
                        }
                    }
                    else
                    {
                        aliasCacheMissed = true;
                        EvidenceLog("别名表缓存后台补 import 未采用（" + an + "），本次退回现场读取。");
                    }
                }
                // 词表建立（内部：词表就绪后并列建立运行时别名表 + 四重验证；表已就绪则早退）
                loc.PrefetchLocalizedNames(new List<string>());
                if (loc.LocalizedNameCount <= 0) return;
                // 【P1 · F-A 修复】现场读取**成功**后补写回 `.alias`。
                //   修复前：全产品唯一写 `.alias` 的路径是 `WarmupWorker` 的**冷启动全量扫描**
                //   分支（见该方法内 `TryWriteAlias` 调用），而缓存命中路径永不进入它
                //   ⇒ 只剩 `.alias` 缺失时，现场读到的表只留在内存，重启即丢（实测 55 s 后仍缺失）。
                //   此处**复用既有通路**：数据来自 `TryExportAliasTable`（与冷启动同源），
                //   写盘走 `NameCacheStore.TryWriteAlias`（含格式/条数/体量全部既有守卫），
                //   不新增缓存层、不新增校验层、不新增标志位。
                //   `verifiedAgainstLoc = true`：此处词表已就绪（上面刚判过），且建表时本身就
                //   带着词表做过 ③④ 交叉验证 —— 与冷启动路径的传参口径一致。
                if (aliasCacheMissed && loc.IsAliasTableReady)
                {
                    List<string> wFrom, wTo;
                    if (loc.TryExportAliasTable(out wFrom, out wTo))
                    {
                        string wNote;
                        if (NameCacheStore.TryWriteAlias(_gameFingerprint, wFrom, wTo, true, out wNote))
                            EvidenceLog("别名表现场读取成功，已补写回缓存（" + wFrom.Count + " 行）。");
                        else
                            EvidenceLog("别名表补写回缓存未成功：" + wNote);
                    }
                }
                SelfHealNameTableFromAlias(loc);
                // 【t29 返工 · R1④ 解耦】补建完成后词表已就绪 ⇒ 这里是「别名表 vs 词表」补验
                //   真正能跑成的地方（缓存命中路径的 import 之后也会调一次，谁先跑成算谁）。
                RunAliasVerificationOnce(loc, "缓存命中后台补建后");
            }
            catch (Exception ex)
            {
                _lastErrorNote = ex.GetType().Name;
                SaveErrorNote();
            }
            finally { _locBackfillRunning = false; }
        }

        /// <summary>
        /// 【R1③】自愈：把「名表缺失 / 但别名链能解析出中文名」的条目补进会话名表并重写名表缓存。
        ///   id 全集取自已装入的类型表（全库 814 条），中文名来自别名链 + 内存词表 ⇒ 无需重扫内存。
        /// </summary>
        private void SelfHealNameTableFromAlias(GameResLocator loc)
        {
            if (loc == null || !loc.IsAliasTableReady) return;
            List<string> ids = new List<string>();
            lock (_nameGate)
            {
                foreach (KeyValuePair<string, int> kv in _itemType)
                    if (kv.Key != null && kv.Key.Length > 0) ids.Add(kv.Key);
            }
            if (ids.Count == 0) return;

            Dictionary<string, string> healed = new Dictionary<string, string>();
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                if (LookupNameKey(id) != null) continue;              // 名表已有 ⇒ 保持既有值不动
                string zh = loc.ResolveLocalizedWithAlias(id);        // 别名优先 + 跟链到不动点 + 查词表
                if (zh != null && zh.Length > 0) healed[id] = zh;
            }
            if (healed.Count == 0) return;

            LoadNamesIntoTable(healed);
            string fp = _gameFingerprint;
            if (fp != null && fp.Length > 0)
            {
                string note;
                // 【t29 返工】重写名表缓存时必须带上**本次别名表条数**：写 0 会让下次启动
                //   拒绝这份缓存并强制重建（自愈）—— 这正是 R1-2 要的语义。
                // 【t34 · U1 修复】这里必须用**行数**（`AliasTableRowCount`），不能用去重键数
                //   （`AliasTableCount`）：名表缓存头的 `alias=` 与 `.alias` 的 `count` 都记行数，
                //   传去重键数会让两侧恒差 12（8709 vs 8697）⇒ 自愈写缓存之后每次启动
                //   都被 F4 的条数交叉判据误拒、退回现场全量重建（≈17 s）。
                // 【2026-09-26 · P2】以上两段所描述的口径与判据**已随 `alias=` 字段一并取消**：
                //   `TryWrite` 不再有形参，自愈写缓存与本路径其余写盘完全同口径，
                //   「行数 vs 去重键数」的混用风险从根上消失（U1 事故类别不再可能复发）。
                NameCacheStore.TryWrite(fp, SnapshotNameTable(), out note);   // 写失败不影响本会话
            }
            EvidenceLog("已用别名链补齐名表缺条 " + healed.Count + " 条（名表缓存已自愈）。");
        }

        /// <summary>会话名表的只读快照（用于自愈后重写名表缓存）。</summary>
        private Dictionary<string, string> SnapshotNameTable()
        {
            Dictionary<string, string> snap = new Dictionary<string, string>();
            lock (_nameGate)
            {
                foreach (KeyValuePair<string, ItemEntry> kv in _itemNames)
                {
                    ItemEntry e = kv.Value;
                    if (e == null || e.Zh == null || e.Zh.Length == 0) continue;
                    snap[kv.Key] = e.Zh;
                }
            }
            return snap;
        }

        /// <summary>
        /// 【R1④】两条路径一致性自检：抽样比较「名表直查」（缓存命中路径的取值口径）与
        ///   「别名链解析」（别名表路径的口径）。判据：两者**不得冲突**；别名链能补上名表缺条。
        ///   默认运行一次（毫秒级），结果上屏一条。
        /// </summary>
        private void AliasPathConsistencySelfTest()
        {
            string[] probes = new string[] {
                "cabbage:1", "cabbage:2", "cabbage:3",
                "surgeon_mistake_bones", "surgeon_mistake_brain", "surgeon_mistake_guts",
                "surgeon_mistake_skin", "surgeon_mistake_skull", "surgeon_mistake_heart",
                "prayer_base:1", "fertilizer:farming_5", "bones_1_1:2"
            };
            int agree = 0, aliasOnly = 0, missing = 0, diverge = 0;
            string diffs = "";
            for (int i = 0; i < probes.Length; i++)
            {
                string id = probes[i];
                string viaTable = LookupNameKey(id);
                string viaAlias = LookupNameKeyWithAlias(id);
                if (viaAlias == null) { missing++; continue; }
                if (viaTable == null) { aliasOnly++; continue; }
                if (viaTable != viaAlias)
                {
                    diverge++;
                    if (diffs.Length < 200)
                        diffs += (diffs.Length > 0 ? "; " : "") + id + "=表:" + viaTable + "/链:" + viaAlias;
                    continue;
                }
                agree++;
            }
            // 【t34 · T2】加「有效样本数 > 0」下限判据：原实现只要求 divege == 0，
            //   12 条样本**全空**（agree + aliasOnly == 0）也会 PASS —— 那是"没测到"，不是"通过"（t33 finding T2）。
            int valid = agree + aliasOnly;
            bool ok = (diverge == 0) && (valid > 0);
            // 【2026-09-26 收尾轮】本方法整体是**内部一致性自检**（方案A 取证），玩家无从据其行动
            //   ⇒ 走 EvidenceLog：默认不上屏，需要时开 GK2TRAINER_EVIDENCE=1 复现。
            EvidenceLog("【方案A 一致性自检】抽样 " + probes.Length + " 条：一致 " + agree
                + " / 仅别名链可得 " + aliasOnly + " / 两路皆无 " + missing + " / 冲突 " + diverge
                + "；有效样本 " + valid + "/" + probes.Length
                + (ok ? " ⇒ PASS"
                      : (diverge != 0 ? (" ⇒ FAIL（存在冲突）：" + diffs)
                                      : " ⇒ FAIL（有效样本为 0：本次没测到，不算通过）")));
        }

        /// <summary>英文物品 ID → 显示名：物品名表（游戏运行时提取）优先，查不到时退回 ID。
        /// 表中查找不触发任何扫描，故可安全用于 UI 线程。
        /// 【方案A 2026-09-26】取名顺序改为**对齐游戏规则**：
        ///   ① 原名直查（会话名表 → 内存词表）—— 保持既有行为，命中结果与接入前逐字相同；
        ///   ② 别名表跟链后查（游戏 `LLBase.L()`：别名优先、命中即跟链到不动点）；
        ///   ③ `id.Split(':')[0]` 一级（游戏 `ItemDef.GetHeader()` 的 Star 回退，规则本身就有）。
        /// 【2026-09-26 · P3 删除】原 ④ 级是本工具**自创**的 `head` / `head+"_1_1"` 两级启发式
        ///   （比游戏规则多退一层：`bones_1_1:2` → `bones`）。已整段删除 —— 到此为止的链路与
        ///   游戏一致，三级都落空即返回原始 id，不再猜测。
        /// </summary>
        private string DisplayName(string id)
        {
            if (id == null) return "?";

            // ①② 原名 / 别名链（游戏规则）
            string n = LookupNameKeyWithAlias(id);
            if (n != null && n.Length > 0)
            {
                // 【P2 修复 2026-09-26】精确命中的星级物品也要标注星级，否则与走回退链的同族
                // 变体显示不一致：例如 cabbage:1 精确命中「卷心菜」不加标注，而 cabbage:2 走
                // 别名回退却显示「卷心菜（银星）」。
                // 【2026-09-26 统一后缀】改为调用 ItemSuffix：星级与红白骷髅都在这里拼，
                // 两者都可缺省（红白均 0 ⇒ 不显示红白；后缀非数字 ⇒ 不显示星级）。
                return n + ItemSuffix(id);
            }

            int colon = id.IndexOf(':');
            if (colon > 0 && colon + 1 < id.Length)
            {
                string baseId = id.Substring(0, colon);

                // ③ 游戏规则本身的一级回退：基名（同样先走别名链，只增不减命中）
                string cand = LookupNameKeyWithAlias(baseId);
                if (cand != null && cand.Length > 0) return cand + ItemSuffix(id);
            }
            return id;
        }

        /// <summary>
        /// 【方案A】带别名表的候选键取名：原名直查（会话名表 → 内存词表）→ 别名链 → 链尾查会话名表。
        /// 顺序刻意保持「原名直查优先」：会话名表里已装入精确 id 与候选键，直查命中时结果与
        /// **接入前逐字相同**（回归安全），别名链只在直查落空时介入。
        /// 全程不触发任何内存扫描（<see cref="GameResLocator.ResolveLocalizedWithAlias"/> /
        /// <see cref="GameResLocator.ResolveAliasChain"/> 只查已建立的索引）⇒ 可安全用于 UI 线程。
        /// </summary>
        private string LookupNameKeyWithAlias(string key)
        {
            if (key == null || key.Length == 0) return null;

            // ① 原名直查（既有两条来源、既有顺序）
            string direct = LookupNameKey(key);
            if (direct != null && direct.Length > 0) return direct;

            if (_loc == null) return null;

            // ② 游戏规则：别名优先 —— 跟链到不动点后查内存中文词表
            string via = _loc.ResolveLocalizedWithAlias(key);
            if (via != null && via.Length > 0) return via;

            // ③ 链尾也查会话名表（名表内含候选键，与内存词表共用同一套候选键口径）
            string chain = _loc.ResolveAliasChain(key);
            if (chain != null && chain.Length > 0 && chain != key)
            {
                ItemEntry e;
                if (NameTableLookup(chain, out e) && e.Zh != null && e.Zh.Length > 0) return e.Zh;
            }
            return null;
        }

        /// <summary>
        /// 按一个候选键取中文名：先查会话名表，再查定位器的内存词表；都没有返回 null。
        /// 两条来源共用同一套候选键，避免只补其中一条导致行为不一致。
        /// </summary>
        private string LookupNameKey(string key)
        {
            if (key == null || key.Length == 0) return null;
            ItemEntry e;
            if (NameTableLookup(key, out e))
            {
                if (e.Zh != null && e.Zh.Length > 0) return e.Zh;
            }
            if (_loc != null)
            {
                string mem;
                if (_loc.TryGetLocalizedName(key, out mem) && mem.Length > 0) return mem;
            }
            return null;
        }

        /// <summary>
        /// 星级后缀（**本工具自己的标注用词**）。
        /// 数据的依据：`ItemDef.quality` 等于 id 里 ":N" 后缀的值，1/2/3 的三级阶梯有游戏数据佐证
        /// （169 个 qualityType=Star 的物品全部带 ":N"，且 0 例后缀 ≠ quality；
        ///  证据 `02_分析记录\_星级与读档机制_20260926\evidence\itemdef_full.csv`）。
        /// **须与游戏文案区分**（t5 评审 F5）：游戏界面并不出现「铜星/银星/金星」字样，
        /// 它用的是 `icon-quality-*` 图标与「青铜/白银」一类文案 —— 此处的三词是本修改器
        /// 为便于区分同名变体而加的**自有标注**，不是游戏原文。
        /// 未知或异常值（如 `fertilizer:peat` 这类非数字后缀）一律返回空串 —— 不猜测。
        /// </summary>
        private static string StarSuffix(string quality)
        {
            if (quality == "1") return "（铜星）";
            if (quality == "2") return "（银星）";
            if (quality == "3") return "（金星）";
            return "";
        }

        /// <summary>
        /// 红白骷髅文本（不含括号），取值来自 ItemDef **运行时字段**（+0xF4 / +0xF8），
        /// **不是**解析 id —— `guts_2_2:2` 的 id 段写 `_2_2` 而字段实为红1白1（814 条中唯一反例），
        /// 该物品在标定样本内且于新偏移上吻合。
        /// 规则（用户 2026-09-26 裁决）：
        ///   · 红白**均为 0** ⇒ 返回空串（不显示，避免满屏「红0白0」）；
        ///   · 否则返回「红X白Y」，**负值按数值显示**以保证一致性
        ///     （用户原话：「移除红1还是标成红-1保证一致性吧」）——
        ///     例：embalm_white_red:1（红-1白1）⇒「红-1白1」；surgeon_mistake_skin（红-1白-1）⇒「红-1白-1」。
        /// </summary>
        private string SkullText(string id)
        {
            if (id == null || id.Length == 0) return "";
            int[] v;
            lock (_nameGate) { _itemAttrs.TryGetValue(id, out v); }
            if (v == null || v.Length < 2) return "";
            if (v[0] == 0 && v[1] == 0) return "";
            return "红" + v[0] + "白" + v[1];
        }

        /// <summary>
        /// 物品显示名的后缀「（星级，红X白Y）」——两部分各自可缺省，都缺省时返回空串。
        /// 例：`bones_1_1:2` ⇒「（银星，红1白1）」；`bones_0_0:1` ⇒「（铜星）」；
        ///     `surgeon_mistake_skin`（无星级、红-1白-1）⇒「（红-1白-1）」。
        /// </summary>
        private string ItemSuffix(string id)
        {
            if (id == null || id.Length == 0) return "";
            string star = "";
            int c = id.IndexOf(':');
            if (c > 0 && c + 1 < id.Length) star = StarSuffix(id.Substring(c + 1));   // 含括号，或空串
            string skull = SkullText(id);
            if (star.Length == 0 && skull.Length == 0) return "";
            if (star.Length == 0) return "（" + skull + "）";
            if (skull.Length == 0) return star;
            // star 形如「（铜星）」→ 去掉收尾括号后与红白合并
            return star.Substring(0, star.Length - 1) + "，" + skull + "）";
        }

        // 【2026-09-26 删除】此处原有 `GetItemIdFromUi()`（返回下拉选中项的英文 id）。
        //   删除理由：两条写入路径（数量「应用」、一次性写入）改为**按地址**取对象后，
        //   它已**无任何调用点**（全仓计数 0）；且它的返回值只用于「按 id 查对象」这一
        //   已被判定为缺陷的做法。职责由 `<see cref="FindInvItemByAddr"/>` 承接。
        //   ⚠ 按项目既有约定不写出被删方法名之外的死代码检索词。

        /// <summary>
        /// 一次性写入：把背包中选中物品的数量改成框内填写的值（不锁定）。
        /// 写入前由 GameResLocator.WriteItemCount 做类名 + itemId + vtable 三重校验，
        /// 只写 +0x40 的 4 字节，写后复读确认。
        /// </summary>
        private void OnItemApplyClick(object sender, EventArgs e)
        {
            // 【T7-F3 修复】整体兜底：本处理器会调用 WriteItemCount → ReadLong/WriteBytes，
            // 句柄失效等竞态异常原先会逃逸到 WinForms 消息循环（弹「未处理异常」或终止进程）。
            // 行为与 OnTickCore 对齐：异常只记内部诊断 + 玩家化提示，绝不外抛。
            try
            {
                if (_loc == null || !_mem.IsOpen) { Log("还没有连接游戏，请先点「刷新」。"); return; }
                if (_scanBusy) { Log("正在读取游戏数据，请稍候再试。"); return; }
                // 【硬约束③·等待态零写入】未就绪绝不写内存（与 OnApplyOnceClick 同一道防线）
                if (!CanWrite()) { Log("游戏数据尚未就绪，请稍候或点「刷新」。"); return; }

                // 【必修缺陷修复 2026-09-26】不再按 id 查（同 id 多件会取错对象），改用下拉项自带地址
                InvEntry sel = _cmbItemId.SelectedItem as InvEntry;
                GameResLocator.ItemRef r = FindInvItemByAddr(sel != null ? sel.Address : 0);
                if (r == null)
                {
                    Log("这个物品目前不在背包里，请点「刷新」后重新选择。");
                    return;
                }
                int target;
                if (!int.TryParse(_txtItemLock.Text.Trim(), out target))
                { Log("请输入 0 ~ 999999 之间的数字。"); return; }
                if (target < 0 || target > 999999)
                { Log("数量请填 0 ~ 999999 之间的数字。"); return; }

                int old = _loc.ReadItemCount(r.Address);
                if (!_loc.WriteItemCount(r.Address, r.ItemId, target))
                {
                    Log("修改失败：该物品已不在背包中，请点「刷新」后重新选择。");
                    return;
                }
                int back = _loc.ReadItemCount(r.Address);
                r.Count = back;
                _selItemId = r.ItemId;
                _selItemAddr = r.Address;
                _lblItemCurrent.Text = back.ToString();
                SetInventory(_invItems);
                Log("已把「" + DisplayName(r.ItemId) + "」数量改为 " + back + "（原 " + old
                    + "）。按 Tab 打开背包即可看到。");
            }
            catch (Exception ex)
            {
                _lastErrorNote = ex.GetType().Name;   // 【T10-F2】只记类型名，绝不写进消息文本
                SaveErrorNote();          // 【T7-F4】诊断出口（会话临时目录，地址已过滤）
                Log("修改未成功，请重新点「刷新」后再试一次。");
            }
        }

        // ------------------------------------------------------------------
        // 进程生命周期三态状态机 + 游戏重启自动重连
        //
        // 三态（状态栏文案）：
        //   NO_PROCESS —— 没有任何 GraveyardKeeper2 进程 → 「等待游戏启动……」
        //   NOT_READY  —— 进程已在但尚未开始游戏（锚链未建立）→ 「等待游戏开始」
        //   READY      —— 已就绪 → 沿用正常的「冷刷新完成…」「热刷新…」文案
        //
        // 自动重连：发现「已连接的 PID 消失」且「出现了（新的）游戏进程」时，
        // 自动关闭旧句柄并重走一次完整冷刷新（LocateWorker 内部 Open 新 PID），无需用户点刷新。
        //
        // 节流（不忙轮询）：
        //   · 主监控 1 s 一次，只做进程存在性检查（FindGamePid），**零内存扫描**；
        //   · 无进程时不做任何扫描，CPU 保持低位；
        //   · 未就绪时退避重试（3→6→…→30 s 封顶），不是每轮都扫全堆；
        //   · 就绪后只走微秒级 IsSameSaveSlot()（身份引用，1 次内存读），不重复全堆扫描。
        //
        // 安全不变式：NO_PROCESS / NOT_READY 下**不写内存**、**不用启发式兜底选组**；
        //   进程消失时立刻清空所有「已定位地址」绑定数据，避免拿旧地址写入新进程。
        // ------------------------------------------------------------------

        private const int LC_NO_PROCESS = 0;
        private const int LC_NOT_READY = 1;
        private const int LC_READY = 2;
        /// <summary>
        /// 【t5·A-1】真失败态：反复重试仍无法确证当前存档。
        /// 必须与 LC_READY 严格区分 —— 旧版在此置 LC_READY，会被 MonitorLifecycle 的
        /// 「锚已失效 → 立即重定位」分支命中并清零重试计数，形成
        /// 「8 次退避 → 置 READY → 立即重启 → 再 8 次」的无限循环（每次重启都做完整冷刷新 + 全堆扫描）。
        /// 本态下写内存判据恒为 false，重试退避为分钟级。
        /// </summary>
        private const int LC_FAILED = 3;
        /// <summary>未就绪重试达到此次数后，仍失败即视为「真失败」并进入 LC_FAILED。</summary>
        private const int NOT_READY_MAX_RETRIES = 8;
        /// <summary>真失败态的重试间隔（毫秒）：足够长以避免扫描循环，又保留自愈能力。</summary>
        private const int FAILED_RETRY_MS = 60000;

        /// <summary>生命周期状态（后台线程写 / UI 线程读 → volatile）。</summary>
        private volatile int _lifeState = LC_NO_PROCESS;
        private DateTime _nextMonitorUtc = DateTime.MinValue;
        private DateTime _nextNotReadyRetryUtc = DateTime.MinValue;
        private int _notReadyRetries = 0;
        /// <summary>资源组是否已由「当前存档锚」唯一命中选定（就绪判定必须同时看它与锚链结果）。</summary>
        private volatile bool _groupSelected = false;
        /// <summary>【t21 时间优化①】本次冷刷新内是否已做过「未识别出资源组 → 立即重试一次」。</summary>

        /// <summary>
        /// 未就绪重试的退避毫秒数：**首次 1500 ms**，之后 3000→6000→…→30000 ms 封顶。
        /// 【t21 时间优化②】首步由 3 s 缩短到 1.5 s（verifier 实测：进程已在但未开始游戏期约省 1.5 s）。
        /// </summary>
        private static int NotReadyDelayMs(int retries)
        {
            if (retries <= 1) return 1500;
            int ms = 3000 * (retries - 1);
            return ms > 30000 ? 30000 : ms;
        }

        /// <summary>
        /// 【硬约束③·等待态零写入】写游戏内存的唯一判据。
        /// 只有「已就绪 + 资源组已选定 + 定位器与进程句柄有效」才为 true。
        /// OnTick 的定时锁定与两个写入按钮（「立即写入」「应用」）全部共用此判定 ——
        /// 未就绪（等待游戏启动 / 等待游戏开始 / 真失败 / 正在刷新）绝不触碰游戏内存。
        /// </summary>
        private bool CanWrite()
        {
            return _lifeState == LC_READY && _groupSelected && _loc != null && _mem.IsOpen;
        }

        /// <summary>
        /// 【审核轮 2026-09-26 · 已删除 SetLifeState】原「状态迁移统一入口」全文件零调用
        /// （20+ 处均为直接 `_lifeState = ...` 赋值），声明的纪律从未被执行；
        /// 反向启用它会改变行为（每次迁移都触发 RefreshFeatureAvailability），故删而不启。
        /// </summary>
        /// <remarks>
        /// 清空全部「与某个已定位存档绑定」的运行时数据。
        /// 进程消失 / 换进程时必须先清，否则旧地址会被用于新进程（误写风险）。
        /// </remarks>
        private void ClearAnchoredData()
        {
            _res = new Dictionary<string, GameResAtom>();
            _playerAtoms = null;
            _selItemAddr = 0;
            System.Threading.Interlocked.Exchange(ref _sciItemAddr, 0);   // 科学真身地址随锚失效一并作废
            _bagItemCache = null;
            _groupSelected = false;               // 【t19】清锚即视为未选组，避免热路径误复用旧选组结果
            SetInventory(new List<GameResLocator.ItemRef>());
            RefreshFeatureAvailability();
        }

        private void MonitorLifecycle()
        {
            if (_scanBusy) return;                       // 刷新进行中，不插队
            DateTime now = DateTime.UtcNow;
            if (now < _nextMonitorUtc) return;
            _nextMonitorUtc = now.AddSeconds(1);          // 主节流：1 s

            // 【P1-2 性能】就绪态下免进程枚举：原实现每秒 FindGamePid（4×GetProcessesByName
            // + 模糊兜底 GetProcesses），实测 2.275 ms + 1335 KB 分配。已连接且进程仍存活时
            // 没有必要枚举全部进程；只有「进程消失」时才回退到枚举 —— 重连路径完全不变。
            int pid;
            if (_lifeState == LC_READY && _pid != 0 && IsGameProcessAlive())
                pid = _pid;
            else
                pid = FindGamePid();
            if (pid == 0)
            {
                // ① 无游戏进程
                _notReadyRetries = 0;
                _nextNotReadyRetryUtc = DateTime.MinValue;
                if (_lifeState != LC_NO_PROCESS)
                {
                    _lifeState = LC_NO_PROCESS;
                    _pid = 0;
                    if (_mem.IsOpen) _mem.Close();
                    if (_loc != null) _loc.InvalidateInventory();
                    ClearAnchoredData();                  // 旧地址一律作废，防止误写新进程
                    SetStatus("等待游戏启动……", STEAM_WAIT);
                    LogEvent("游戏已退出，等待重新启动游戏……");   // 【T12】阶段切换 → 绕过去重
                }
                return;                                   // 无进程：零扫描
            }

            if (pid != _pid)
            {
                // ② 出现（新的）游戏进程 → 先等游戏窗口创建(=用户可见游戏窗口),再开始定位。
                // 【2026-09-24】启动早期(Logo/发行商页/加载进度条)定位注定失败;以主窗口句柄为闸,
                // 窗口未创建期间每轮只做零成本探测,不重试定位、不清锚。
                // (曾试 Process.Modules 检测 Assembly-CSharp.dll:Mono 托管程序集由运行时自行映射,
                //  不在 Win32 模块列表中,永远探测不到——已证伪移除。)
                if (!GameWindowCreated(pid))
                {
                    SetStatus("等待游戏启动……", STEAM_WAIT);
                    return;
                }
                _notReadyRetries = 0;
                _nextNotReadyRetryUtc = DateTime.MinValue;
                ClearAnchoredData();
                SetStatus("正在重新连接游戏……", STEAM_WAIT);
                LogEvent("检测到游戏，正在连接……");           // 【T12】阶段切换 → 绕过去重
                StartLocate(2);   // 【T12】触发源 2：检测到游戏进程自动重连 → 完成时用专属文案
                return;
            }

            // ②′ 进程未变但**身份引用**已失效（真读档 / 回主菜单）→ 全部回落「等待游戏开始」并自动重定位。
            // 【P0a 2026-09-26】判据改用 IsSameSaveSlot()：只认「静态槽不再指向缓存的 PlayerData」
            // 这一个决定性信号（外加 gameState 闸）。旧实现用 IsAnchorStillValid()（= 身份 ∧ **结构可读**），
            // 把「背包被清空（容器 size == 0）」这类**合法结构状态**也判成换档 ⇒ 清空重载；
            // 实测误判持续 57.8 s（527/4546 采样点 containerOk=0 而 slotOk=1、inGame=1）。
            // 结构不可读**不再**进入本分支：后果 = 不降态、不清数据、不重载；下一次热路径
            // 廉价校验（FindPlayerInventory / LocateWorker 分流）失败时走既有冷路径**重新解析一次**。
            // 【2026-09-24 恢复】此分支缺失导致读档后各缓存地址继续读旧档对象（§2.4:旧对象不释放）,
            // 显示旧数据直到手动点刷新。判据仅数次内存读,每秒一次开销可忽略。
            // 【t3 A-1】条件严格限定 LC_READY：真失败态（LC_FAILED）不得进入本分支，
            // 否则「清零重试计数 → 立即重启」会把 NOT_READY_MAX_RETRIES 完全抵消。
            if (_lifeState == LC_READY && _loc != null && (!_loc.IsSameSaveSlot() || !_loc.IsInGameSession()))
            {
                _lifeState = LC_NOT_READY;
                _notReadyRetries = 0;
                _nextNotReadyRetryUtc = DateTime.MinValue;
                ClearAnchoredData();
                SetStatus("等待游戏开始", STEAM_WAIT);
                LogEvent("检测到读档或返回主菜单，正在重新读取当前存档……");   // 【T12】阶段切换 → 绕过去重
                StartLocate(1);   // 【T12】触发源 1：读档/回主菜单自动重定位 → 完成时输出对称的专属文案
                return;
            }

            if (_lifeState == LC_FAILED)
            {
                // ③′ 真失败态：分钟级长退避重试（保留自愈能力，又消除「8 次退避 → 重启」的循环）。
                // 【t3 A-1】绝不在此清零 _notReadyRetries —— 那正是无限重启循环的成因。
                // 用户显式点「刷新」不受此限（StartLocate 立刻执行）。
                if (now < _nextNotReadyRetryUtc) return;
                _nextNotReadyRetryUtc = now.AddMilliseconds(FAILED_RETRY_MS);
                StartLocate(-1);  // 【T12】自动重试：保持原触发源（读档期间不得被误报成重连）
                return;
            }

            if (_lifeState == LC_NOT_READY)
            {
                // ③ 进程在但尚未就绪 → 退避重试。
                // 【2026-09-24】重试前先轻读 gameState(一次 4 字节):主菜单/logo 期定位注定失败,
                // 只做 2 s 一次的 gameState 轮询,不再发起全堆扫描;开始游戏(InGame)后才真正定位。
                if (now < _nextNotReadyRetryUtc) return;
                // 【t3 A-5】定位器可能尚未构造成功（构造期抛异常时 _loc 仍为 null）→
                // 带退避重新发起定位，避免 _loc.GetGameState() 空引用逃逸到消息循环。
                if (_loc == null)
                {
                    _nextNotReadyRetryUtc = now.AddMilliseconds(NotReadyDelayMs(_notReadyRetries + 1));
                    StartLocate(-1);  // 【T12】自动重试：保持原触发源
                    return;
                }
                if (_loc.GetGameState() == 0)
                {
                    _nextNotReadyRetryUtc = now.AddMilliseconds(2000);   // 主菜单:轻轮询,等玩家开始游戏
                    return;
                }
                int delay = NotReadyDelayMs(_notReadyRetries + 1);
                _nextNotReadyRetryUtc = now.AddMilliseconds(delay);
                StartLocate(-1);  // 【T12】自动重试：保持原触发源
            }
        }

        /// <summary>游戏主窗口是否已创建(可见游戏窗口的进程级信号;窗口销毁/未创建时句柄为 0)。</summary>
        private static bool GameWindowCreated(int pid)
        {
            try
            {
                System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid);
                return p.MainWindowHandle != System.IntPtr.Zero;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 【P0-④ 性能】游戏进程存活探活。
        /// 原实现每 250 ms 调一次 Process.GetProcessById（实测单次 4.188 ms → 16.8 ms/s 纯浪费）。
        /// 改为缓存 Process 实例并读 HasExited（.NET 侧已缓存退出状态，微秒级）；
        /// 仅 PID 变化（游戏重启）时重建实例。进程消失时对缓存的访问会抛异常 → 视为已退出，
        /// 清空缓存并交由 MonitorLifecycle 统一做重连与文案。
        /// </summary>
        private bool IsGameProcessAlive()
        {
            if (_pid == 0) return false;
            try
            {
                System.Diagnostics.Process p = _gameProc;
                if (p == null || p.Id != _pid)
                {
                    p = System.Diagnostics.Process.GetProcessById(_pid);
                    _gameProc = p;
                }
                return !p.HasExited;
            }
            catch
            {
                _gameProc = null;
                return false;
            }
        }

        /// <summary>
        /// 【P0-③】派发一次后台科学重定位（同一时刻只允许一个在跑）。
        /// UI 线程只做「起线程」，不做扫描 —— 这是消除「每 2 秒界面冻结 271 ms」的关键。
        /// </summary>
        private void StartSciRecheck(string itemId)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _sciRecheckRunning, 1, 0) != 0) return;
            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                try { SciRecheckWorker(itemId); }
                catch { }
                finally { System.Threading.Interlocked.Exchange(ref _sciRecheckRunning, 0); }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 【P0-③】后台重定位科学物品：结构链自带「当前存档」语义（读档后旧地址不可达即自动回落）。
        /// 命中 → 间隔回到 2 s；未命中 → 指数退避（2 s → 4 → 8 → … → 60 s 封顶），
        /// 避免「当前档还没有科学」时每 2 s 遍历一遍全部 974 个 WgoData。
        /// </summary>
        private void SciRecheckWorker(string itemId)
        {
            GameResLocator loc = _loc;
            if (loc == null) return;
            long fresh = 0;
            try { fresh = loc.FindItemViaWorld(itemId); }
            catch { }
            if (fresh != 0)
            {
                _sciRecheckInterval = 8;                     // 命中 → 恢复 2 s 节奏
            }
            else
            {
                int iv = _sciRecheckInterval * 2;
                _sciRecheckInterval = iv > 240 ? 240 : iv;   // 未命中 → 最长 60 s
            }
            long old = System.Threading.Interlocked.Read(ref _sciItemAddr);
            if (fresh != old)
            {
                System.Threading.Interlocked.Exchange(ref _sciItemAddr, fresh);
                if (InvokeRequired) BeginInvoke(new Action(RefreshFeatureAvailability));
                else RefreshFeatureAvailability();
            }
        }

        // ------------------------------------------------------------------
        // 定时写入
        // ------------------------------------------------------------------

        private void OnTick(object sender, EventArgs e)
        {
            // 【t3 A-5】定时器回调是 UI 线程消息循环的一部分 —— 异常逃逸会弹「未处理异常」
            // 对话框甚至终止进程。此处统一兜底：只记日志、绝不外抛。
            try { OnTickCore(); }
            catch (Exception ex)
            {
                // 【t3 C-4】异常不再静默、也不吐术语：玩家看到一句可操作提示，
                // 类型名与消息留给内部诊断（【T7-F4】经 SaveErrorNote 落到会话诊断文件）。
                _lastErrorNote = ex.GetType().Name;   // 【T10-F2】只记类型名，绝不写进消息文本
                SaveErrorNote();
                Log("读取游戏数据时出错，请重新点「刷新」。");
            }
        }

        private void OnTickCore()
        {
            // 【生命周期监控】必须在任何早退之前执行：
            // 否则游戏退出后 _mem 已关、_loc 已空，监控会停摆（这正是旧版不重连的原因）。
            MonitorLifecycle();

            if (_loc == null || !_mem.IsOpen) return;
            if (!IsGameProcessAlive()) return;   // 【P0-④】廉价探活，替代每轮 4.2 ms 的 GetProcessById

            // 【硬约束③】安全不变式：未就绪（等待游戏启动 / 等待游戏开始 / 真失败）绝不写内存
            bool canWrite = CanWrite();

            // 同一游戏资源可能挂多个界面项（当前版本已无此类项；本去重作为通用保护保留）：
            // 每轮同一 ResKey 只允许被第一个勾选项写入，避免两项互相覆盖。
            // 【P2-1】复用字段而非每轮 new List<string>()（250 ms 一轮，4 次/秒的纯垃圾）。
            List<string> resLocked = _resLockedThisTick;
            resLocked.Clear();
            foreach (Feature f in _features)
            {
                // 物品型功能（科学/science）:走 Item 层。主背包缓存优先,其次「刷新」时
                // 全堆解析出的研究台容器真身地址（分解产物放研究台容器,实测不在主背包）
                if (f.ItemId != null)
                {
                    // 【P0-③ 性能】科学结构链重定位改到**后台线程**执行。
                    //  原实现在 UI 线程每 ~2 s 同步跑一次 FindItemViaWorld —— 单次实测 272 ms
                    //（遍历 wgoDataByUidCache 全部 974 个 WgoData × 96 个字段槽），
                    //  造成界面每 2 秒冻结一次（UI 线程占用 154.7 ms/s 的 87.6% 来自这里）。
                    //  现在这里只做「到点派活」，绝不阻塞消息循环；失败走指数退避。
                    if (!_scanBusy && canWrite && ++_sciRecheckCounter >= _sciRecheckInterval)
                    {
                        _sciRecheckCounter = 0;
                        // 【P5 · 2026-09-26】与冷路径同一约定（见 LocateWorker 内的「bool inBag」守卫）:
                        //   **背包里已有该物品 ⇒ 不做世界链定位**。因为下一行 ia 优先取 LookupBagCache，
                        //   命中时 _sciItemAddr 根本不会被读取 ⇒ 那次世界链遍历没有消费者，是纯浪费。
                        //   仅当背包缓存未命中（science 在研究台等 WGO 容器里）才派发后台重定位。
                        if (LookupBagCache(f.ItemId) == 0) StartSciRecheck(f.ItemId);
                    }
                    long ia = LookupBagCache(f.ItemId);
                    if (ia == 0) ia = System.Threading.Volatile.Read(ref _sciItemAddr);
                    if (ia == 0 || _scanBusy) continue;
                    int curI = _mem.ReadInt(ia + GameResLocator.ITEM_COUNT);
                    string cs = curI.ToString();
                    if (f.Lbl.Text != cs) f.Lbl.Text = cs;
                    if (!f.Chk.Checked) continue;
                    if (!canWrite) continue;
                    float targetI;
                    if (!float.TryParse(f.Txt.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out targetI)) continue;
                    int ti = (int)targetI;
                    if (curI != ti)
                    {
                        if (_loc.WriteItemCount(ia, f.ItemId, ti))
                        {
                            if (!_sciTipShown)
                            {
                                _sciTipShown = true;
                                Log("科学：已修改。重新打开研究台即可看到新数值。");
                            }
                        }
                        else
                        {
                            // 写失败 = 地址可能失效（对象被消耗销毁）:清缓存,提示刷新重新定位
                            _bagItemCache = null;
                            System.Threading.Interlocked.Exchange(ref _sciItemAddr, 0);
                            _sciRecheckInterval = 8;      // 【P0-③】重置退避，下一轮立即重新定位
                            if (f.Lbl.Text != "-") f.Lbl.Text = "-";
                            Log("科学：该物品已不在背包中，请点「刷新」后重新选择。");
                        }
                    }
                    continue;
                }
                if (f.Missing) continue;
                GameResAtom a;
                if (!_res.TryGetValue(f.ResKey, out a)) continue;
                float cur = _mem.ReadFloat(a.Address + GameResLocator.ATOM_VALUE);
                string s = cur.ToString("G9");
                if (f.Lbl.Text != s) f.Lbl.Text = s;
                if (!f.Chk.Checked) continue;
                if (!canWrite) continue;                     // 等待态：只刷新显示，绝不写内存
                if (resLocked.Contains(f.ResKey)) continue;
                float target;
                if (!float.TryParse(f.Txt.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out target))
                    continue;
                if (Math.Abs(cur - target) > 0.0001f)
                    _mem.WriteFloat(a.Address + GameResLocator.ATOM_VALUE, target);
                resLocked.Add(f.ResKey);
                // 「点赞」可见性提示（一次性）：写入已即时生效，但常驻 HUD 仅在点赞变动/
                // 教堂品质变化/读档时重读（2026-09-23 决定性实验结论），给出用户可操作的刷新方法。
                if (f.Key == "likes" && !_likesTipShown)
                {
                    _likesTipShown = true;
                    // 【2026-09-25 文案更正】原「卖任意 1 件商品即可看到变化」是错误表述
                    //（小数增量不触发 HUD 重绘，实测 +0.06 / +0.84 均无效）。
                    Log("点赞：已生效，获得 +1👍 即可看到变化。");
                }
            }

            // 只读 4 字节直读内存，不经过定位器，避免与后台扫描争用
            if (_selItemAddr != 0)
            {
                int cur = _mem.ReadInt(_selItemAddr + GameResLocator.ITEM_COUNT);
                string cs = cur.ToString();
                if (_lblItemCurrent.Text != cs) _lblItemCurrent.Text = cs;
            }
        }

        private void OnApplyOnceClick(object sender, EventArgs e)
        {
            // 【T7-F3 修复】整体兜底：本处理器会遍历全部功能项并调用 WriteFloat / WriteItemCount，
            // 句柄失效等竞态异常原先会逃逸到 WinForms 消息循环。行为与 OnTickCore 对齐。
            try
            {
                // 【硬约束③·等待态零写入】必须先过写权限判定：按钮置灰是异步委托（存在
                // 「状态已变、界面尚未刷新」的窗口），此处是最后一道防线。
                if (!CanWrite()) { Log("游戏数据尚未就绪，请稍候或点「刷新」。"); return; }
                if (_scanBusy) { Log("正在读取游戏数据，请稍候再试。"); return; }

                List<string> resLocked = new List<string>();
                foreach (Feature f in _features)
                {
                    // 物品型功能（科学/science）:一次性写入（主背包缓存或研究台容器真身）
                    if (f.ItemId != null)
                    {
                        long ia = LookupBagCache(f.ItemId);
                        if (ia == 0) ia = System.Threading.Volatile.Read(ref _sciItemAddr);
                        float targetM;
                        if (ia != 0 && !_scanBusy &&
                            float.TryParse(f.Txt.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out targetM))
                        {
                            if (_loc.WriteItemCount(ia, f.ItemId, (int)targetM))
                            {
                                if (!_sciTipShown)
                                {
                                    _sciTipShown = true;
                                    Log("科学：已修改。重新打开研究台即可看到新数值。");
                                }
                            }
                            else
                            {
                                _bagItemCache = null;
                                System.Threading.Interlocked.Exchange(ref _sciItemAddr, 0);
                                _sciRecheckInterval = 8;      // 【P0-③】重置退避
                                Log("科学：该物品已不在背包中，请点「刷新」后重新选择。");
                            }
                        }
                        continue;
                    }
                    if (f.Missing) continue;
                    GameResAtom a;
                    if (!_res.TryGetValue(f.ResKey, out a)) continue;
                    if (resLocked.Contains(f.ResKey)) continue;
                    float target;
                    if (!float.TryParse(f.Txt.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out target))
                        continue;
                    _mem.WriteFloat(a.Address + GameResLocator.ATOM_VALUE, target);
                    resLocked.Add(f.ResKey);
                }

                // 【必修缺陷修复 2026-09-26】同上：按地址取对象，不按 id
                InvEntry sel2 = _cmbItemId.SelectedItem as InvEntry;
                GameResLocator.ItemRef r = FindInvItemByAddr(sel2 != null ? sel2.Address : 0);
                int targetCount;
                if (r != null && int.TryParse(_txtItemLock.Text.Trim(), out targetCount))
                {
                    if (_loc.WriteItemCount(r.Address, r.ItemId, targetCount))
                    {
                        r.Count = _loc.ReadItemCount(r.Address);
                        _lblItemCurrent.Text = r.Count.ToString();
                    }
                }
                Log("已写入一次。按 Tab 打开背包即可看到新数量。");
            }
            catch (Exception ex)
            {
                _lastErrorNote = ex.GetType().Name;   // 【T10-F2】只记类型名，绝不写进消息文本
                SaveErrorNote();          // 【T7-F4】诊断出口（会话临时目录，地址已过滤）
                Log("写入未成功，请重新点「刷新」后再试一次。");
            }
        }

        private void OnAllOffClick(object sender, EventArgs e)
        {
            foreach (Feature f in _features) f.Chk.Checked = false;
            Log("已取消全部锁定。");
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_timer != null) _timer.Stop();
            try { _mem.Close(); } catch { }
            // 退出即清理本次会话的临时缓存目录（异常退出另有 ProcessExit 兜底；
            // 本函数绝不递归删除 %TEMP% 本身或任何非本工具目录）
            try { NameCacheStore.CleanupCurrentSession(); } catch { }
        }
    }
}
