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
        private Label _lblTitle;
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
        private readonly Dictionary<string, ItemEntry> _itemNames = new Dictionary<string, ItemEntry>();
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
        /// <summary>下半区面板：物品数量修改区 + 日志区（同属「读取结果」，共用一块面板，中间一条内部分隔线）。</summary>
        private static readonly Rectangle PANEL_LOWER = new Rectangle(10, 402, 512, 134);

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
            using (Pen inner = new Pen(STEAM_SEP))
                e.Graphics.DrawLine(inner, PANEL_LOWER.Left + 6, 454, PANEL_LOWER.Right - 7, 454);
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

            Text = "守墓人2 修改器  v1.0   [By:东皇钟]";

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
            ClientSize = new Size(532, 566);
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
        /// 取客户区纵坐标 y 处的渐变颜色（与 OnPaintBackground 的 0% / 80% / 100% 三段定义一致）。
        /// 用途：Flat 样式的 CheckBox 会用 BackColor 填充复选框方框，而控件不支持真透明；
        /// 给它「本行所在位置的渐变同色」后，方框与整行看起来就是透明的 —— 既不会出现
        /// 白色实心方块（系统默认 ButtonFace），也不会出现深色补丁。
        /// 渐变在 22px 行高内的色差 &lt; 2 个色阶，肉眼不可见。
        /// </summary>
        private Color SteamGradientAt(int y)
        {
            int h = ClientSize.Height;
            if (h <= 0) return STEAM_GRAD_TOP;
            float p = ((float)y / h) / 0.8f;       // 0%→80% 区间内归一化；80% 之后保持底色
            if (p < 0f) p = 0f;
            if (p > 1f) p = 1f;
            return Color.FromArgb(
                (int)Math.Round(STEAM_GRAD_TOP.R + (STEAM_GRAD_BOTTOM.R - STEAM_GRAD_TOP.R) * p),
                (int)Math.Round(STEAM_GRAD_TOP.G + (STEAM_GRAD_BOTTOM.G - STEAM_GRAD_TOP.G) * p),
                (int)Math.Round(STEAM_GRAD_TOP.B + (STEAM_GRAD_BOTTOM.B - STEAM_GRAD_TOP.B) * p));
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
            _lblTitle = MakeLabel("守墓人 2  内存修改器", 16, 8, 400, 28, Color.White, 12f, true);
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
            MakeLabel("物品数量修改（下拉列表为背包中的物品）", 16, 404, 400, STEAM_TEXT, 9f, true);

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
            _txtLog = new TextBox();
            _txtLog.Left = 16; _txtLog.Top = 458; _txtLog.Width = 500; _txtLog.Height = 68;
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
            _lnkThanks.Left = 16; _lnkThanks.Top = 538; _lnkThanks.Width = 500; _lnkThanks.Height = 20;
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
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 必须在句柄创建之后再对齐：ComboBox 高度由字体决定，
            // 句柄未创建时 Height 只是默认值（远小于实际高度），拿它当基准会把按钮/标签压扁。
            AlignItemRow();
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

                // ---------------- 【热路径】锚有效 **且资源组已选定** → 不做任何全堆扫描 ----------------
                // 只按缓存地址重读数值（微秒级）；地址失效（换档/重载）时往下走冷路径。
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
            _suppressSel = true;
            _cmbItemId.BeginUpdate();
            _cmbItemId.Items.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                string d = DisplayName(items[i].ItemId) + " × " + items[i].Count;
                _cmbItemId.Items.Add(new InvEntry(items[i].ItemId, d));
                if (keep.Length > 0 && items[i].ItemId == keep) _cmbItemId.SelectedIndex = i;
            }
            _cmbItemId.EndUpdate();
            if (_cmbItemId.SelectedIndex < 0 && items.Count > 0) _cmbItemId.SelectedIndex = 0;
            _suppressSel = false;

            // 按钮/输入框可用性统一由 RefreshFeatureAvailability 决定（含就绪态判定），
            // 不再在此单独点亮 —— 那是「状态未就绪却能点应用」的旁路来源（t3 R-5/D-8）。
            RefreshFeatureAvailability();
            OnItemSelectionChanged(null, null);
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
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color.White))
                e.Graphics.FillPolygon(b, new Point[] {
                    new Point(cx - 7, cy - 4), new Point(cx + 7, cy - 4), new Point(cx, cy + 6) });
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
            GameResLocator.ItemRef r = FindInvItem(sel.Id);
            if (r == null) { _selItemAddr = 0; _lblItemCurrent.Text = "-"; return; }
            _selItemAddr = r.Address;
            _lblItemCurrent.Text = r.Count.ToString();
        }

        private GameResLocator.ItemRef FindInvItem(string id)
        {
            for (int i = 0; i < _invItems.Count; i++)
                if (_invItems[i].ItemId == id) return _invItems[i];
            return null;
        }

        // 【已删除 · t5 死代码清理】本节原有 3 个「背包轻量重枚举」成员（入口 + 工作线程 + 应用）：
        //   界面上的「查询」按钮已于 2026-09-23 移除，它们**没有任何事件绑定**，属 UI 死链；
        //   且其中一个还留有「⚠ 兜底定位…」旧文案（与玩家化要求方向相反）。
        //   职责已被「刷新」（全量重定位）+「下拉选中 + 250 ms 定时器」（自动读数量）完全覆盖。
        //   （按项目既有约定不列名，原文见 backup_src_t5\TrainerForm.cs）

        /// <summary>下拉项：显示「中文名 × 数量」（Id 仍是英文物品 ID，仅内部使用，不上屏）。</summary>
        private class InvEntry
        {
            public string Id;
            public string Display;
            public InvEntry(string id, string display) { Id = id; Display = display; }
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

        /// <summary>词表 ∩ 物品 id 全集 = 物品名表（精简掉 UI / 任务 / 对话等非物品键）。</summary>
        private static Dictionary<string, string> IntersectNames(Dictionary<string, string> whole, List<string> itemIds)
        {
            Dictionary<string, string> t = new Dictionary<string, string>();
            if (whole == null || itemIds == null) return t;
            for (int i = 0; i < itemIds.Count; i++)
            {
                string zh;
                if (whole.TryGetValue(itemIds[i], out zh) && zh != null && zh.Length > 0)
                    t[itemIds[i]] = zh;
            }
            return t;
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
            LoadNamesIntoTable(table);
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
                    Dictionary<string, string> whole = new Dictionary<string, string>();
                    loc.CopyLocalizedNames(whole);
                    Dictionary<string, string> itemTable = IntersectNames(whole, defIds);

                    if (itemTable.Count < NameCacheStore.MinEntries)
                    {
                        // 【t3 C-1】把「静默失效」变成可见提示：诊断在 loc.Diagnostics 里
                        Log("物品名称暂时读取不足，稍后会自动再读一次。");
                        return;
                    }

                    string wrNote;
                    if (!NameCacheStore.TryWrite(fp, itemTable, out wrNote))
                    {
                        // 缓存写不进去（目录不可写等）→ 表留在内存，刷新仍然不必重扫
                        LoadNamesIntoTable(itemTable);
                        _namesReady = true;
                    }
                    SetStatus("物品名称已加载完成。", STEAM_TEXT);
                    RefillInventoryNames();     // 【P0-②】把中文名推上界面
                }
                finally { mem.Close(); }
            }
            catch (Exception ex)
            {
                // 【t3 C-1】预热异常不再静默（旧版空 catch 会让「整表提取失效」完全无痕）
                Log("物品名称读取失败（" + ex.GetType().Name + "），已先使用物品编号显示。");
            }
            finally
            {
                _warmupRunning = false;
            }
        }

        /// <summary>英文物品 ID → 显示名：物品名表（游戏运行时提取）优先，查不到时退回 ID。
        /// 表中查找不触发任何扫描，故可安全用于 UI 线程。</summary>
        private string DisplayName(string id)
        {
            ItemEntry e;
            if (id != null && NameTableLookup(id, out e))
            {
                if (e.Zh != null && e.Zh.Length > 0) return e.Zh;
            }
            if (id != null && _loc != null)
            {
                string mem;
                if (_loc.TryGetLocalizedName(id, out mem) && mem.Length > 0) return mem;
            }
            return id == null ? "?" : id;
        }

        /// <summary>取当前下拉选中项对应的英文物品 ID（下拉显示中文，内部始终用 ID）。</summary>
        private string GetItemIdFromUi()
        {
            // 【T8-F3 处置】原实现末尾有一个「从自由文本里抠 (itemId)」的兜底分支
            //（ParseItemIdText）：`_cmbItemId.DropDownStyle = DropDownList` 之后，
            // 玩家无法手打文本、未选中时 Text 为空 → 该分支**实际不可达**，已整体删除；
            // 这里退化为「返回选中项显示文本」，与原兜底在不可达前提下的行为等价。
            string t = _cmbItemId.Text;
            if (t == null) t = "";
            t = t.Trim();
            InvEntry sel = _cmbItemId.SelectedItem as InvEntry;
            if (sel != null && (t.Length == 0 || t == sel.ToString())) return sel.Id;
            return t;
        }

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

                string id = GetItemIdFromUi();
                GameResLocator.ItemRef r = FindInvItem(id);
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
        //   · 就绪后只走微秒级 IsAnchorStillValid()，不重复全堆扫描。
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

        /// <summary>状态迁移统一入口：改状态 + 同步界面可用性，避免多处漏调用导致「按钮可点但不可写」。</summary>
        private void SetLifeState(int s)
        {
            _lifeState = s;
            RefreshFeatureAvailability();
        }

        /// <summary>
        /// 清空全部「与某个已定位存档绑定」的运行时数据。
        /// 进程消失 / 换进程时必须先清，否则旧地址会被用于新进程（误写风险）。
        /// </summary>
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

            // ②′ 进程未变但存档锚已失效（读档 / 回主菜单）→ 全部回落「等待游戏开始」并自动重定位。
            // 【2026-09-24 恢复】此分支缺失导致读档后各缓存地址继续读旧档对象（§2.4:旧对象不释放）,
            // 显示旧数据直到手动点刷新。IsAnchorStillValid 仅数次内存读,每秒一次开销可忽略。
            // 【t3 A-1】条件严格限定 LC_READY：真失败态（LC_FAILED）不得进入本分支，
            // 否则「清零重试计数 → 立即重启」会把 NOT_READY_MAX_RETRIES 完全抵消。
            if (_lifeState == LC_READY && _loc != null && (!_loc.IsAnchorStillValid() || !_loc.IsInGameSession()))
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
                        StartSciRecheck(f.ItemId);
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

                string id = GetItemIdFromUi();
                GameResLocator.ItemRef r = FindInvItem(id);
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
