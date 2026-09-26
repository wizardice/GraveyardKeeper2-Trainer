using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace GK2Trainer
{
    /// <summary>
    /// 一个 GameResAtom 实例。Mono 对象布局（32 字节，已实测验证）：
    ///   +0x00  vtable 指针
    ///   +0x08  sync block (0)
    ///   +0x10  type  -> System.String (如 "money")
    ///   +0x18  value -> float
    /// </summary>
    public class GameResAtom
    {
        public long Address;
        public long VTable;
        public string TypeName;
        public float Value;

        public override string ToString()
        {
            return (TypeName == null ? "?" : TypeName) + " = " + Value.ToString("G9");
        }
    }

    /// <summary>
    /// 动态定位器：完全不依赖硬编码地址，通过 Mono 对象结构自识别 + 类名校验
    /// 在运行时发现数据。原理等价于 Cheat Engine 的 Mono Dissect。
    ///
    /// 已实测确认的 Mono 结构偏移（Unity 6 / mono-2.0-bdwgc）：
    ///   MonoVTable + 0x00 -> MonoClass*
    ///   MonoClass  + 0x48 -> name (char*)
    ///   MonoClass  + 0x50 -> name_space (char*)
    ///   MonoString + 0x10 -> length (int32)
    ///   MonoString + 0x14 -> utf16 chars
    /// </summary>
    public class GameResLocator
    {
        public const int MONO_VTABLE_KLASS = 0x00;
        public const int MONO_CLASS_NAME = 0x48;
        public const int MONO_CLASS_NAMESPACE = 0x50;
        public const int MONO_STRING_LENGTH = 0x10;
        public const int MONO_STRING_CHARS = 0x14;

        // ---- 真 vtable 标定与静态字段读取（均为「结构偏移」，不是地址）----
        public const int MONO_VTABLE_MONODOMAIN = 0x10;    // [VT+0x10] == MonoDomain（同进程内所有真 vtable 同值）
        public const int MONO_VTABLE_RUNTIMETYPE = 0x18;   // [VT+0x18] → RuntimeType
        public const int MONO_VTABLE_STATIC_DATA = 0x68;   // [VT+0x68] → 静态字段数据块
        public const int STATIC_SLOT_PLAYERDATA = 0x28;    // static_data 块内 <PlayerData>k__BackingField
        public const int MAINGAME_GAMESTATE = 0x120;       // MainGame 实例 gameState(int:0=MainMenu,1=InGame;2026-09-24 主菜单/档内差分实测)
        // ---- PlayerData / Inventory 字段偏移 ----
        public const int PLAYERDATA_INVENTORY = 0x58;
        public const int PLAYERDATA_TOOLBELT = 0x60;
        public const int PLAYERDATA_GAMERES = 0x80;
        public const int INVENTORY_CONTAINER = 0x38;
        // ---- 参与校验的类型名 ----
        public const string MAIN_GAME_CLASS = "MainGame";
        public const string RUNTIMETYPE_CLASS = "RuntimeType";
        public const string PLAYERDATA_CLASS = "PlayerData";
        public const string INVENTORY_CLASS = "Inventory";
        /// <summary>【硬约束①】PlayerData+0x80 指向对象的期望类名（运行时校验用）。
        /// 本机正式版实测：PD+0x80 → 类名 "GameRes" → +0x10 = List`1（resValues，_size=92）。</summary>
        public const string GAMERES_CLASS = "GameRes";

        public const int ATOM_VTABLE = 0x00;
        public const int ATOM_SYNC = 0x08;
        public const int ATOM_TYPE = 0x10;
        public const int ATOM_VALUE = 0x18;
        public const int ATOM_SIZE = 0x20;

        private readonly ProcessMemory _mem;
        private readonly List<long> _heapStart = new List<long>();
        private readonly List<long> _heapEnd = new List<long>();
        private long _heapLo = long.MaxValue;
        private long _heapHi = 0;
        private readonly Dictionary<long, string> _classNameCache = new Dictionary<long, string>();
        /// <summary>
        /// 【t3 A-2】类名缓存的同步门。本对象被 UI 线程（OnTick 读数）与后台线程
        /// （定位线程 / 预热线程 / 科学重定位）共享，Dictionary 并发读写会损坏内部结构
        /// （表现为假命中或异常）。对照 TrainerForm._nameGate 的正面样板：
        /// 锁只保护字典访问，读内存一律放在锁外（幂等，重复读无害）。
        /// </summary>
        private readonly object _classGate = new object();
        /// <summary>【t3 A-2】中文词表索引的同步门（同上；<see cref="_locNames"/> 跨线程共享）。</summary>
        private readonly object _locGate = new object();

        public long GameResAtomVTable = 0;
        /// <summary>
        /// 【P0-② 分段早停】锚引用扫描的分块大小。区域按地址升序累积到此上限即切一块，
        /// 块内并行扫描 + 流式全链校验，命中即停。选 256 MB：本机实测合格候选出现在
        /// 136 MB 处，单块即可覆盖，而最坏情况（需扫完）也只多付一次分块开销。
        /// </summary>
        private const long EARLY_STOP_BLOCK_BYTES = 256L * 1024 * 1024;        public long PlayerResArrayBase = 0;
        public int PlayerResCount = 0;
        public readonly List<string> Diagnostics = new List<string>();
        /// <summary>【t3 C-1/C-2/C-3】诊断列表的同步门（多条后台线程都会写）。</summary>
        private readonly object _diagGate = new object();

        // 扫描统计
        public int StatCandidates = 0;
        public int StatDistinctVtables = 0;
        public int StatVtablesChecked = 0;

        public GameResLocator(ProcessMemory mem)
        {
            _mem = mem;
            BuildHeapIndex();
        }

        /// <summary>
        /// 【P0-⑤】带「上次已标定的 GameResAtom 真 vtable」的构造（同一游戏进程内复用）。
        /// vtable 由 Mono 运行时在加载期创建，**同一次进程运行内稳定**（读档/换档不会改变它），
        /// 因此换档触发的再次冷刷新不必重扫全堆去标定它。
        /// 使用前仍会做一次类名校验（见 <see cref="RefreshCurrentSaveAnchor"/>），
        /// 不通过即清零回落完整自举 —— 宁可不定位，不可选错。
        /// </summary>
        public GameResLocator(ProcessMemory mem, long knownAtomVTable)
        {
            _mem = mem;
            GameResAtomVTable = (knownAtomVTable > 0x10000) ? knownAtomVTable : 0;
            BuildHeapIndex();
        }

        // ------------------------------------------------------------------
        // 堆区间索引（快路径 + 精确二分）
        // ------------------------------------------------------------------

        /// <summary>
        /// 【审核轮 2026-09-26 · F6】Mono 堆区域判据（唯一权威版本）：
        /// 已提交、非 Guard / NoAccess、私有内存、≥ 4 KB。
        /// BuildHeapIndex 与 HeapRegions 共用，避免筛选条件漂移
        /// （原两处逐字重复的注释自称「避免漂移」却已各自一份 —— 此即证据）。
        /// ⚠ 刻意与 ProcessMemory.FilterScanRegions（引用扫描的宽松版）保持不同：
        ///   后者无 MEM_PRIVATE / 尺寸下限，属 HANDOFF §1.3-2 方案B 既有裁决，勿合并。
        /// </summary>
        private static bool IsMonoHeapRegion(MemRegion r)
        {
            if (r.State != ProcessMemory.MEM_COMMIT) return false;
            if ((r.Protect & ProcessMemory.PAGE_GUARD) != 0) return false;
            if ((r.Protect & ProcessMemory.PAGE_NOACCESS) != 0) return false;
            if (r.Type != ProcessMemory.MEM_PRIVATE) return false;
            if (r.Size < 0x1000) return false;
            return true;
        }

        private void BuildHeapIndex()
        {
            List<MemRegion> regs = _mem.GetRegions();
            List<MemRegion> keep = new List<MemRegion>();
            foreach (MemRegion r in regs)
            {
                if (!IsMonoHeapRegion(r)) continue;
                keep.Add(r);
            }
            keep.Sort(delegate(MemRegion a, MemRegion b) { return a.Base.CompareTo(b.Base); });
            for (int i = 0; i < keep.Count; i++)
            {
                if (_heapEnd.Count > 0 && keep[i].Base <= _heapEnd[_heapEnd.Count - 1])
                {
                    long e = keep[i].Base + keep[i].Size;
                    if (e > _heapEnd[_heapEnd.Count - 1]) _heapEnd[_heapEnd.Count - 1] = e;
                }
                else
                {
                    _heapStart.Add(keep[i].Base);
                    _heapEnd.Add(keep[i].Base + keep[i].Size);
                }
            }
            if (_heapStart.Count > 0)
            {
                _heapLo = _heapStart[0];
                _heapHi = _heapEnd[_heapEnd.Count - 1];
            }
        }

        /// <summary>
        /// 【t3 C-1/C-2/C-3】把「被吞掉的异常」变成可读诊断（不再静默降级）：
        /// 关键路径（ItemDef 枚举 / 资源结构链 / 科学结构链）的异常一律登记到
        /// <see cref="Diagnostics"/>，供日志与排障工具读取；列表上限 32 条防止无限增长。
        /// </summary>
        private void AddDiagnostic(string where, Exception ex)
        {
            AddNote(where + " 失败：" + ex.GetType().Name + " " + ex.Message);
        }

        /// <summary>登记一条内部诊断（不上屏；供排障与工具读取）。列表上限 32 条。</summary>
        private void AddNote(string msg)
        {
            lock (_diagGate)
            {
                if (Diagnostics.Count >= 32) Diagnostics.RemoveAt(0);
                Diagnostics.Add(msg);
            }
        }

        /// <summary>
        /// 【P0-②】把区域按地址升序切成总大小不超过 maxBytes 的块。
        /// 用途：分段早停 —— 从最低地址开始逐块扫描校验，命中即可停止，语义与全扫等价。
        /// </summary>
        private static List<List<MemRegion>> SplitIntoBlocks(List<MemRegion> regions, long maxBytes)
        {
            List<List<MemRegion>> blocks = new List<List<MemRegion>>();
            if (regions == null || regions.Count == 0) return blocks;
            List<MemRegion> cur = new List<MemRegion>();
            long acc = 0;
            for (int i = 0; i < regions.Count; i++)
            {
                MemRegion r = regions[i];
                if (cur.Count > 0 && acc + r.Size > maxBytes)
                {
                    blocks.Add(cur);
                    cur = new List<MemRegion>();
                    acc = 0;
                }
                cur.Add(r);
                acc += r.Size;
            }
            if (cur.Count > 0) blocks.Add(cur);
            return blocks;
        }

        /// <summary>区域集合的总字节数（仅用于日志展示扫描量）。</summary>
        private static long TotalBytes(List<MemRegion> regions)
        {
            long t = 0;
            if (regions == null) return 0;
            for (int i = 0; i < regions.Count; i++) t += regions[i].Size;
            return t;
        }

        /// <summary>地址是否落在托管堆区间内</summary>
        public bool InHeap(long addr)
        {
            if (addr < _heapLo || addr >= _heapHi) return false;
            int lo = 0, hi = _heapStart.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (addr < _heapStart[mid]) hi = mid - 1;
                else if (addr >= _heapEnd[mid]) lo = mid + 1;
                else return true;
            }
            return false;
        }

        /// <summary>快速预判（仅总体范围），用于扫描热路径</summary>
        private bool MaybeHeap(long addr) { return addr >= _heapLo && addr < _heapHi; }

        /// <summary>
        /// 托管堆区间总字节数。
        /// 【保留说明】t3 曾列为死代码，但它是**诊断/性能复核的唯一入口**：
        ///   本轮的只读性能探针（02_分析记录\_代码审查_20260924\tools\Probe-*.cs）用它
        ///   计算「扫描量 / 全堆比例」，删除会破坏这些可复跑证据工具。故保留并在此标注用途。
        /// </summary>
        public long HeapBytes
        {
            get
            {
                long t = 0;
                for (int i = 0; i < _heapStart.Count; i++) t += _heapEnd[i] - _heapStart[i];
                return t;
            }
        }

        // ------------------------------------------------------------------
        // Mono 元数据读取
        // ------------------------------------------------------------------

        /// <summary>
        /// 掩掉对象首字最低位（GC 标记位）。
        ///
        /// 【依据·实测】2026-09-26 现场取证（`02_分析记录\_星级与读档机制_20260926\
        /// evidence\GC标记位污染vtable_实测取证_20260926.md`）：
        /// 游戏的 Mono 运行时是 `mono-2.0-bdwgc.dll`（BDW / Boehm GC）。3,402 点采样中，
        /// `*(PlayerData)` 读到的**最低位为 1 共 4 次，且全部落在判据失败点上**；同一份数据里
        /// `slotRaw`（存的是对象**地址**）0 次为奇数。高频探针（~16 ms）抓到一段**连续 6 点、
        /// 持续 78 ms** 的稳定窗口，期间该值**恒为同一个奇数**（同一对象、同一最低位翻转），
        /// 且容器校验同步失败（污染沿容器链传播）。
        /// 后果：vtable 解析失败 ⇒ 类名判定失败 ⇒ 结构判据误报 ⇒ 原判据清空重载（用户报告的
        /// 「正常游玩中被识别为读档」）。污染窗口约 78 ms / 轮询 1 s ⇒ 单次过图撞上概率约 8%，
        /// 解释了该 bug 的偶发性。
        ///
        /// 【安全性】托管对象指针按 8 字节对齐，最低 3 位恒为 0 ⇒ 掩掉最低位对**正常指针无损**，
        /// 对被借用的标记位则正好还原真值。
        /// ⚠ 「8 字节对齐」是**工程前提**（本仓库未引 Mono/BDW 源码作为依据），
        /// 其**实测支持**为：t18 复算 7 份原始 CSV 共 114,726 个采样点，`slotRaw % 8 == 0` 为 **100.000%**。
        /// 与机制推测一样，属「前提 + 实测支持」，不得写成已由源码证明。
        ///
        /// 【作用边界（t18 评审 F4，措辞纪律）】本掩码只消除**「对象首字被标记位污染」**这一个成因。
        /// t17 独立验证还在同一场景观察到**另一种**误判形态：`pdVtRaw` 为**偶数**、
        /// `pdClassOk=1` 而 **`containerOk=0`**（容器链上的子判据失败，见
        /// `evidence/GC标记位污染vtable_实测取证_20260926.md` 的口径更正段）。
        /// 该形态**不在本掩码覆盖范围内**，属另立跟踪项。
        /// ⇒ **对外措辞统一为「消除了成因一」，不得写成「误判已彻底消除」。**
        ///
        /// 【机制说明】"标记位借用对象首字最低位"这一**具体实现属推测**（未查 Mono/BDW 源码确认）；
        /// 但"最低位偶发置 1 且必然导致类名解析失败"是**实测确定**的，本掩码针对该现象，不依赖机制命名。
        /// </summary>
        public const long GC_MARK_BIT_MASK = ~1L;

        public string GetClassName(long vtable)
        {
            if (vtable == 0) return null;
            vtable &= GC_MARK_BIT_MASK;          // 去掉 GC 借用的标记位（见上）
            string cached;
            bool hit;
            lock (_classGate) { hit = _classNameCache.TryGetValue(vtable, out cached); }
            if (hit) return cached;
            string name = null;
            long klass = _mem.ReadLong(vtable + MONO_VTABLE_KLASS);
            if (klass > 0x10000 && klass < 0x7FFFFFFF0000L)
            {
                long namep = _mem.ReadLong(klass + MONO_CLASS_NAME);
                if (namep > 0x10000 && namep < 0x7FFFFFFF0000L)
                {
                    string s = _mem.ReadAscii(namep, 96);
                    if (s != null && s.Length > 0 && s.Length < 96) name = s;
                }
            }
            // 竞态下可能重复读同一 vtable（结果相同，幂等）→ 后写覆盖无害
            lock (_classGate) { _classNameCache[vtable] = name; }
            return name;
        }

        public string ReadMonoString(long strObj)
        {
            if (strObj == 0) return null;
            int len = _mem.ReadInt(strObj + MONO_STRING_LENGTH);
            if (len <= 0 || len > 256) return null;
            byte[] b = _mem.ReadBytes(strObj + MONO_STRING_CHARS, len * 2);
            if (b == null || b.Length != len * 2) return null;
            string s = Encoding.Unicode.GetString(b);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x20 || c > 0x7E) return null;
            }
            return s;
        }

        // ------------------------------------------------------------------
        // 核心：并行一次遍历发现全部 GameResAtom
        // ------------------------------------------------------------------

        private const int CHUNK = 4 * 1024 * 1024;
        private const int OVERLAP = 0x40;

        /// <summary>
        /// 可扫描的托管堆区域（已提交、非 Guard / NoAccess 的私有区）。
        /// FindAllAtoms 与 ItemDef 枚举共用，避免两处筛选条件漂移。
        /// </summary>
        private List<MemRegion> HeapRegions()
        {
            List<MemRegion> targets = new List<MemRegion>();
            List<MemRegion> regs = _mem.GetRegions();
            foreach (MemRegion r in regs)
            {
                if (!IsMonoHeapRegion(r)) continue;
                targets.Add(r);
            }
            return targets;
        }

        /// <summary>
        /// 【P0-⑤ 自举加速】找一个「类名为 GameResAtom」的真 vtable：按地址升序分段扫描，
        /// 段内只做**纯内存**的候选计数（vt 落堆区间 + sync==0 + type 指针落堆区间 + value 有限），
        /// 零跨进程类名读；收满一段后只对出现次数最多的少数 vtable 做类名校验，命中即返回。
        /// 全部段落失败返回 0（调用方回落完整 FindAllAtoms，保证正确性 —— 宁慢不选错）。
        ///
        /// 正确性与旧实现的等价性：校验规则与 FindAllAtoms 一致（vtable 类名 == "GameResAtom"）；
        /// 返回值只用于取 MonoDomain（同进程内所有真 vtable 的 +0x10 同值），无需枚举全部原子。
        /// 实测：全堆 3.3~4.0 s → 数十至数百毫秒。
        /// </summary>
        private long FastBootstrapVTable()
        {
            const long SEG_BYTES = 64L * 1024 * 1024;    // 每段 64 MB
            // 段大小与段数经实测选定：16 MB×48 段会把「真 vtable 的出现频次」稀释到各段，
            // 反而更晚命中（实测 622 MB / 2517 ms）；64 MB×16 段实测 510 MB / 1929 ms，最优。
            const int MAX_SEGS = 16;                     // 最多扫 16 段（1 GB）后回落全扫
            const int MIN_CAND = 256;                    // 每段至少校验的候选数（按频次降序补齐）
            // 说明：只出现 1 次的真 vtable（段内仅 1 个该类的对象）也可能命中，
            // 因此每段至少校验 256 个候选（约 1 ms 的类名读），而不止频次 ≥2 的那批。

            List<MemRegion> heap = HeapRegions();
            List<List<MemRegion>> segs = SplitIntoBlocks(heap, SEG_BYTES);
            int segLimit = segs.Count < MAX_SEGS ? segs.Count : MAX_SEGS;

            // 【分段顺序：两端交替】托管堆里对象的分布随游戏行为变化（本机实测：GameResAtom
            //  集中在高地址区，低端前 1 GB 几乎为空）。因此按「末段 → 首段 → 倒数第二段 → 第二段…」
            //  推进，无论原子堆在哪一端都只需 1~2 段即可命中；即便两端都没有，也会逐段扫完
            //  （最坏 = 扫 segLimit 段后回落完整扫描）。
            //  ⚠ 该顺序只影响"先扫哪里"，**不影响正确性**：自举只需要"任一 GameResAtom 的
            //   真 vtable"，而同一进程内该类所有实例共享同一 vtable，取哪个实例都等价。
            List<int> order = new List<int>();
            int loIdx = 0, hiIdx = segs.Count - 1;
            while (loIdx <= hiIdx && order.Count < segLimit)
            {
                order.Add(hiIdx--);
                if (loIdx <= hiIdx && order.Count < segLimit) order.Add(loIdx++);
            }

            long scanned = 0;
            long lo = _heapLo, hi = _heapHi;
            byte[] buf = new byte[CHUNK];
            // 跨段累计频次：真 vtable 被大量对象共享（本机 2350 个原子共享同一 vtable），
            // 而谓词弱造成的假阳性 vtable 是散列值，几乎都只出现 1 次 —— 用频次分层是关键。
            Dictionary<long, int> vtCount = new Dictionary<long, int>();
            HashSet<long> attempted = new HashSet<long>();

            for (int s = 0; s < order.Count; s++)
            {
                List<MemRegion> seg = segs[order[s]];
                for (int ri = 0; ri < seg.Count; ri++)
                {
                    MemRegion r = seg[ri];
                    long off = 0;
                    while (off < r.Size)
                    {
                        int want = (int)Math.Min((long)CHUNK, r.Size - off);
                        if (want < ATOM_SIZE) break;
                        int got = _mem.ReadInto(r.Base + off, buf, want);
                        if (got > ATOM_SIZE)
                        {
                            int limit = got - ATOM_SIZE;
                            for (int i = 0; i <= limit; i += 8)
                            {
                                long vt = BitConverter.ToInt64(buf, i + ATOM_VTABLE);
                                if (vt < lo || vt >= hi) continue;
                                if (BitConverter.ToInt64(buf, i + ATOM_SYNC) != 0) continue;
                                long tp = BitConverter.ToInt64(buf, i + ATOM_TYPE);
                                if (tp < lo || tp >= hi) continue;
                                float val = BitConverter.ToSingle(buf, i + ATOM_VALUE);
                                if (float.IsNaN(val) || float.IsInfinity(val)) continue;
                                if (val > 1e12f || val < -1e12f) continue;
                                int c;
                                vtCount.TryGetValue(vt, out c);
                                vtCount[vt] = c + 1;
                            }
                        }
                        scanned += want;
                        if (want < CHUNK) break;
                        off += want - (off + want < r.Size ? OVERLAP : 0);
                    }
                }
                if (vtCount.Count == 0) continue;

                // 按频次降序：先校验「频次 ≥2 的候选」（真 vtable 必落此列），最多 512 个；
                // 再补足到至少 64 个候选。每段只校验尚未尝试过的 vtable。
                List<KeyValuePair<long, int>> sorted = new List<KeyValuePair<long, int>>(vtCount);
                sorted.Sort(delegate(KeyValuePair<long, int> a, KeyValuePair<long, int> b)
                {
                    return b.Value.CompareTo(a.Value);
                });
                int tried = 0;
                for (int i = 0; i < sorted.Count && tried < 512; i++)
                {
                    if (i >= MIN_CAND && sorted[i].Value < 2) break;
                    long vt = sorted[i].Key;
                    if (attempted.Contains(vt)) continue;
                    attempted.Add(vt);
                    tried++;
                    long klass = _mem.ReadLong(vt);
                    if (klass <= 0x10000 || klass >= 0x7FFFFFFF0000L) continue;
                    if (GetClassNameUncached(vt) == "GameResAtom")
                    {
                        AnchorLog.Add("① 自举加速：扫 " + (scanned / 1048576) + " MB 即标定原子 vtable=0x"
                            + vt.ToString("X") + "（候选频次 " + sorted[i].Value + "，共校验 " + tried + " 个）");
                        return vt;
                    }
                }
            }
            AnchorLog.Add("① 自举加速未命中（已扫 " + (scanned / 1048576) + " MB / 共 "
                + segs.Count + " 段，校验 " + attempted.Count + " 个候选）→ 回落完整扫描");
            return 0;
        }

        /// <summary>
        /// 【P0-⑦ 宽松自举】直接求 MonoDomain，不要求候选是 GameResAtom：
        ///   判据：某地址 v 的 vtable 链可读 —— `*(v)` 是类对象、`*(klass+0x48)` 是名字串，
        ///         读到非空 ASCII 名字即认定 v 是真 vtable（随机指针通过这条链的概率极低）。
        ///   强校验：**双源一致** —— 两个彼此独立的真 vtable 必须给出同一个 `*(v+0x10)`；
        ///         只有一致时才采信（本机实测 MonoDomain 对象自身没有可读 vtable，
        ///         所以「读 MonoDomain 类名」这条判据不可用，双源一致是可行且更强的替代）。
        ///   任一环节不通过返回 0 → 调用方回落完整 FindAllAtoms（保证正确性，宁慢不选错）。
        /// </summary>
        private long LooseBootstrapDomain()
        {
            const int MAX_TRY = 4096;                     // 候选尝试上限（每次约 3 次内存读）
            List<MemRegion> heap = HeapRegions();
            List<List<MemRegion>> segs = SplitIntoBlocks(heap, 16L * 1024 * 1024);
            if (segs.Count == 0) return 0;
            int[] pick = new int[] { 0, segs.Count - 1 };  // 两端各试一段（对象分布可能偏任一端）
            long lo = _heapLo, hi = _heapHi;
            byte[] buf = new byte[CHUNK];
            long dom = 0;
            int tried = 0;

            for (int pi = 0; pi < pick.Length; pi++)
            {
                if (pi == 1 && pick[0] == pick[1]) break;
                List<MemRegion> seg = segs[pick[pi]];
                for (int ri = 0; ri < seg.Count; ri++)
                {
                    MemRegion r = seg[ri];
                    long off = 0;
                    while (off < r.Size && tried < MAX_TRY)
                    {
                        int want = (int)Math.Min((long)CHUNK, r.Size - off);
                        if (want < 8) break;
                        int got = _mem.ReadInto(r.Base + off, buf, want);
                        if (got >= 8)
                        {
                            int limit = got - 8;
                            for (int i = 0; i <= limit; i += 8)
                            {
                                if (tried >= MAX_TRY) break;
                                long v = BitConverter.ToInt64(buf, i);
                                if (v < lo || v >= hi) continue;
                                tried++;
                                long klass = _mem.ReadLong(v);
                                if (klass <= 0x10000 || klass >= 0x7FFFFFFF0000L) continue;
                                long namep = _mem.ReadLong(klass + MONO_CLASS_NAME);
                                if (namep <= 0x10000 || namep >= 0x7FFFFFFF0000L) continue;
                                string nm = _mem.ReadAscii(namep, 96);
                                if (nm == null || nm.Length == 0 || nm.Length >= 96) continue;
                                long d = _mem.ReadLong(v + MONO_VTABLE_MONODOMAIN);
                                if (d <= 0x10000 || d >= 0x7FFFFFFF0000L) continue;
                                if (dom == 0) { dom = d; continue; }   // 第一个源
                                if (d == dom)
                                {
                                    AnchorLog.Add("① 宽松自举：试扫 " + ((pi == 0 ? pick[0] : pick[1]) + 1)
                                        + " 段 / 候选 " + tried + " 个即双源一致");
                                    return dom;                        // 双源一致 → 采信
                                }
                            }
                        }
                        off += want;
                    }
                    if (tried >= MAX_TRY) break;
                }
                if (dom != 0 && tried >= MAX_TRY) break;
            }
            if (dom != 0) AnchorLog.Add("① 宽松自举未取得双源一致 → 回落完整扫描");
            return 0;
        }

        public List<GameResAtom> FindAllAtoms()
        {
            List<MemRegion> targets = HeapRegions();

            int n = targets.Count;
            List<long>[] partAddr = new List<long>[n];
            List<long>[] partVt = new List<long>[n];

            // 【P0-①】线程本地复用缓冲（原实现每 4 MB 分片都 new byte[]：
            // 单次 FindAllAtoms 实测分配 4.84 GB、触发 Gen2 GC 28 次）。
            // 【审核轮 2026-09-26】加 using：LOH 缓冲扫描结束立即归还，不再等完整 GC。
            using (System.Threading.ThreadLocal<byte[]> tl =
                new System.Threading.ThreadLocal<byte[]>(delegate { return new byte[CHUNK]; }))
            {
                Parallel.For(0, n, delegate(int ri)
                {
                    MemRegion r = targets[ri];
                    List<long> a = new List<long>();
                    List<long> v = new List<long>();
                    byte[] buf = tl.Value;
                    long off = 0;
                    while (off < r.Size)
                    {
                        int want = (int)Math.Min((long)CHUNK, r.Size - off);
                        if (want < ATOM_SIZE) break;
                        int got = _mem.ReadInto(r.Base + off, buf, want);
                        if (got > ATOM_SIZE)
                        {
                            int limit = got - ATOM_SIZE;
                            for (int i = 0; i <= limit; i += 8)
                            {
                                long vt = BitConverter.ToInt64(buf, i + ATOM_VTABLE);
                                if (!MaybeHeap(vt)) continue;
                                long sync = BitConverter.ToInt64(buf, i + ATOM_SYNC);
                                if (sync != 0) continue;
                                long tp = BitConverter.ToInt64(buf, i + ATOM_TYPE);
                                if (!MaybeHeap(tp)) continue;
                                float val = BitConverter.ToSingle(buf, i + ATOM_VALUE);
                                if (float.IsNaN(val) || float.IsInfinity(val)) continue;
                                if (val > 1e12f || val < -1e12f) continue;

                                a.Add(r.Base + off + i);
                                v.Add(vt);
                            }
                        }
                        if (want < CHUNK) break;
                        off += want - (off + want < r.Size ? OVERLAP : 0);
                    }
                    partAddr[ri] = a;
                    partVt[ri] = v;
                });
            }

            // 合并 + 统计 vtable 频次
            List<long> candAddr = new List<long>();
            List<long> candVt = new List<long>();
            Dictionary<long, int> vtCount = new Dictionary<long, int>();
            for (int i = 0; i < n; i++)
            {
                List<long> a = partAddr[i];
                List<long> v = partVt[i];
                partAddr[i] = null; partVt[i] = null;
                if (a == null) continue;
                for (int k = 0; k < a.Count; k++)
                {
                    candAddr.Add(a[k]);
                    candVt.Add(v[k]);
                    int c;
                    vtCount.TryGetValue(v[k], out c);
                    vtCount[v[k]] = c + 1;
                }
                a.Clear(); v.Clear();
            }

            StatCandidates = candAddr.Count;
            StatDistinctVtables = vtCount.Count;

            // 按频次降序，逐个用 MonoClass 类名校验，锁定 GameResAtom
            List<KeyValuePair<long, int>> sorted = new List<KeyValuePair<long, int>>(vtCount);
            sorted.Sort(delegate(KeyValuePair<long, int> a, KeyValuePair<long, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            long finalVt = 0;
            int checkedCount = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].Value < 3 && checkedCount > 0) break; // 频次过低的尾巴不再看
                if (checkedCount >= 20000) break;
                checkedCount++;
                string cn = GetClassName(sorted[i].Key);
                if (checkedCount <= 25)
                {
                    // 【审核轮 2026-09-26】改走 AddNote：此处已属三级兜底的后台线程路径，
                    //   直接 Diagnostics.Add 会绕过 _diagGate 锁、也不守 32 条滚动上限。
                    AddNote(string.Format("  vt=0x{0:X}  count={1}  class={2}",
                        sorted[i].Key, sorted[i].Value, cn == null ? "<null>" : cn));
                }
                if (cn == "GameResAtom") { finalVt = sorted[i].Key; break; }
            }
            StatVtablesChecked = checkedCount;
            GameResAtomVTable = finalVt;
            // 【审核轮 2026-09-26】同上：改走 AddNote（并发纪律）。
            AddNote(string.Format("候选={0} 不同vtable={1} 已检查={2} 命中=0x{3:X}",
                StatCandidates, StatDistinctVtables, checkedCount, finalVt));

            // 精确重建
            List<GameResAtom> atoms = new List<GameResAtom>();
            if (finalVt != 0)
            {
                for (int i = 0; i < candAddr.Count; i++)
                {
                    if (candVt[i] != finalVt) continue;
                    GameResAtom a = new GameResAtom();
                    a.Address = candAddr[i];
                    a.VTable = finalVt;
                    a.TypeName = ReadMonoString(_mem.ReadLong(candAddr[i] + ATOM_TYPE));
                    a.Value = _mem.ReadFloat(candAddr[i] + ATOM_VALUE);
                    if (a.TypeName != null) atoms.Add(a);
                }
            }
            atoms.Sort(delegate(GameResAtom a, GameResAtom b) { return a.Address.CompareTo(b.Address); });
            return atoms;
        }

        // ------------------------------------------------------------------
        // 物品 id 全集（ItemDef 实例）
        //
        // 用途：把游戏运行时中文本地化词表（9 千余条，含 UI / 任务 / 对话文本）
        //       精简成「物品名表」（数百条）后落盘缓存，避免把非物品键写进缓存。
        // ItemDef 布局：【实测】+0x00 vtable / +0x08 sync(恒 0) / +0x10 id(MonoString)。
        // 与 FindAllAtoms 同一套「弱对象候选 → vtable 类名判定 → 精确重建」模式，只读。
        // ------------------------------------------------------------------

        public const int ITEMDEF_ID = 0x10;
        private const int ITEMDEF_SYNC = 0x08;
        /// <summary>
        /// ItemDef 的 redSkulls / whiteSkulls 的**运行时**偏移。
        /// 【来源·实测】2026-09-26 用 `Probe-ItemDefFields.exe` dump 814 个实例的 36 个字段槽，
        /// 再用已知静态属性表反向匹配 ⇒ 这两个位置 **814/814 = 100% 吻合**（次优候选仅 94.8%），
        /// 且两者是相邻的 4 字节 int（+0xF4 占 4 字节后紧接 +0xF8），与字段声明顺序互相印证。
        /// 详见 `02_分析记录\_星级与读档机制_20260926\evidence\标定_ItemDef骷髅字段偏移.md`。
        /// ⚠ 不要按 id 段推断属性：`guts_2_2:2` 的 id 写 `_2_2`，字段实为**红1白1**（814 条中唯一反例），
        /// 该物品在标定样本内且于新偏移上吻合 ⇒ 证明取值来自字段。
        ///
        /// ⚠ **写入约束（t21 评审 H3）**：这两个字段都是 **4 字节 int**，若将来（如「物品种类修改」）
        /// 需要写入，**只能写 4 字节**；按 8 字节写会覆盖 `+0xF8` 起的相邻字段（两者首尾相接）。
        /// 当前全仓对这两个偏移**只有读取**（`ReadInt`），无任何写入点。
        /// </summary>
        public const int ITEMDEF_RED_SKULLS = 0xF4;
        public const int ITEMDEF_WHITE_SKULLS = 0xF8;

        /// <summary>
        /// ItemDef 的 `quality` / `qualityType` 的**运行时**偏移（2026-09-26 同法标定，
        /// 复用同一份 dump、改用 `tools/match_quality_fields.py` 反向匹配）：
        /// **`quality` @ +0xD8、`qualityType` @ +0xDC**（同一 8 字节槽的高低位），
        /// 两者 **814/814 = 100% 吻合**，次优候选仅 81.3%。详见标定文档 §4.1。
        /// `qualityType`：0 = None（非星级物品，全库 645 条，二级菜单应置灰）、1 = Star（星级物品，169 条）。
        /// ⚠ 同为 4 字节 int，写入约束与 red/white 相同（只可写 4 字节）。
        /// </summary>
        public const int ITEMDEF_QUALITY = 0xD8;
        public const int ITEMDEF_QUALITY_TYPE = 0xDC;

        /// <summary>
        /// ItemDef 的 `type`（`ItemType` 枚举，4 字节 int）的**运行时**偏移：**@ +0xCC**
        /// （即 0xC8 槽的**高 32 位**）。
        /// 【2026-09-26 标定】同法反向匹配：**814/814 = 100% 吻合**，次优候选仅 74.2%
        /// （`tools\match_type_field.py`，枚举数值取自反编译源码 `public enum ItemType`）。
        /// 用途：「物品种类修改」的分组键 = **中文名 + ItemType**（用户 2026-09-26 确认）——
        /// ItemType 是区分「外科医生的失误」6 条（Bones/Brain/Guts/Heart/Skin/Skull，
        /// 其余字段完全相同、红白均为 -1）的**唯一**手段。
        /// ⚠ 同为 4 字节，只可写 4 字节。
        /// </summary>
        public const int ITEMDEF_TYPE = 0xCC;
        private const int ITEMDEF_MIN_OBJ = 0x20;

        /// <summary>
        /// 枚举游戏内存中所有 ItemDef 实例的 id（物品 id 全集，实测约 814 个）。
        /// 失败（拿不到 ItemDef 类 / 一个都没有）时返回空列表，绝不抛异常。
        /// </summary>
        public List<string> FindAllItemDefIds()
        {
            List<string> ids = new List<string>();
            ItemDefAttrs.Clear();          // 【2026-09-26】与 ids 同生命周期，避免跨次累积
            ItemDefQuality.Clear();        // 同上
            ItemDefType.Clear();           // 同上（ItemType：分组键的一半）
            ItemDefInstAddr.Clear();       // 同上（地址字典：仅本进程内有效）
            ItemDefIdPtr.Clear();          // 同上
            try
            {
                List<MemRegion> targets = HeapRegions();
                int n = targets.Count;
                if (n == 0) return ids;

                List<long>[] partAddr = new List<long>[n];
                List<long>[] partVt = new List<long>[n];
                // 【P0-①】线程本地复用缓冲（原实现单次扫描分配 4.9 GB、Gen2 GC 27 次）。
                // 【审核轮 2026-09-26】加 using：LOH 缓冲扫描结束立即归还。
                using (System.Threading.ThreadLocal<byte[]> tl =
                    new System.Threading.ThreadLocal<byte[]>(delegate { return new byte[CHUNK]; }))
                {
                    System.Threading.Tasks.Parallel.For(0, n, delegate(int ri)
                    {
                        MemRegion r = targets[ri];
                        List<long> a = new List<long>();
                        List<long> v = new List<long>();
                        byte[] buf = tl.Value;
                        long off = 0;
                        while (off < r.Size)
                        {
                            int want = (int)Math.Min((long)CHUNK, r.Size - off);
                            if (want < ITEMDEF_MIN_OBJ) break;
                            int got = _mem.ReadInto(r.Base + off, buf, want);
                            if (got > ITEMDEF_MIN_OBJ)
                            {
                                int limit = got - ITEMDEF_MIN_OBJ;
                                for (int i = 0; i <= limit; i += 8)
                                {
                                    long vt = BitConverter.ToInt64(buf, i);
                                    if (!MaybeHeap(vt)) continue;
                                    long sync = BitConverter.ToInt64(buf, i + ITEMDEF_SYNC);
                                    if (sync != 0) continue;
                                    long idp = BitConverter.ToInt64(buf, i + ITEMDEF_ID);
                                    if (!MaybeHeap(idp)) continue;
                                    a.Add(r.Base + off + i);
                                    v.Add(vt);
                                }
                            }
                            if (want < CHUNK) break;
                            off += want - (off + want < r.Size ? OVERLAP : 0);
                        }
                        partAddr[ri] = a;
                        partVt[ri] = v;
                    });
                }

                // 合并 + 统计 vtable 频次
                List<long> candAddr = new List<long>();
                List<long> candVt = new List<long>();
                Dictionary<long, int> vtCount = new Dictionary<long, int>();
                for (int i = 0; i < n; i++)
                {
                    List<long> a = partAddr[i];
                    List<long> v = partVt[i];
                    partAddr[i] = null; partVt[i] = null;
                    if (a == null) continue;
                    for (int k = 0; k < a.Count; k++)
                    {
                        candAddr.Add(a[k]);
                        candVt.Add(v[k]);
                        int c;
                        vtCount.TryGetValue(v[k], out c);
                        vtCount[v[k]] = c + 1;
                    }
                    a.Clear(); v.Clear();
                }
                if (candAddr.Count == 0) return ids;

                // 按频次降序，逐个用 MonoClass 类名校验，锁定 ItemDef
                List<KeyValuePair<long, int>> sorted = new List<KeyValuePair<long, int>>(vtCount);
                sorted.Sort(delegate(KeyValuePair<long, int> a, KeyValuePair<long, int> b)
                {
                    return b.Value.CompareTo(a.Value);
                });

                long finalVt = 0;
                int checkedCount = 0;
                for (int i = 0; i < sorted.Count; i++)
                {
                    if (sorted[i].Value < 3 && checkedCount > 0) break;
                    if (checkedCount >= 20000) break;
                    checkedCount++;
                    if (GetClassName(sorted[i].Key) == "ItemDef") { finalVt = sorted[i].Key; break; }
                }
                ItemDefVTable = finalVt;
                if (finalVt == 0) return ids;

                HashSet<string> seen = new HashSet<string>();
                // 【2026-09-26】同一遍扫描里顺带读红白骷髅（零额外扫描成本）——
                // 供物品下拉显示属性标注，使同名变体可区分（如两条「骨骼（铜星）」）。
                for (int i = 0; i < candAddr.Count; i++)
                {
                    if (candVt[i] != finalVt) continue;
                    long idPtr = _mem.ReadLong(candAddr[i] + ITEMDEF_ID);
                    string id = ReadMonoString(idPtr);
                    if (id == null || id.Length == 0 || id.Length > 64) continue;
                    if (!IsAsciiId(id)) continue;
                    if (seen.Add(id))
                    {
                        ids.Add(id);
                        // 只读 4 字节字段本身；偏移见 ITEMDEF_RED_SKULLS / ITEMDEF_WHITE_SKULLS 的注释
                        int red = _mem.ReadInt(candAddr[i] + ITEMDEF_RED_SKULLS);
                        int white = _mem.ReadInt(candAddr[i] + ITEMDEF_WHITE_SKULLS);
                        ItemDefAttrs[id] = new int[] { red, white };
                        // 【2026-09-26 · 方案C 前置】同遍读出星级并登记实例/字符串指针（零额外扫描）
                        int qual = _mem.ReadInt(candAddr[i] + ITEMDEF_QUALITY);
                        int qtype = _mem.ReadInt(candAddr[i] + ITEMDEF_QUALITY_TYPE);
                        ItemDefQuality[id] = new int[] { qual, qtype };
                        // 【2026-09-26 · 方案C】ItemType（分组键的一半）：+0xCC，实测 814/814
                        ItemDefType[id] = _mem.ReadInt(candAddr[i] + ITEMDEF_TYPE);
                        ItemDefInstAddr[id] = candAddr[i];
                        ItemDefIdPtr[id] = idPtr;
                    }
                }
            }
            // 【t3 C-1】异常不再静默吞掉：否则预热会「看起来成功但返回空表」，
            // 之后每次刷新都要重付 4 秒的实时提取，且无任何线索。
            catch (Exception ex) { AddDiagnostic("FindAllItemDefIds", ex); }
            return ids;
        }
        // ------------------------------------------------------------------
        // 【2026-09-23 删除】HUD 富文本交叉校验已整体移除，原成员：
        //   ⚠ 此处刻意不再写出被删成员名，以免死代码检索（DEADCODE_HITS）把说明文字误判为残留。
        // 删除理由（实测，证据见 02_分析记录\_判据重构_20260923\ 与 _容器判别泛化_20260923\）：
        //   · 无效：用户与 verifier 双重独立实测均显示「仍选错」（选到旧档/附近箱子）；
        //   · 昂贵：每次调用需扫全堆富文本，实测 3.1 s，占单次刷新耗时的很大比重；
        //   · 已被取代：选组改为「当前存档对象闭包恰好命中 1 组」
        //     （MainGame::<PlayerData> 现场解析 → 问题 A「主背包 ≡ PlayerData+0x58」/
        //       问题 B「当前存档 ≡ MainGame::<PlayerData>」两层判据）。
        //   历史实现可从项目内 __backup_* 备份目录取回。
        // ------------------------------------------------------------------

        /// <summary>ItemDef 类的真 vtable（由 FindAllItemDefIds 现场标定；随进程变化，不写死）。</summary>
        public long ItemDefVTable = 0;

        /// <summary>
        /// 物品 id → [redSkulls, whiteSkulls]（由 <see cref="FindAllItemDefIds"/> 在同一遍扫描中填充）。
        /// 【2026-09-26 新增】用途：物品下拉显示红白骷髅标注，使同名变体可区分
        /// （如 `bones_0_0:1` 红0白0 与 `bones_0_1:1` 红0白1 都叫「骨骼（铜星）」）。
        /// 每次 <see cref="FindAllItemDefIds"/> 开始时会先清空。
        /// </summary>
        public readonly Dictionary<string, int[]> ItemDefAttrs = new Dictionary<string, int[]>();

        /// <summary>
        /// 物品 id → [quality, qualityType]（由 <see cref="FindAllItemDefIds"/> 同一遍扫描填充）。
        /// 【2026-09-26 新增 · 方案C 前置】偏移见 <see cref="ITEMDEF_QUALITY"/> 注释（实测 814/814）。
        /// `qualityType == 0` ⇒ 该物品**不分星级**（全库 645 条）；`== 1` ⇒ 有星级（169 条，归并 53 个物品名）。
        /// 用途：物品星级二级菜单的置灰判定（不分星级 ⇒ 置灰不可展开）。
        /// 纯数据、与地址无关 ⇒ 可随名表/属性缓存一同落盘（见 CacheStore）。
        /// </summary>
        public readonly Dictionary<string, int[]> ItemDefQuality = new Dictionary<string, int[]>();

        /// <summary>
        /// 物品 id → `ItemType` 枚举值（`int`；由 <see cref="FindAllItemDefIds"/> 同一遍扫描填充）。
        /// 【2026-09-26 新增 · 方案C 前置】偏移见 <see cref="ITEMDEF_TYPE"/>（实测 814/814）。
        /// 用途：「物品种类修改」分组键 = **中文名 + ItemType**。
        /// ⚠ 本字典存的是**枚举数值**；UI 若要显示名称需自行做「数值 → 中文名」映射
        /// （枚举定义见反编译源码 `public enum ItemType`；None=0 … Bag=400、Demon=666）。
        /// </summary>
        public readonly Dictionary<string, int> ItemDefType = new Dictionary<string, int>();

        /// <summary>
        /// 物品 id → ItemDef **实例地址**（堆扫描现场值）。
        /// ⚠ **地址只存活于本进程内存，绝不落盘**（红线③：地址跨进程必失效）。
        /// 仅供方案C「写入」阶段在同一进程内使用（读目标物品的字段值 / 校验写入前后一致）。
        /// </summary>
        public readonly Dictionary<string, long> ItemDefInstAddr = new Dictionary<string, long>();

        /// <summary>
        /// 物品 id → 该 ItemDef 上 `id` 字段指向的 **MonoString 指针**（UTF-16LE 内容）。
        /// ⚠ 同 <see cref="ItemDefInstAddr"/>：**地址不落盘**。
        /// 用途：方案C 若需把 Item 的 id 指向另一个字符串，可复用游戏内存里**已存在**的字符串，
        /// 从而完全避免自行分配 MonoString（不触发 GC 分配、不写入游戏堆的未知区域）。
        /// </summary>
        public readonly Dictionary<string, long> ItemDefIdPtr = new Dictionary<string, long>();
        // ------------------------------------------------------------------


        // ==================================================================
        // 【仅供 tools\ 诊断工具使用 —— 修改器运行时不调用】（t3 §5.2 / t5 清理轮标注）
        //   使用方：tools\LikeExperiment.cs / LikeLocate.cs / CacheProbe.cs / JudgeProbe.cs /
        //           ItemCountSelfCheck.cs / Verify-LocMemRead.cs / _diag_locread.ps1
        //   本节成员：FindAllPlayerArrays / FindPlayerResourceArray / FindByName /
        //           PickGroupByCurrentSave / BuildCurrentSaveClosure / ExpandRefs
        //           （以及下文 ItemCount 段的 FindItemCounts / ReadLegacyItemCount）
        //
        //   ⚠ 为什么"标注"而不是拆成 partial 文件：tools\ 下的多个编译脚本
        //     （_likeexp_build.ps1 / _diag_locread.ps1 / Verify-LocMemRead.ps1 / _t11_*.ps1）
        //     以**显式文件名**列出 `src\GameResLocator.cs` 参与编译，拆文件会让它们集体编译失败。
        //     因此这些成员保留在本文件内，仅在此处集中标注用途与"运行时不用"。
        //
        //   历史背景：「全堆扫描 + 地址连续聚组 + 当前存档闭包选组」判据已在 2026-09-24 被
        //     读档场景证伪（读档后 atom 不再地址连续 → 0 命中 → 永不就绪），修改器改走
        //     「结构链直达 resValues」（见 BuildResGroupViaAnchor）。这些成员只留作诊断工具。
        // ==================================================================

        // ------------------------------------------------------------------
        // 玩家资源数组识别
        // ------------------------------------------------------------------

        private static readonly string[] PlayerArraySignature = new string[]
        {
            "global_ppl", "donkey_body_drop_chance", "tech_blue", "tech_green",
            "tech_red", "insanity", "energy"
        };

        /// <summary>
        /// 找出全部「玩家资源数组」候选。
        /// 切换存档后旧存档的对象可能仍未被 GC 回收，因此会出现多组候选，
        /// 需要由上层按数值特征选择当前存档所属的那一组。
        /// </summary>
        public List<List<GameResAtom>> FindAllPlayerArrays(List<GameResAtom> all)
        {
            List<List<GameResAtom>> result = new List<List<GameResAtom>>();
            int i = 0;
            while (i < all.Count)
            {
                int j = i;
                while (j + 1 < all.Count && all[j + 1].Address - all[j].Address == ATOM_SIZE) j++;
                if (j > i)
                {
                    List<GameResAtom> seg = all.GetRange(i, j - i + 1);
                    int score = 0;
                    bool hasMoney = false;
                    for (int k = 0; k < seg.Count; k++)
                    {
                        if (seg[k].TypeName == "money") hasMoney = true;
                        for (int s = 0; s < PlayerArraySignature.Length; s++)
                        {
                            if (seg[k].TypeName == PlayerArraySignature[s]) { score++; break; }
                        }
                    }
                    if (hasMoney && score >= 4) result.Add(seg);
                }
                i = j + 1;
            }
            return result;
        }

        public List<GameResAtom> FindPlayerResourceArray(List<GameResAtom> all)
        {
            List<List<GameResAtom>> groups = FindAllPlayerArrays(all);
            List<GameResAtom> best = null;
            int bestScore = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                int score = 0;
                for (int k = 0; k < groups[g].Count; k++)
                {
                    for (int s = 0; s < PlayerArraySignature.Length; s++)
                    {
                        if (groups[g][k].TypeName == PlayerArraySignature[s]) { score++; break; }
                    }
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = groups[g];
                }
            }
            if (best != null)
            {
                PlayerResArrayBase = best[0].Address;
                PlayerResCount = best.Count;
            }
            return best;
        }

        public GameResAtom FindByName(List<GameResAtom> atoms, string name)
        {
            for (int i = 0; i < atoms.Count; i++)
                if (atoms[i].TypeName == name) return atoms[i];
            return null;
        }

        // ------------------------------------------------------------------
        // 【已证伪，仅供存档研究，勿用于修改】
        // 物品全局计数（ItemCount : itemId | itemDef | count）
        //
        // 2026-09-22 结论：全程序集 IL 中 0 处调用 ItemCount 的任何方法；
        // 175 个实例的持有者全部是 QuestPhraseRequirement（任务条件描述符）。
        // 不存在「玩家全局物品计数容器」，改 ItemCount 不影响游戏中的任何数字。
        // 真正有效的物品数量是 Item + 0x40（见本文件末尾「Item 层」段）。
        // 以下代码保留仅为追溯历史结论，UI 已不再调用。
        //
        // 实测布局（对象大小 0x28，MonoClass+0x1C=40）：
        //   +0x10 itemId  (MonoString*)
        //   +0x18 itemDef (ItemDef*)   元数据签名 TypeDef RID 496 = ItemDef
        //   +0x20 count   (int32)      签名字节 06 08 = ELEMENT_TYPE_I4
        // 注意：count 是 4 字节。早期记录写 int64、itemDef 写 +0x30 均为错误；
        // 按 8 字节写会溢到 +0x24（对象尾部填充/相邻对象），从而破坏对象。
        // ------------------------------------------------------------------

        public const int ITEMCOUNT_ID = 0x10;
        public const int ITEMCOUNT_ITEMDEF = 0x18;
        public const int ITEMCOUNT_COUNT = 0x20;

        public class ItemCountRef
        {
            public long Address;
            public string ItemId;
            public int Count;
        }

        /// <summary>
        /// 【已证伪，仅供存档研究，勿用于修改】按物品 ID 查找所有 ItemCount 实例。
        /// 不需要预先知道 ItemCount 的 vtable：先定位该 ID 的 Mono String，
        /// 再找出引用它、且类名确为 "ItemCount" 的对象。
        /// </summary>
        public List<ItemCountRef> FindItemCounts(string itemId)
        {
            List<ItemCountRef> found = new List<ItemCountRef>();
            if (string.IsNullOrEmpty(itemId) || itemId.Length > 64) return found;

            byte[] chars = Encoding.Unicode.GetBytes(itemId);
            byte[] needle = new byte[4 + chars.Length];
            int n = itemId.Length;
            needle[0] = (byte)(n & 0xFF);
            needle[1] = (byte)((n >> 8) & 0xFF);
            needle[2] = (byte)((n >> 16) & 0xFF);
            needle[3] = (byte)((n >> 24) & 0xFF);
            Array.Copy(chars, 0, needle, 4, chars.Length);

            List<long> strHits = _mem.ScanParallel(needle, 0, 0, 300);
            if (strHits.Count == 0) return found;

            List<long> strObjs = new List<long>();
            // needle 以 length 字段(4 字节)开头，命中位置即 String 对象的 +0x10
            for (int i = 0; i < strHits.Count; i++) strObjs.Add(strHits[i] - MONO_STRING_LENGTH);

            List<long> refs = _mem.ScanPointersTo(strObjs, 4000);
            int[] backs = new int[] { 0x10, 0x18, 0x20, 0x28, 0x30, 0x08 };
            for (int i = 0; i < refs.Count; i++)
            {
                for (int b = 0; b < backs.Length; b++)
                {
                    long obj = refs[i] - backs[b];
                    string cn = GetClassName(_mem.ReadLong(obj));
                    if (cn == "ItemCount")
                    {
                        bool dup = false;
                        for (int k = 0; k < found.Count; k++)
                        {
                            if (found[k].Address == obj) { dup = true; break; }
                        }
                        if (!dup)
                        {
                            ItemCountRef r = new ItemCountRef();
                            r.Address = obj;
                            r.ItemId = itemId;
                            r.Count = ReadLegacyItemCount(obj);
                            found.Add(r);
                        }
                        break;
                    }
                }
            }
            return found;
        }

        /// <summary>【已证伪】读 ItemCount +0x20。仅供存档研究，勿用于修改。</summary>
        public int ReadLegacyItemCount(long obj)
        {
            byte[] b = _mem.ReadBytes(obj + ITEMCOUNT_COUNT, 4);
            if (b == null || b.Length != 4) return 0;
            return BitConverter.ToInt32(b, 0);
        }

        // 【已删除 · t5 死代码清理】本节原有 1 个「向 ItemCount 写数量」的成员：
        //   ItemCount 链路已整体证伪（HANDOFF §2.5），src 与 tools 双侧零引用。
        //   删除同时收窄了「可写其它结构」的误用面 —— 修改物品数量请用
        //   WriteItemCount（Item + 0x40，类名 + itemId + 写后复读三重校验）。

        // ------------------------------------------------------------------
        // Item 层（已实测有效）—— 玩家主库存与物品数量
        //
        // 为什么是它：玩家真实物品就是 Item 实例，数量 = Item + 0x40 的 int32。
        // 实测：berry_pie 的 count 由 30 改成 777 后，按 Tab 关开背包，
        // 游戏物品栏该格真的显示 777；回滚后恢复 30。
        //
        // Item 对象布局（instance_size = 0x50，实测确认；vtable 随进程变化，禁止硬编码）：
        //   +0x00 vtable                  +0x10 itemId (MonoString*)
        //   +0x18 ItemDef*（容器为 0）     +0x20 id 副本（与 +0x10 同一指针）
        //   +0x28 SGuid                   +0x30 List<Item>（容器才有）
        //   +0x38 Dictionary（容器才有）
        //   +0x40 count    (int32) ← 只写 4 字节！容器上 +0x44 是容量，
        //                            按 8 字节写会把容量字段一起写坏。
        //   +0x44 inventorySize (int32)      +0x48 inventoryFillSize (int32, 源码默认 -1)
        // 【2026-09-26 口径修正 · 实测来源 tools\Probe-ItemFields.cs，样本 bag25/heap256】
        //   旧注释把 +0x44/+0x48 写成「容器容量 / 容器已用格数（== List._size）」，**不准确**：
        //   静态字段是 Item.inventorySize / Item.inventoryFillSize（后者声明为 `= -1`）。
        //   实测：普通背包物品 +0x44 = 0、+0x48 = -1（**正是源码默认值**，不是"已用格数"）；
        //   只有**玩家主库存**这一个对象上 +0x48 恰好等于 List._size（11 == 11）。
        //   ⚠ 因此 **`+0x48 == List._size` 不是通用判据**，它在 `fillSize` 未被维护的容器上不成立；
        //     任何新增的容器识别逻辑都不得只依赖它（既有判据保留原样，未做未经验证的改动）。
        //
        // List<T>：+0x10 T[] _items, +0x18 int32 _size
        // 数组    ：+0x10 bounds（szarray 为 NULL）, +0x18 max_length, +0x20 元素0
        //           ⚠ 元素起点是 +0x20 而不是 +0x18（前辈已踩过这个坑）
        // ------------------------------------------------------------------

        public const int ITEM_ITEMID = 0x10;
        public const int ITEM_ITEMDEF = 0x18;
        public const int ITEM_LIST = 0x30;
        public const int ITEM_DICT = 0x38;
        public const int ITEM_COUNT = 0x40;
        public const int ITEM_CAPACITY = 0x44;   // 实为 Item.inventorySize（见上方口径修正）
        /// <summary>
        /// 实为 `Item.inventoryFillSize`（源码声明 `= -1`），**不是**「容器已用格数」。
        /// 实测：普通背包物品恒为 −1；仅在玩家主库存上恰好等于 `List._size`。
        /// ⚠ 名称沿用旧值以免大面积改名；**新增逻辑不要依赖 `ITEM_USED == LIST_SIZE`**。
        /// </summary>
        public const int ITEM_USED = 0x48;
        public const int ITEM_SIZE = 0x50;

        public const int LIST_ARR = 0x10;
        public const int LIST_SIZE = 0x18;
        public const int ARRAY_LEN = 0x18;
        public const int ARRAY_DATA = 0x20;
        /// <summary>GameRes.resValues（List&lt;GameResAtom&gt;）偏移；反编译源码佐证（GameRes 类首引用字段）。</summary>
        public const int GAMERES_RESVALUES = 0x10;

        /// <summary>游戏内部给玩家主库存打的标签（语义锚，不是硬编码地址）。</summary>
        // （2026-09-23 删除）原「语义锚 tag」常量已移除：实测该 tag 不是身份标识
        //   （GetWidgetsDataForMultiInventory 会把它透传给每个附近容器，
        //     UIBaseChestWindowData 构造时运行期还会改写成 -simple_chest 变体），
        //   现改由 MainGame::<PlayerData> 静态引用定位当前存档（见 RefreshCurrentSaveAnchor）。
        public const string INVENTORY_ITEM_ID = "inventory";

        /// <summary>库存中的一件物品（一个格子）。</summary>
        public class ItemRef
        {
            public long Address;
            public string ItemId;
            public int Count;
        }

        /// <summary>玩家主库存容器 Item 的地址（0 = 尚未定位/定位失败）。</summary>
        public long PlayerInventory = 0;
        /// <summary>主库存的定位来源，仅用于日志与排查。</summary>
        public string InventorySource = "";
        /// <summary>语义锚路径下主库存获得的票数（历史字段，仅诊断）。</summary>
        public int InventoryVotes = 0;
        /// <summary>语义锚路径下的候选容器个数（诊断用）。</summary>
        public int InventoryCandidates = 0;

        // ==================================================================
        // 当前存档锚（冷刷新现场解析，零硬编码地址）
        //
        // 两层问题分别由游戏自身数据结构回答：
        //   问题 A（主背包 vs 箱子）：主背包 ≡ 某个 PlayerData 的 +0x58；
        //     箱子/传送带/NPC 容器天然不在这个集合内。
        //   问题 B（当前存档 vs 旧档残留）：当前存档 ≡
        //     MainGame::<PlayerData>k__BackingField（由游戏在读档时自行更新）。
        //
        // 现场解析链（全部由值校验串联，无任何常量地址）：
        //   ① 自举 MonoDomain：任一 GameResAtom 的真 vtable 的 +0x10
        //      （同进程内所有真 vtable 的 +0x10 同值）
        //   ② ScanPointersTo(domain) → 命中位置 p → 候选真 vtable = p - 0x10
        //   ③ 候选校验 V1/V3 + 类名： [VT]==klass、[VT+0x18]→RuntimeType、
        //      klass.name == "MainGame"
        //   ④ [VT+0x68] = static_data 块；块内 +0x28 = <PlayerData>k__BackingField
        //   ⑤ 校验链：PD 类名 == PlayerData → +0x58 Inventory → +0x38 Item
        //      （类名 Item、itemId=="inventory"、List 自洽）
        // 任一级不过 → 拒绝定位（宁可不定位，不可选错）。
        // 地址只存活在进程内存，绝不落盘（跨游戏启动必失效）。
        // ==================================================================

        /// <summary>当前存档 PlayerData 对象地址（0 = 未定位）。</summary>
        public long CurrentPlayerData = 0;
        /// <summary>当前存档的资源对象（PlayerData+0x80）。</summary>
        public long CurrentGameRes = 0;
        /// <summary>当前存档的 Inventory 对象（PlayerData+0x58）。</summary>
        public long CurrentInventory = 0;
        /// <summary>当前存档的工具腰带 Inventory（PlayerData+0x60）。</summary>
        public long CurrentToolBelt = 0;
        /// <summary>MainGame 单例实例（static_data 中类名 MainGame 的槽;结构链/主菜单检测的出发点）。</summary>
        public long MainGameInstance = 0;
        /// <summary>现场解析出的 MainGame 真 vtable（仅诊断显示）。</summary>
        public long AnchorMainGameVTable = 0;
        /// <summary>现场解析出的 MainGame static_data 块（仅诊断显示）。</summary>
        public long AnchorStaticData = 0;
        /// <summary>现场自举出的 MonoDomain（仅诊断显示）。</summary>
        public long AnchorMonoDomain = 0;
        /// <summary>最近一次冷刷新的逐级日志（供界面显示失败原因）。</summary>
        public readonly List<string> AnchorLog = new List<string>();

        /// <summary>
        /// 定位「当前存档的玩家主库存」容器 Item。
        ///
        /// 判据（两层，均由游戏自身数据结构定义，零硬编码地址）：
        ///   问题 A（主背包 vs 箱子/容器）→ 主背包 ≡ PlayerData+0x58（箱子天然不在该集合内）；
        ///   问题 B（当前存档 vs 旧档残留）→ MainGame::&lt;PlayerData&gt;k__BackingField
        ///   （游戏自己维护，读档时由游戏更新，免疫 6 个 PlayerData 残留实例）。
        ///
        /// 热路径：已缓存地址且廉价校验通过 → 直接复用（几次读取）。
        /// 冷路径：完整现场解析（见 RefreshCurrentSaveAnchor）。
        /// 任一环节不通过即【拒绝定位】—— 宁可不定位，不可选错。
        /// </summary>
        public long FindPlayerInventory()
        {
            // 热路径：锚**既仍指向当前存档、又结构可读**时直接复用（微秒级）。
            // 【P0a 语义边界】这里的 IsAnchorStillValid() 是**复用前的廉价校验**（身份 ∧ 结构），
            //   失败只表示「不能复用缓存」⇒ 落到下面的冷路径**重新解析一次**（不清空、不降态）；
            //   它**不承担换档语义** —— 换档 / 回主菜单判定在 TrainerForm.MonitorLifecycle，
            //   那里只用 IsSameSaveSlot()（结构抖动不得触发重载）。
            if (PlayerInventory != 0 && IsAnchorStillValid())
                return PlayerInventory;

            // 冷路径：完整现场解析（启动 / 换档 / 地址校验失败都走这里）
            if (RefreshCurrentSaveAnchor())
                return PlayerInventory;

            PlayerInventory = 0;
            return 0;
        }

        /// <summary>
        /// 【冷刷新】零硬编码现场解析「当前存档锚」，缓存到**进程内存**（绝不落盘）。
        /// 链：原子vtable+0x10 → MonoDomain → ScanPointersTo(domain) 候选 vtable
        ///   → V1(klass)/V3(RuntimeType)/类名==MainGame → [VT+0x68] static_data
        ///   → [+0x28] PlayerData → +0x58 Inventory → +0x38 Item（全链值校验）。
        /// 返回 true 表示整条校验链通过。
        /// </summary>
        public bool RefreshCurrentSaveAnchor()
        {
            AnchorLog.Clear();
            CurrentPlayerData = 0;
            CurrentGameRes = 0;
            CurrentInventory = 0;
            CurrentToolBelt = 0;
            AnchorMainGameVTable = 0;
            AnchorStaticData = 0;
            MainGameInstance = 0;
            PlayerInventory = 0;
            InventorySource = "";

            // ① 自举 MonoDomain（同进程内所有真 vtable 的 +0x10 同值；用任一原子的真 vtable 取）
            long seedVt = GameResAtomVTable;
            // 【P0-⑤】先做「使用前校验」：同一进程内复用上次标定的 vtable 时，
            // 必须先确认它仍指向类名 GameResAtom 的对象；否则清零回落完整自举。
            if (seedVt > 0x10000)
            {
                long sv = _mem.ReadLong(seedVt);
                if (sv <= 0x10000 || GetClassNameUncached(seedVt) != "GameResAtom")
                {
                    AddNote("复用 vtable 校验未通过 → 回落完整自举");
                    seedVt = 0;
                    GameResAtomVTable = 0;
                }
            }
            long domainOverride = 0;
            if (seedVt <= 0x10000)
            {
                // 【P0-⑤ 自举加速】只为拿「任一 GameResAtom 的真 vtable」，不必扫完整堆：
                //  旧实现为了这一个值付一次全堆扫描（实测 3.3~4.0 s，占冷刷新 34~41%）。
                //  新实现按地址升序分段扫描，段内只做纯内存的候选计数（零类名读），
                //  收满一段后对出现次数最多的少数 vtable 做类名校验，命中即返回；
                //  全部段落失败即回落完整 FindAllAtoms（保证正确性，宁慢不选错）。
                seedVt = FastBootstrapVTable();
                if (seedVt > 0x10000) GameResAtomVTable = seedVt;
            }
            if (seedVt <= 0x10000)
            {
                // 【P0-⑦ 宽松自举】严格判据（类名 == GameResAtom）未命中时，改用宽松判据：
                //  只要求「某地址的 vtable→klass→名字链可读」，并用**双源一致**做强校验
                //  （两个彼此独立的真 vtable 必须给出同一个 [+0x10] ⇒ 该值即 MonoDomain）。
                //  本机实测：GameResAtom 集中在堆中部，严格判据要扫 510 MB；
                //  宽松判据在第一个试扫段的头几个候选即可双源一致 ⇒ 数十毫秒。
                domainOverride = LooseBootstrapDomain();
            }
            if (seedVt <= 0x10000 && domainOverride <= 0x10000)
            {
                List<GameResAtom> probe = FindAllAtoms();
                if (probe.Count > 0) seedVt = probe[0].VTable;
            }
            if (seedVt <= 0x10000 && domainOverride <= 0x10000)
            {
                AnchorLog.Add("自举失败：内存中未发现 GameResAtom 对象（游戏可能尚未开始游戏）");
                InventorySource = "未定位：自举失败（未发现资源原子）";
                return false;
            }
            AnchorMonoDomain = (seedVt > 0x10000)
                ? _mem.ReadLong(seedVt + MONO_VTABLE_MONODOMAIN)
                : domainOverride;
            if (seedVt <= 0x10000 && domainOverride > 0x10000)
                AnchorLog.Add("① 宽松自举：MonoDomain=0x" + AnchorMonoDomain.ToString("X") + "（双源一致校验通过）");
            if (AnchorMonoDomain <= 0x10000 || AnchorMonoDomain >= 0x7FFFFFFF0000L)
            {
                AnchorLog.Add("自举失败：[原子vtable+0x10] 不是有效指针");
                InventorySource = "未定位：MonoDomain 自举失败";
                return false;
            }
            AnchorLog.Add("① 自举 MonoDomain=0x" + AnchorMonoDomain.ToString("X") + "（取自原子真 vtable +0x10）");

            // ② 扫 domain 值 → 命中位置 p 即 vtable+0x10 → 候选真 vtable = p-0x10
            // 【P0-② 分段早停】把可扫描区域按**地址升序**切成不超过 EARLY_STOP_BLOCK_BYTES 的块，
            // 逐块扫描 + 流式校验，块内出现「全链合格」候选即停。
            // 语义与旧实现**严格等价**：旧实现扫完全部区域后取「命中序列（区域序 + 区内偏移升序
            // = 地址升序）中第一个合格者」；本实现从最低地址开始分块、块内取地址最小的合格者
            // → 结果必为同一候选（不是近似，是等价）。
            // 实测：全堆 3.93 GB 全扫 2240 ms → 命中在 136 MB 处即停 78 ms（-96.5%），
            //       返回的 vtable 与原实现**逐位一致**（具体数值见 §1.9 / 性能审查报告实测记录，
            //       此处刻意不写出地址字面量：避免静态「禁止硬编码地址」检查误报）。
            long vt = 0, klass = 0, sd = 0, pd = 0;
            int named = 0;
            long scannedBytes = 0;
            int totalHits = 0;
            List<MemRegion> scanRegions = _mem.FilterScanRegions(_mem.GetRegions());
            List<List<MemRegion>> blocks = SplitIntoBlocks(scanRegions, EARLY_STOP_BLOCK_BYTES);
            for (int b = 0; b < blocks.Count && vt == 0; b++)
            {
                List<MemRegion> block = blocks[b];
                for (int i = 0; i < block.Count; i++) scannedBytes += block[i].Size;
                List<long> hits = _mem.ScanPointersTo(new List<long>(new long[] { AnchorMonoDomain }), block, 500000);
                totalHits += hits.Count;
                // hits 已按地址升序（区域序 + 区内偏移升序）
                for (int i = 0; i < hits.Count; i++)
                {
                    long cand = hits[i] - MONO_VTABLE_MONODOMAIN;
                    if (cand <= 0x10000 || cand >= 0x7FFFFFFF0000L) continue;
                    if (GetClassNameUncached(cand) != MAIN_GAME_CLASS) continue;      // V1 + 类名
                    named++;
                    long candKlass = _mem.ReadLong(cand);
                    if (candKlass <= 0x10000) continue;
                    // V3：[VT+0x18] 是 RuntimeType「对象」，必须再取其 vtable 才能读类名（少读一层会得到垃圾）
                    long rtObj = _mem.ReadLong(cand + MONO_VTABLE_RUNTIMETYPE);
                    if (rtObj <= 0x10000 || rtObj >= 0x7FFFFFFF0000L) continue;
                    if (GetClassNameUncached(_mem.ReadLong(rtObj)) != RUNTIMETYPE_CLASS) continue;
                    long candSd = _mem.ReadLong(cand + MONO_VTABLE_STATIC_DATA);      // V4
                    if (candSd <= 0x10000 || candSd >= 0x7FFFFFFF0000L) continue;
                    long candSlot = _mem.ReadLong(candSd + STATIC_SLOT_PLAYERDATA);
                    if (candSlot <= 0x10000 || candSlot >= 0x7FFFFFFF0000L) continue;
                    if (GetClassNameUncached(_mem.ReadLong(candSlot)) != PLAYERDATA_CLASS) continue;
                    vt = cand; klass = candKlass; sd = candSd; pd = candSlot;
                    break;
                }
            }
            AnchorLog.Add("② domain 引用命中 " + totalHits + " 处（分段早停：实扫 "
                + (scannedBytes / 1048576) + " MB / 全 " + (TotalBytes(scanRegions) / 1048576) + " MB）");
            AnchorLog.Add("   类名为 MainGame 的候选 " + named + " 个");
            if (vt == 0)
            {
                AnchorLog.Add("③ 未找到 MainGame 真 vtable（V1/V3/static_data/<PlayerData> 全链未通过）→ 拒绝定位");
                InventorySource = "未定位：MainGame 静态引用链未通过";
                return false;
            }
            AnchorMainGameVTable = vt;
            AnchorStaticData = sd;
            // 记录 MainGame 单例实例(主菜单检测 IsInGameSession / gameState 轮询的出发点)。
            // 放在 sd 确定处(而非全链成功后):主菜单期冷刷新失败也能记录,gameState 轮询才可用。
            for (int off = 0x00; off <= 0x500; off += 8)
            {
                long v = _mem.ReadLong(sd + off);
                if (v > 0x10000 && GetClassName(_mem.ReadLong(v)) == "MainGame") { MainGameInstance = v; break; }
            }
            // 【T7-F1 修复】gameState 偏移**不在此处标定**。
            // 此处只确定了 static_data，尚不能确证「当前在档内」；而**旧判据**只做单点读数的值域检查
            //（落在 0 或 1 两个值之内即认为可信）—— 该判据过弱：0 是对象字段最常见的取值
            //（零填充 / 未初始化），偏移一旦随版本变动落到零值区，
            // gsv == 0 就会**必然通过**校验（t7 评审 T7-F1：形式化断言）。
            // 标定改到第 ④ 级全链校验**全部通过之后**（已确证在档内，见下方 ④ 末尾）。
            // 未标定期间 GameStateOffsetVerified = 0 ⇒ GetGameState() 返回 -1（保守：闸失效、
            // 退回照常重试，仅损失该项优化 —— t5 §2.1 已论证该方向安全）。
            GameStateOffsetVerified = 0;
            AnchorLog.Add("③ MainGame 真 vtable=0x" + vt.ToString("X") + "  klass=0x" + klass.ToString("X"));
            AnchorLog.Add("   static_data=0x" + sd.ToString("X") + " → [+0x28] PlayerData=0x" + pd.ToString("X"));

            // ④ 全链值校验：PD → +0x58 Inventory → +0x38 容器 Item
            if (GetClassNameUncached(_mem.ReadLong(pd)) != PLAYERDATA_CLASS)
            {
                AnchorLog.Add("④ [static_data+0x28] 类名不是 PlayerData → 拒绝定位");
                InventorySource = "未定位：静态槽类名不符";
                return false;
            }
            long inv = _mem.ReadLong(pd + PLAYERDATA_INVENTORY);
            if (inv <= 0x10000 || GetClassNameUncached(_mem.ReadLong(inv)) != INVENTORY_CLASS)
            {
                AnchorLog.Add("④ PD+0x58 类名不是 Inventory → 拒绝定位");
                InventorySource = "未定位：PlayerData+0x58 非 Inventory";
                return false;
            }
            long item = _mem.ReadLong(inv + INVENTORY_CONTAINER);
            if (!IsPlayerInventory(item))
            {
                AnchorLog.Add("④ Inventory+0x38 未通过容器校验（类名=Item / itemId=inventory / List 自洽）→ 拒绝定位");
                InventorySource = "未定位：容器校验不通过";
                return false;
            }

            CurrentPlayerData = pd;
            CurrentInventory = inv;
            long belt = _mem.ReadLong(pd + PLAYERDATA_TOOLBELT);
            CurrentToolBelt = (belt > 0x10000 && GetClassNameUncached(_mem.ReadLong(belt)) == INVENTORY_CLASS) ? belt : 0;
            // 【硬约束①】PlayerData+0x80 的运行时校验：必须是指向类名 "GameRes" 的对象。
            // 不过即拒绝定位（调用方回落「等待游戏开始」退避）—— 宁可不定位，不可选错。
            // 本机正式版实测：PD+0x80 → "GameRes" → +0x10 = List`1（resValues, _size=92）。
            long gres = _mem.ReadLong(pd + PLAYERDATA_GAMERES);
            if (gres <= 0x10000 || GetClassNameUncached(_mem.ReadLong(gres)) != GAMERES_CLASS)
            {
                AnchorLog.Add("④ PD+0x80 未指向 " + GAMERES_CLASS + " 对象 → 拒绝定位");
                InventorySource = "未定位：PlayerData+0x80 非 " + GAMERES_CLASS;
                CurrentGameRes = 0;
                return false;
            }
            CurrentGameRes = gres;
            PlayerInventory = item;
            InventorySource = "当前存档锚（MainGame::<PlayerData> 现场解析，零硬编码）";

            // 【T7-F1 修复】gameState 偏移标定：位置改到第 ④ 级全链校验**全部通过之后**
            // （PD 类名 == PlayerData、+0x58 == Inventory、+0x38 容器自洽、+0x80 == GameRes
            //  逐级均通过 ⇒ 已确证「当前在档内」），且**要求 gsv == 1**：
            //   · 此处已确证在档内 → gameState 语义上必须是 1（InGame）；
            //   · 读到的不是 1（含 0 —— 零填充最常见）即说明 0x120 不再是 gameState 字段
            //     → 置 -1（保守：主菜单闸停用，退回「照常重试」，仅损失该项优化）。
            // 这样「偏移变动落到零值区也必然通过」的误通过通路被彻底堵死（t7 评审 T7-F1）。
            if (MainGameInstance > 0x10000)
            {
                int gsv = _mem.ReadInt(MainGameInstance + MAINGAME_GAMESTATE);
                bool gsOk = (gsv == 1);
                GameStateOffsetVerified = gsOk ? 1 : -1;
                AnchorLog.Add(gsOk
                    ? "④ gameState@+0x120 标定通过（在档内且值 == 1）"
                    : "④ gameState@+0x120 标定未通过（在档内但值 = " + gsv
                      + " ≠ 1）→ 主菜单闸停用，改走保守重试（仅损失该项优化）");
            }
            else
            {
                GameStateOffsetVerified = -1;
                AnchorLog.Add("④ MainGame 实例未记录 → gameState 闸停用（保守）");
            }

            AnchorLog.Add("④ 校验链通过：容器 0x" + item.ToString("X")
                + " itemId=" + ReadMonoString(_mem.ReadLong(item + ITEM_ITEMID))
                + " cap=" + _mem.ReadInt(item + ITEM_CAPACITY)
                + " used=" + _mem.ReadInt(item + ITEM_USED));
            return true;
        }

        /// <summary>
        /// 【换档判据·唯一决定性信号】静态槽是否仍指向缓存的 PlayerData（身份引用）。
        ///
        /// 读 static_data 块的 <c>STATIC_SLOT_PLAYERDATA</c>，与缓存的 CurrentPlayerData 比对。
        /// static_data 块地址在同一次会话内稳定（只在进程启动时变），而槽内容会在读档时被
        /// 游戏改写成新 PD —— 所以「槽值 != 缓存 PD」就是换档 / 重载的**正向**信号
        /// （成本 1 次读内存）。
        ///
        /// 不能只靠结构校验：实测读档后旧对象不释放，旧 PD 的
        /// +0x58 → Inventory → +0x38 → Item 链结构依然完全合法，会被误判为「仍有效」，
        /// 从而继续用旧档数据（用户最初报告的缺陷复发路径）。
        ///
        /// 本游戏**没有游戏内读档**：存档必须睡觉，读档必须回主菜单再继续 / 读档
        /// ⇒ 「换档」是稀疏事件，且**必然**改写该静态槽。因此只需这一个信号。
        ///
        /// **结构完好性不在此方法内**（见 <see cref="IsAnchorReadable"/>）：结构判据回答不了
        /// 「是不是当前那一个」，只能回答「此刻读不读得到」。旧实现把两者 AND 进同一个 bool，
        /// 导致「背包被清空（容器 size == 0）」这类结构抖动被调用方当成换档 ⇒ 清空重载
        /// （实测误判持续 57.8 s）。换档 / 回主菜单判定**必须**用本方法。
        /// </summary>
        public bool IsSameSaveSlot()
        {
            if (CurrentPlayerData <= 0x10000) return false;

            // 【换档判据】static_data 块 + 静态槽仍指向缓存的 PD？
            if (AnchorStaticData <= 0x10000) return false;
            return _mem.ReadLong(AnchorStaticData + STATIC_SLOT_PLAYERDATA) == CurrentPlayerData;
        }

        /// <summary>
        /// 【结构可读性·必要条件】缓存的锚结构此刻是否可读
        /// （PD 类名 + PlayerInventory 有效 + 容器自洽）。
        /// 只做几次读取，且绕过类名缓存 —— 否则地址失效后旧缓存会造成假命中。
        ///
        /// **只回答「这一轮能不能读」，绝不回答「是不是当前那一个」** ——
        /// 失败只允许触发「重新解析一次」或记诊断，**不得触发换档 / 清空重载**
        /// （旧实现把本判据混进换档判据，是「背包清空被误判为读档」的根因；
        /// 结构抖动是合法且常见的：背包空、过图、容器重排都会让它瞬时为 false）。
        /// </summary>
        public bool IsAnchorReadable()
        {
            if (CurrentPlayerData <= 0x10000) return false;

            // 以下为必要条件（结构完好性），不可单独作为换档判据
            if (GetClassNameUncached(_mem.ReadLong(CurrentPlayerData)) != PLAYERDATA_CLASS) return false;
            if (PlayerInventory <= 0x10000) return false;
            return IsPlayerInventory(PlayerInventory);
        }

        /// <summary>
        /// 【兼容保留】旧方法 = <see cref="IsSameSaveSlot"/> ∧ <see cref="IsAnchorReadable"/>，
        /// 与原实现的语义逐字等价（短路顺序亦一致）。
        ///
        /// 适用场景只有一个：**复用缓存前的廉价校验** —— 既要求锚仍指向当前存档，
        /// 又要求此刻结构可读；失败后果是**重新解析**（冷路径），**不是**换档重载。
        /// **换档 / 回主菜单判定不得使用本方法**（那会把结构抖动误判成换档），
        /// 请改用 <see cref="IsSameSaveSlot"/>。
        /// </summary>
        public bool IsAnchorStillValid()
        {
            return IsSameSaveSlot() && IsAnchorReadable();
        }

        /// <summary>
        /// 【硬约束①】gameState 偏移（MAINGAME_GAMESTATE=0x120）的运行时自校验结果：
        ///   0 = 尚未标定（首次解析前）；**1 = 已通过（在档内上下文下读数 == 1）**；-1 = 未通过 / 未知。
        ///
        /// 判据说明（T17-F2：与实现逐字对齐，避免误导后续维护者）：
        ///   **不是**「单点读数落在 0 或 1 两个值之内即通过」。标定发生在 <see cref="RefreshCurrentSaveAnchor"/> 第④级
        ///   全链校验**全部通过之后**（PD 类名 / +0x58 Inventory / +0x38 容器自洽 / +0x80 GameRes
        ///   逐级均通过 ⇒ 已确证「当前在档内」），此时只接受 `gsv == 1`；
        ///   读数 0（主菜单 / 零填充）与任何越界值一律判为「偏移不可信」→ 置 -1，
        ///   即**闸停用 → 退回保守重试**（仅损失该项优化，方向安全；见 t7 评审 T7-F1）。
        /// 只有 1 时 gameState 才被当作「主菜单 / 档内」判据；否则 <see cref="GetGameState"/>
        /// 一律返回 -1（未知）⇒ 调用方退回「照常重试」（宁可慢，不可选错）。
        /// 本机正式版实测：档内读数为 1、主菜单期为 0；邻域 0x110~0x130 无同值干扰字段
        /// （逐偏移采样见 tools\Probe-OffsetCheck.exe 输出）。
        /// </summary>
        public int GameStateOffsetVerified = 0;

        /// <summary>
        /// 【主菜单检测 2026-09-24】档内/主菜单的引用链完全相同(GameSave/PlayerData 均不清理),
        /// 唯一稳定信号是 MainGame.Instance.gameState(int:0=MainMenu,1=InGame,差分实测)。
        /// 主菜单(或读档加载期)返回 false → 调用方回落「等待游戏开始」。
        /// 【硬约束①】偏离值域时返回 false（等价于「未知」），不轻信未知偏移。
        /// </summary>
        public bool IsInGameSession()
        {
            return GetGameState() == 1;
        }

        /// <summary>
        /// 读 MainGame.gameState(0=MainMenu,1=InGame)。
        /// 返回 -1 表示「未知」：Instance 未记录 / 偏移自校验未通过 / 读数超出值域。
        /// 用于定位重试前的「是否已开始游戏」轻量探测(一次 4 字节读)。
        /// </summary>
        public int GetGameState()
        {
            if (MainGameInstance <= 0x10000) return -1;
            if (GameStateOffsetVerified != 1) return -1;   // 【硬约束①】未通过自校验 ⇒ 不采信
            int g = _mem.ReadInt(MainGameInstance + MAINGAME_GAMESTATE);
            return (g == 0 || g == 1) ? g : -1;            // 值域外 ⇒ 偏移不再成立
        }

        /// <summary>清掉库存缓存与类名缓存，下次重新定位（换档 / 进程重连时调用）。</summary>
        public void InvalidateInventory()
        {
            PlayerInventory = 0;
            InventorySource = "";
            InventoryVotes = 0;
            InventoryCandidates = 0;
            CurrentPlayerData = 0;
            CurrentGameRes = 0;
            CurrentInventory = 0;
            CurrentToolBelt = 0;
            AnchorMainGameVTable = 0;
            AnchorStaticData = 0;
            AnchorMonoDomain = 0;
            lock (_classGate) { _classNameCache.Clear(); }
            GameStateOffsetVerified = 0;
        }

        /// <summary>
        /// 不经缓存的类名读取：地址失效检测必须绕过缓存，
        /// 否则已释放的旧地址会一直返回记忆中的旧类名（假命中）。
        /// 同样需要掩掉 GC 借用的最低位（依据见 <see cref="GC_MARK_BIT_MASK"/> 的注释）——
        /// `IsAnchorReadable()` 正是走这条路读 PlayerData 类名，不掩码则过图/传送时
        /// 会因标记位污染而误判为「结构不可读」（P0a 之后不再升级为「读档 / 回主菜单」）。
        /// </summary>
        private string GetClassNameUncached(long vtable)
        {
            if (vtable <= 0x10000 || vtable >= 0x7FFFFFFF0000L) return null;
            vtable &= GC_MARK_BIT_MASK;          // 去掉 GC 借用的标记位
            long klass = _mem.ReadLong(vtable + MONO_VTABLE_KLASS);
            if (klass <= 0x10000 || klass >= 0x7FFFFFFF0000L) return null;
            long namep = _mem.ReadLong(klass + MONO_CLASS_NAME);
            if (namep <= 0x10000 || namep >= 0x7FFFFFFF0000L) return null;
            string s = _mem.ReadAscii(namep, 96);
            if (s == null || s.Length == 0 || s.Length >= 96) return null;
            return s;
        }

        /// <summary>校验一个地址是否仍是合格的玩家主库存容器 Item。</summary>
        public bool IsPlayerInventory(long item)
        {
            if (item == 0) return false;
            if (GetClassName(_mem.ReadLong(item)) != "Item") return false;
            if (ReadMonoString(_mem.ReadLong(item + ITEM_ITEMID)) != INVENTORY_ITEM_ID) return false;

            long list = _mem.ReadLong(item + ITEM_LIST);
            if (list == 0) return false;
            string lc = GetClassName(_mem.ReadLong(list));
            if (lc == null || lc.IndexOf("List") < 0) return false;

            // 【P0b 2026-09-26】`size == 0`（背包被清空）是**合法状态**，不得判为无效容器。
            //   实测（evidence\cause2_play_r1\anchor.csv，527 个 size=0 采样点）：
            //   used=0 / size=0 / cap=20 / arrLen=34，pdClassOk、invOk 全为 1 —— 其余子项
            //   **全部成立**，唯一失败项就是原来的 `size < 1`（既导致定位失败，又经旧
            //   IsAnchorStillValid 被误判为换档）。放宽仅限「空」这一种合法状态：
            //   下界改为只拒绝 size < 0（读坏值），上界由紧随其后的 `size > cap` 补齐。
            int size = _mem.ReadInt(list + LIST_SIZE);
            if (size < 0) return false;
            // 容器结构一致性：+0x48 必须等于 List._size
            // 【2026-09-26 口径标注 · 未改判据】实测该字段语义是 `Item.inventoryFillSize`
            //   （源码默认 −1），只在**玩家主库存**上恰好等于 List._size。
            //   本判据在既有实测覆盖到的容器（主背包 / 研究台 / 附近箱子）上都成立，故**保留原样**；
            //   但它的成立依赖「游戏已维护 fillSize」，属**潜在误拒风险**（可能拒绝一个合法容器 ⇒
            //   表现为「资源组未找到」）。已登记为待观察项，未做未经验证的放宽。
            if (_mem.ReadInt(item + ITEM_USED) != size) return false;
            int cap = _mem.ReadInt(item + ITEM_CAPACITY);
            if (cap < 1 || cap > 1024) return false;
            // 【P0b 新增比较·收紧】结构自洽性：已用格数不可能超过容量。
            //   旧实现无此项；它的意义是补偿 `size == 0` 时 `arrLen >= size` 退化为恒真
            //   所损失的判别力。依据：item+0x44 实为 Item.inventorySize（容量），
            //   item+0x30 的 List._size 为已用格数。实测 4546 样本中 size > cap 出现 0 次
            //   （cap 恒 20、size ≤ 19）⇒ 本项在实测覆盖范围内零影响；方向安全（拒绝优于选错）。
            if (size > cap) return false;

            long arr = _mem.ReadLong(list + LIST_ARR);
            if (arr == 0) return false;
            long arrLen = _mem.ReadLong(arr + ARRAY_LEN);
            if (arrLen < size || arrLen > 4096) return false;
            return true;
        }

        // ------------------------------------------------------------------
        // 资源原子组的「当前存档」归属判定（配合上方 CurrentSave 锚使用）
        //
        // 历史判据已删除：UI 标签 + 「最新投票者地址 / 票数 / 容量」三级比较。
        // 证伪记录（2026-09-23，五轮侦察）：
        //   ① 标签无区分度 —— 实测 64 个标签组件中 62 个都带 same 标签
        //      （含箱子 / 传送带容器 / 传送点仓库 / NPC 背包）；
        //   ② 票数不可用 —— 读档后旧主背包 4 票 > 当前主背包 2 票；
        //      同一主背包票数还会漂移（7 → 2）；
        //   ③ 基址最高不可用 —— 读档后旧主背包地址高于当前主背包（旧对象不释放）；
        //   ④ Inventory+0x40(ViewId) 有 38 个反例，不能作为主背包判据。
        // ------------------------------------------------------------------

        /// <summary>
        /// 用「当前存档锚」选出属于当前存档的资源原子组。
        /// 判定依据：该组的原子对象被当前存档对象闭包（PlayerData / GameRes / Inventory /
        /// toolBelt 及其一阶解引用：List 元素、数组元素、对象字段）引用。
        /// **恰好 1 组命中才采信**；0 组或多组命中一律返回 -1（宁可不选，不可选错）。
        /// </summary>
        public int PickGroupByCurrentSave(List<List<GameResAtom>> groups)
        {
            if (groups == null || groups.Count == 0) return -1;
            if (CurrentPlayerData == 0 || CurrentGameRes == 0) return -1;

            HashSet<long> closure = BuildCurrentSaveClosure();
            if (closure.Count == 0) return -1;

            int hit = -1;
            int hits = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                bool matched = false;
                for (int k = 0; k < groups[g].Count && !matched; k++)
                    if (closure.Contains(groups[g][k].Address)) matched = true;
                if (matched) { hits++; hit = g; }
            }
            AnchorLog.Add("⑤ 选组：闭包 " + closure.Count + " 个引用目标，命中组数 " + hits
                + (hits == 1 ? "（唯一，采信）" : "（不唯一，拒绝采信）"));
            return hits == 1 ? hit : -1;
        }

        /// <summary>当前存档对象闭包（PlayerData / GameRes / Inventory / toolBelt 及一阶解引用）。</summary>
        private HashSet<long> BuildCurrentSaveClosure()
        {
            HashSet<long> set = new HashSet<long>();
            List<long> roots = new List<long>();
            if (CurrentPlayerData != 0) roots.Add(CurrentPlayerData);
            if (CurrentGameRes != 0) roots.Add(CurrentGameRes);
            if (CurrentInventory != 0) roots.Add(CurrentInventory);
            if (CurrentToolBelt != 0) roots.Add(CurrentToolBelt);
            for (int i = 0; i < roots.Count; i++) ExpandRefs(roots[i], set, 0);
            return set;
        }

        /// <summary>把对象的引用目标（字段值 / List 元素 / 数组元素）加入集合，限深限宽避免噪音。</summary>
        private void ExpandRefs(long obj, HashSet<long> set, int depth)
        {
            if (obj <= 0x10000 || obj >= 0x7FFFFFFF0000L) return;
            if (depth > 2) return;
            string cls = GetClassName(_mem.ReadLong(obj));
            if (cls == null) return;

            if (cls.EndsWith("[]"))
            {
                long len = _mem.ReadLong(obj + ARRAY_LEN);
                if (len < 0 || len > 512) return;
                for (long i = 0; i < len; i++)
                {
                    long e = _mem.ReadLong(obj + ARRAY_DATA + i * 8);
                    if (e <= 0x10000) continue;
                    if (set.Add(e)) ExpandRefs(e, set, depth + 1);
                }
                return;
            }

            if (cls.IndexOf("List") >= 0)
            {
                long arr = _mem.ReadLong(obj + LIST_ARR);
                int size = _mem.ReadInt(obj + LIST_SIZE);
                if (arr > 0x10000 && size >= 0 && size <= 512)
                {
                    for (long i = 0; i < size; i++)
                    {
                        long e = _mem.ReadLong(arr + ARRAY_DATA + i * 8);
                        if (e <= 0x10000) continue;
                        if (set.Add(e)) ExpandRefs(e, set, depth + 1);
                    }
                }
                return;
            }

            for (int off = 0x10; off <= 0x200; off += 8)
            {
                long v = _mem.ReadLong(obj + off);
                if (v <= 0x10000 || v >= 0x7FFFFFFF0000L) continue;
                if (set.Add(v)) ExpandRefs(v, set, depth + 1);
            }
        }

        // 【已删除 · t5 死代码清理】本节原有 1 个「兜底取基址最高」的容器查找成员：
        //   已被 §2.4.1 正面证伪并禁用（实测会选到 cap=2 的祭祀台而不是玩家主库存），
        //   src 与 tools 双侧零引用。（按项目既有约定不列名，原文见 backup_src_t5）

        /// <summary>
        /// 找出内容等于 text 的 MonoString 对象地址（而非字符位置）。
        /// needle 带上 String 的 length 前缀，命中位置即对象 +0x10。
        /// </summary>
        public List<long> FindMonoStringObjects(string text, int max)
        {
            List<long> res = new List<long>();
            if (string.IsNullOrEmpty(text) || text.Length > 200) return res;

            byte[] chars = Encoding.Unicode.GetBytes(text);
            byte[] needle = new byte[4 + chars.Length];
            int n = text.Length;
            needle[0] = (byte)(n & 0xFF);
            needle[1] = (byte)((n >> 8) & 0xFF);
            needle[2] = (byte)((n >> 16) & 0xFF);
            needle[3] = (byte)((n >> 24) & 0xFF);
            Array.Copy(chars, 0, needle, 4, chars.Length);

            List<long> hits = _mem.ScanParallel(needle, 0, 0, max * 8);
            for (int i = 0; i < hits.Count; i++)
            {
                long obj = hits[i] - MONO_STRING_LENGTH;
                if (ReadMonoString(obj) != text) continue;
                bool dup = false;
                for (int k = 0; k < res.Count; k++)
                    if (res[k] == obj) { dup = true; break; }
                if (!dup) res.Add(obj);
                if (res.Count >= max) break;
            }
            return res;
        }

        /// <summary>
        /// 遍历玩家主库存，返回每格物品的 { Address, ItemId, Count }。
        /// 自动过滤 null 槽、非 Item 对象、无名 itemId 与 "empty" 占位项。
        /// </summary>
        public List<ItemRef> ListInventoryItems()
        {
            List<ItemRef> list = new List<ItemRef>();
            long inv = FindPlayerInventory();
            if (inv == 0) return list;

            long lst = _mem.ReadLong(inv + ITEM_LIST);
            if (lst == 0) return list;
            long arr = _mem.ReadLong(lst + LIST_ARR);
            if (arr == 0) return list;

            int size = _mem.ReadInt(lst + LIST_SIZE);
            long arrLen = _mem.ReadLong(arr + ARRAY_LEN);
            if (arrLen < size) size = (int)arrLen;
            if (size < 0) size = 0;
            if (size > 512) size = 512;

            for (int i = 0; i < size; i++)
            {
                long e = _mem.ReadLong(arr + ARRAY_DATA + i * 8);
                if (e == 0) continue;
                if (GetClassName(_mem.ReadLong(e)) != "Item") continue;
                string id = ReadMonoString(_mem.ReadLong(e + ITEM_ITEMID));
                if (string.IsNullOrEmpty(id)) continue;
                if (id == "empty") continue;
                ItemRef r = new ItemRef();
                r.Address = e;
                r.ItemId = id;
                r.Count = ReadItemCount(e);
                list.Add(r);
            }
            return list;
        }

        /// <summary>读物品数量：Item + 0x40 的 int32（只读 4 字节）。</summary>
        public int ReadItemCount(long itemAddr)
        {
            if (itemAddr == 0) return 0;
            byte[] b = _mem.ReadBytes(itemAddr + ITEM_COUNT, 4);
            if (b == null || b.Length != 4) return 0;
            return BitConverter.ToInt32(b, 0);
        }

        /// <summary>
        /// 写入物品数量（Item + 0x40，int32，只写 4 字节）。
        /// 写前双重校验：① vtable 类名必须是 "Item"；② +0x10 的 itemId 必须等于预期值。
        /// 写后复读确认数量、vtable 与 itemId 三者均未被破坏，任一不符即返回 false。
        /// 不确定就绝不写。
        /// </summary>
        /// <summary>
        /// 【结构链资源组 2026-09-24】直接遍历 PlayerData+0x80 → GameRes+0x10 → resValues
        /// (List&lt;GameResAtom&gt;)——这就是游戏自己的玩家资源容器,天然=当前存档,零歧义、毫秒级。
        /// 背景:读档场景实测 resValues 的 atom 对象不再地址连续(List 重排/对象散落),
        /// 旧「全堆扫描+连续聚组+闭包选组」判据会 0 命中 → 永不就绪(无限循环),故本路径为主路径。
        /// 校验:数组类名含 GameResAtom、逐元素类名校验、组内必须同时含 money 与 energy(基本签名)。
        /// 任一不满足返回空列表(调用方回退旧扫描路径)。调用前提:RefreshCurrentSaveAnchor() 已成功。
        /// </summary>
        public List<GameResAtom> BuildResGroupViaAnchor()
        {
            List<GameResAtom> res = new List<GameResAtom>();
            try
            {
                if (CurrentPlayerData <= 0x10000 || CurrentGameRes <= 0x10000) return res;
                long list = _mem.ReadLong(CurrentGameRes + GAMERES_RESVALUES);
                if (list <= 0x10000) return res;
                long arr = _mem.ReadLong(list + LIST_ARR);
                if (arr <= 0x10000) return res;
                int n = _mem.ReadInt(list + LIST_SIZE);
                if (n <= 0 || n > 4096) return res;
                string arrCls = GetClassName(_mem.ReadLong(arr));
                if (arrCls == null || arrCls.IndexOf("GameResAtom") < 0) return res;
                for (int i = 0; i < n; i++)
                {
                    long a = _mem.ReadLong(arr + ARRAY_DATA + i * 8);
                    if (a <= 0x10000) continue;
                    long vt = _mem.ReadLong(a);
                    if (vt <= 0x10000) continue;
                    if (GetClassName(vt) != "GameResAtom") continue;
                    long sp = _mem.ReadLong(a + ATOM_TYPE);
                    if (sp <= 0x10000) continue;
                    string ty = ReadMonoString(sp);
                    if (ty == null) continue;
                    GameResAtom g = new GameResAtom();
                    g.Address = a; g.VTable = vt; g.TypeName = ty;
                    g.Value = _mem.ReadFloat(a + ATOM_VALUE);
                    res.Add(g);
                }
                bool hasMoney = false, hasEnergy = false;
                foreach (GameResAtom g in res)
                {
                    if (g.TypeName == "money") hasMoney = true;
                    else if (g.TypeName == "energy") hasEnergy = true;
                }
                if (!hasMoney || !hasEnergy) res.Clear();
            }
            // 【t3 C-2】结构链解析异常改为可见诊断（调用方据此回落「等待游戏开始」退避）
            catch (Exception ex) { AddDiagnostic("BuildResGroupViaAnchor", ex); }
            return res;
        }

        /// <summary>
        /// 【科学快速定位 2026-09-24】沿游戏自身结构链直达 WGO 容器物品（毫秒级,零硬编码,逐级类名校验）：
        ///   static_data → MainGame 实例 → GameSave → worldData → cache(WgoDataCache)
        ///   → +0x10 字典 wgoDataByUidCache(Dictionary&lt;Guid,WgoData&gt;,Entry=32 字节,value 槽 +0x18)
        ///   → 逐个 WgoData 遍历字段槽找 Inventory → +0x38 容器 Item → List 内找 itemId。
        /// 依据:研究台(survey_wgo)的分解产物 science 存放在研究台容器(实测不在玩家主背包);
        /// MainGame.get_WorldData = Instance.GameSave.worldData（IL）。找不到返回 0。
        /// 调用前提:RefreshCurrentSaveAnchor() 已成功（依赖 AnchorStaticData）。
        /// </summary>
        public long FindItemViaWorld(string itemId)
        {
            try
            {
                if (AnchorStaticData <= 0x10000) return 0;
                long inst = 0;
                for (int off = 0x00; off <= 0x500 && inst == 0; off += 8)
                {
                    long v = _mem.ReadLong(AnchorStaticData + off);
                    if (v > 0x10000 && GetClassName(_mem.ReadLong(v)) == "MainGame") inst = v;
                }
                if (inst == 0) return 0;
                long gs = FindFieldObjByClass(inst, "GameSave", 0x400);
                if (gs == 0) return 0;
                long wd = FindFieldObjByClass(gs, "WorldData", 0x400);
                if (wd == 0) return 0;
                long wc = FindFieldObjByClass(wd, "WgoDataCache", 0x100);
                if (wc == 0) return 0;
                long dict = _mem.ReadLong(wc + 0x10);
                if (dict <= 0x10000) return 0;
                string dcls = GetClassName(_mem.ReadLong(dict));
                if (dcls == null || !dcls.Contains("Dictionary")) return 0;
                long entries = _mem.ReadLong(dict + 0x18);
                if (entries <= 0x10000) return 0;
                int arrLen = _mem.ReadInt(entries + 0x18);
                if (arrLen <= 0 || arrLen > 0x100000) return 0;
                // 【硬约束①】Entry 布局运行时自校验（教训 38：遍历前必须验证）。
                // ① 元素数组必须是 Entry[]（类名含 "Entry"）；
                // ② 步进 32 / value 槽 +0x18 的布局自洽性：抽样槽的 next(int@+0x04)
                //    必须落在「-1（链表结束）或 [0, arrLen)」——用错误步进时几乎必然越界。
                // 任一不过即返回 0（科学条目置灰），绝不用未验证的布局去猜地址。
                if (!VerifyWgoEntryLayout(entries, arrLen)) return 0;
                for (int i = 0; i < arrLen; i++)
                {
                    long wgo = _mem.ReadLong(entries + 0x20 + i * 32 + 0x18);
                    if (wgo <= 0x10000) continue;
                    if (GetClassName(_mem.ReadLong(wgo)) != "WgoData") continue;
                    long hit = FindItemInWgoInventory(wgo, itemId);
                    if (hit != 0) return hit;
                }
            }
            // 【t3 C-3】科学结构链异常改为可见诊断（否则条目永久置灰且无任何线索）
            catch (Exception ex) { AddDiagnostic("FindItemViaWorld", ex); }
            return 0;
        }

        /// <summary>
        /// 【硬约束①】字典 Entry 布局自校验（本机正式版实测：类名 "Entry[]"、步进 32、
        /// value 槽 +0x18、995 个非空槽的 next 全部 ∈ {-1} ∪ [0,1931)）。
        /// 返回 false 表示布局与预期不符 → 调用方放弃该路径（宁可不定位，不可选错）。
        /// </summary>
        private bool VerifyWgoEntryLayout(long entries, int arrLen)
        {
            long arrVt = _mem.ReadLong(entries);
            if (arrVt <= 0x10000) return false;
            string cls = GetClassName(arrVt);
            if (cls == null || cls.IndexOf("Entry") < 0) return false;

            // 抽样前 64 个非空槽校验 next 值域（错误步进会立刻表现为越界）
            int checkedSlots = 0;
            for (int i = 0; i < arrLen && checkedSlots < 64; i++)
            {
                long e = entries + 0x20 + i * 32;
                long val = _mem.ReadLong(e + 0x18);
                if (val <= 0x10000) continue;              // 空槽（Dictionary 允许空洞）
                int next = _mem.ReadInt(e + 0x04);
                if (next < -1 || next >= arrLen) return false;
                long vt = _mem.ReadLong(val);
                if (vt <= 0x10000) return false;
                checkedSlots++;
            }
            // 抽样为空（数组全空）不算失败：此时返回值本就是 0，无风险
            return true;
        }

        /// <summary>在对象前 size 字节内找「引用对象类名==cls」的字段槽,返回被引用对象。</summary>
        private long FindFieldObjByClass(long obj, string cls, int size)
        {
            for (int off = 0x10; off <= size; off += 8)
            {
                long v = _mem.ReadLong(obj + off);
                if (v <= 0x10000) continue;
                long vt = _mem.ReadLong(v);
                if (vt <= 0x10000) continue;
                if (GetClassName(vt) == cls) return v;
            }
            return 0;
        }

        /// <summary>遍历 WgoData 的字段槽找 Inventory,在其容器 List 里找 itemId 匹配且 count&gt;0 的物品。</summary>
        private long FindItemInWgoInventory(long wgo, string itemId)
        {
            for (int off = 0x10; off <= 0x300; off += 8)
            {
                long inv = _mem.ReadLong(wgo + off);
                if (inv <= 0x10000) continue;
                long ivt = _mem.ReadLong(inv);
                if (ivt <= 0x10000) continue;
                if (GetClassName(ivt) != "Inventory") continue;
                long cont = _mem.ReadLong(inv + INVENTORY_CONTAINER);
                if (cont <= 0x10000) continue;
                long cl = _mem.ReadLong(cont + ITEM_LIST);
                if (cl <= 0x10000) continue;
                long carr = _mem.ReadLong(cl + LIST_ARR);
                if (carr <= 0x10000) continue;
                int n = _mem.ReadInt(cl + LIST_SIZE);
                if (n <= 0 || n > 1024) continue;
                for (int k = 0; k < n; k++)
                {
                    long it = _mem.ReadLong(carr + 0x20 + k * 8);
                    if (it <= 0x10000) continue;
                    long sp = _mem.ReadLong(it + ITEM_ITEMID);
                    if (sp <= 0x10000) continue;
                    if (ReadMonoString(sp) == itemId && _mem.ReadInt(it + ITEM_COUNT) > 0)
                        return it;
                }
            }
            return 0;
        }

        public bool WriteItemCount(long itemAddr, string expectedItemId, int value)
        {
            if (itemAddr == 0 || string.IsNullOrEmpty(expectedItemId)) return false;

            long vt = _mem.ReadLong(itemAddr);
            if (vt == 0 || GetClassName(vt) != "Item") return false;
            long idPtr = _mem.ReadLong(itemAddr + ITEM_ITEMID);
            if (ReadMonoString(idPtr) != expectedItemId) return false;

            if (!_mem.WriteBytes(itemAddr + ITEM_COUNT, BitConverter.GetBytes(value))) return false;

            // 复读确认
            if (ReadItemCount(itemAddr) != value) return false;
            if (_mem.ReadLong(itemAddr) != vt) return false;
            if (_mem.ReadLong(itemAddr + ITEM_ITEMID) != idPtr) return false;
            return true;
        }

        // ------------------------------------------------------------------
        // 运行时本地化词表直读（物品中文名，零外部依赖）
        //
        // 目标：中文物品名直接来自游戏运行时内存，不再依赖外部 CSV，
        //       也不往 exe 里嵌入数据 —— 单文件 exe，名字与游戏版本永远一致。
        //
        // 实测结构（64 位 Mono / Unity 6，与 tools\LocExtract 的结论一致）：
        //   Dictionary<string,string> 的 Entry[] 数组对象：
        //       +0x18 max_length(guintptr)        +0x20 元素区
        //   Entry 元素 24 字节：
        //       +0x00 hashCode(int32)  +0x04 next(int32)
        //       +0x08 key(MonoString*) +0x10 value(MonoString*)
        //   若某引用位置 p 正是 value 槽，则 key 槽 = p - 8。
        //   key 是本地化 ID（与游戏内 itemId 同名，如 "egg_chicken"），
        //   value 是当前语言文本（中文表里形如 "<nobr>鸡蛋</nobr>"，需剥富文本标记）。
        //
        // 定位流程（整张表只扫一次，之后查询是纯内存查找 —— 同一 ID 不会重复扫描）：
        //   ① FindMonoStringObjects(itemId) 找到该 ID 的 MonoString 对象
        //   ② ScanPointersTo(...) 得到引用这些对象的全部槽位
        //   ③ 对每个候选槽双向校验（r 当 key 槽 / 当 value 槽），判据为
        //      「key 槽是 System.String 且内容 == itemId」「value 槽是 System.String 且含中文」；
        //      英文表因 value 不含中文而被自动排除 —— 取第一个通过者作锚点
        //   ④ 沿 24 字节步长前后探出连续有效区，再定位所在 Entry[] 数组对象，
        //      按其 max_length 遍历整张表（表中间允许空洞，故不能只靠连续区）
        //   ⑤ 只登记 value 含中文的条目 → itemId -> 中文名
        //
        // 只读实现：全程只有 ReadBytes / ReadLong / ReadInt，不写游戏内存。
        // ------------------------------------------------------------------

        private const int ENTRY_STEP = 24;
        private const int ENTRY_KEY_OFF = 0x08;
        private const int ENTRY_VALUE_OFF = 0x10;
        private const int ENTRY_PTR_STEP = 0x08;         // key 槽 -> value 槽 只差 8 字节
        private const long ENTRY_MAX_SPAN = 0x400000;    // 连续区探测的最大跨度
        private const long ENTRY_MAX_LEN = 5000000;      // Entry[] 数组长度上限（防误判）
        private const long ARRAY_SCAN_BACK = 0x100000;   // 数组对象向上搜索范围
        private const int ANCHOR_TRY_MAX = 8;            // 最多尝试的候选锚点数
        private const int TABLE_MIN_PLAUSIBLE = 64;      // 条目数达到此值即认为拿到了完整词表

        /// <summary>内存直读的备用种子（这些 ID 在任何语言的本地化表里都存在）。</summary>
        private static readonly string[] LocalizationSeedFallback = new string[]
        {
            "faith", "science", "money", "energy", "wood", "stone", "iron", "water"
        };

        /// <summary>中文词表索引（itemId → 中文名）。【t3 A-2】跨线程共享 → 一切访问走 <see cref="_locGate"/>。</summary>
        private readonly Dictionary<string, string> _locNames = new Dictionary<string, string>();
        // 【t3 A-2】索引状态同样跨线程（预热线程写 / UI 线程读）→ volatile
        private volatile bool _locIndexBuilt = false;
        private volatile bool _locIndexFailed = false;
        private string _locIndexSeed = "";
        private volatile int _locIndexEntries = 0;
        private double _locIndexSeconds = 0.0;

        // ------------------------------------------------------------------
        // 【方案A】运行时别名表 aliases1/aliases2（LazyBearTechnology.LLBase 的 public 实例字段，
        //   List<string>，随语言对象从 resources.assets 反序列化）。
        //   与词表 **_locNames 完全独立**存放：查询时串联，绝不合并（合并会污染
        //   CopyLocalizedNames / LocalizedNameCount 的语义与缓存指纹）。
        //   定位链与词表同构：内容锚点字符串 → String[] 数组头(r-0x20) → List<string>(+0x10)
        //   → LL 实例（ClassNameOf == "LL" 且 id == "zh_cn"）→ aliases1 @ +0x30 / aliases2 @ +0x38。
        //   偏移由运行时标定（不硬编码），并逐条与词表交叉验证；任一不过即整体拒绝。
        // ------------------------------------------------------------------
        private readonly List<string> _aliasFrom = new List<string>();
        private readonly List<string> _aliasTo = new List<string>();
        /// <summary>别名键 → aliases1 中的**首次**出现下标（复刻 List.IndexOf 语义）。</summary>
        private readonly Dictionary<string, int> _aliasIndex = new Dictionary<string, int>();
        private volatile bool _aliasIndexBuilt = false;
        private volatile bool _aliasIndexFailed = false;
        private volatile int _aliasIndexEntries = 0;
        private double _aliasIndexSeconds = 0.0;
        private string _aliasIndexDiag = "尚未建立";

        /// <summary>别名跟链的防环保险。游戏 L() 是**无上限递归**，我们必须自己设上限。</summary>
        private const int ALIAS_MAX_HOPS = 64;
        /// <summary>别名表条数的合理区间（实测正式版 zh_cn 为 8709；给宽区间，不写死单值）。</summary>
        private const int ALIAS_MIN_PLAUSIBLE = 1000;
        private const int ALIAS_MAX_PLAUSIBLE = 20000;
        /// <summary>内容锚点：离线/运行时别名表的首行 alias_from。是**内容**，不是地址。</summary>
        private const string ALIAS_ANCHOR = "garden_empty_1_place";
        /// <summary>四重内容验证的抽样规模。</summary>
        private const int ALIAS_VERIFY_SAMPLE = 200;
        private static readonly string[] KnownLangIds = new string[]
        {
            "en", "de", "fr", "pt-br", "es", "es-mx", "ru", "uk-ua", "it",
            "pl", "tr", "ja", "zh_cn", "zh_cht", "ko", "th", "vn"
        };

        /// <summary>
        /// 查一个 itemId 的中文名（数据来自游戏运行时内存）。
        /// 首次调用会按需建立中文词表索引（一次全堆扫描，数秒级）；
        /// 之后同一进程内的所有查询都是纯内存查找，不会重复扫描。
        /// 查不到返回 null（不回退、不抛异常）。
        /// </summary>
        public string LookupLocalizedName(string itemId)
        {
            if (itemId == null || itemId.Length == 0) return null;

            string hit;
            lock (_locGate) { if (_locNames.TryGetValue(itemId, out hit)) return hit; }

            if (!_locIndexBuilt && !_locIndexFailed)
            {
                List<string> seeds = new List<string>();
                seeds.Add(itemId);
                for (int i = 0; i < LocalizationSeedFallback.Length; i++)
                    seeds.Add(LocalizationSeedFallback[i]);
                EnsureLocalizedNameIndex(seeds);
                lock (_locGate) { if (_locNames.TryGetValue(itemId, out hit)) return hit; }
            }
            return null;
        }

        /// <summary>
        /// 只查已建立的中文词表索引，绝不触发扫描 —— 可安全地在 UI 线程调用。
        /// </summary>
        public bool TryGetLocalizedName(string itemId, out string name)
        {
            name = null;
            if (itemId == null || itemId.Length == 0) return false;
            string hit;
            lock (_locGate) { if (!_locNames.TryGetValue(itemId, out hit)) return false; }
            name = hit;
            return true;
        }

        /// <summary>
        /// 批量预取：先确保中文词表索引已建立（以 itemIds 内的 ID 依次作种子，
        /// 再退到内置种子），然后返回这批 ID 中命中中文名的个数。
        /// 适合在枚举完背包物品后于后台线程调用，避免 UI 线程做重扫描而卡顿。
        /// </summary>
        public int PrefetchLocalizedNames(ICollection<string> itemIds)
        {
            if (!_locIndexBuilt && !_locIndexFailed)
            {
                List<string> seeds = new List<string>();
                if (itemIds != null)
                {
                    foreach (string id in itemIds)
                        if (id != null && id.Length > 0) seeds.Add(id);
                }
                for (int i = 0; i < LocalizationSeedFallback.Length; i++)
                    seeds.Add(LocalizationSeedFallback[i]);
                EnsureLocalizedNameIndex(seeds);
            }
            else
            {
                // 【方案A】词表已就绪（含缓存装载路径）时，补建别名表
                EnsureAliasIndex();
            }
            if (itemIds == null) return 0;

            int hit = 0;
            lock (_locGate)
            {
                foreach (string id in itemIds)
                    if (id != null && _locNames.ContainsKey(id)) hit++;
            }
            return hit;
        }

        /// <summary>清空中文词表索引与缓存（进程重连 / 切换存档 / 切换游戏语言后调用）。</summary>
        public void ClearLocalizedNameCache()
        {
            lock (_locGate) { _locNames.Clear(); }
            _locIndexBuilt = false;
            _locIndexFailed = false;
            _locIndexSeed = "";
            _locIndexEntries = 0;
            _locIndexSeconds = 0.0;
            // 【方案A】别名表与词表同生命周期：一起清（进程重连 / 换档 / 换语言后重建）
            lock (_locGate)
            {
                _aliasFrom.Clear();
                _aliasTo.Clear();
                _aliasIndex.Clear();
            }
            _aliasIndexBuilt = false;
            _aliasIndexFailed = false;
            _aliasIndexEntries = 0;
            _aliasIndexSeconds = 0.0;
            _aliasIndexDiag = "尚未建立";
        }

        /// <summary>已建立的中文条目数（0 表示未建立或未取到）。</summary>
        public int LocalizedNameCount { get { return _locIndexEntries; } }

        /// <summary>
        /// 把已建立的中文词表索引拷贝到目标字典（仅补目标里还没有的 key），返回新增条数。
        /// 供 UI 侧一次性装载整张表 —— 这样即使背包为空，只要内存直读成功就有中文名可用。
        /// 持锁期间只做字典遍历（约 1 ms / 9300 条），无内存读取。
        /// </summary>
        public int CopyLocalizedNames(IDictionary<string, string> dst)
        {
            if (dst == null) return 0;
            int n = 0;
            lock (_locGate)
            {
                foreach (KeyValuePair<string, string> kv in _locNames)
                {
                    if (dst.ContainsKey(kv.Key)) continue;
                    dst[kv.Key] = kv.Value;
                    n++;
                }
            }
            return n;
        }

        /// <summary>中文词表索引状态，供日志与报告使用。</summary>
        public string LocalizedNameIndexStatus
        {
            get
            {
                if (_locIndexBuilt)
                    return "已建立 " + _locIndexEntries + " 条（种子 '" + _locIndexSeed
                        + "'，耗时 " + _locIndexSeconds.ToString("F1") + "s）";
                if (_locIndexFailed) return "建立失败：内存中未找到中文本地化字典";
                return "尚未建立";
            }
        }

        private bool EnsureLocalizedNameIndex(List<string> seeds)
        {
            if (_locIndexBuilt) return true;
            if (_locIndexFailed) return false;

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < seeds.Count && !_locIndexBuilt; i++)
                TryBuildLocalizedNameIndex(seeds[i]);
            sw.Stop();
            _locIndexSeconds = sw.Elapsed.TotalSeconds;
            if (!_locIndexBuilt) _locIndexFailed = true;
            // 【方案A】词表就绪后**并列**建立别名表。两者独立成败：
            //   别名表建立失败 ⇒ 只让后续回退链退回纯词表，绝不影响词表索引本身。
            if (_locIndexBuilt) EnsureAliasIndex();
            return _locIndexBuilt;
        }

        /// <summary>
        /// 以 seed 为锚点一次性读出整张中文本地化词表。
        /// 返回是否成功建立索引。
        /// </summary>
        private bool TryBuildLocalizedNameIndex(string seed)
        {
            if (seed == null || seed.Length == 0 || seed.Length > 200) return false;

            List<long> objs = FindMonoStringObjects(seed, 16);
            if (objs.Count == 0) return false;

            List<long> refs = _mem.ScanPointersTo(objs, 200000);
            if (refs.Count == 0) return false;

            // ③ 双向校验，收集候选锚点（value 槽位置）。
            //    注意：Entry 内 key 槽(+0x08) 与 value 槽(+0x10) 只相差 8 字节，
            //    所以「命中位置 r 是 key 槽」时 value 槽 = r + 8（不是 r + 0x10）。
            List<long> anchors = new List<long>();
            for (int i = 0; i < refs.Count && anchors.Count < ANCHOR_TRY_MAX; i++)
            {
                long r = refs[i];
                string k, v;
                // 方向 A：命中位置 r 是 Entry 的 key 槽 → value 槽 = r + 8
                long pa = r + ENTRY_PTR_STEP;
                if (IsEntryValueSlot(pa, out k, out v) && k == seed && HasCjk(v))
                {
                    if (!anchors.Contains(pa)) anchors.Add(pa);
                    continue;
                }
                // 方向 B：命中位置 r 本身是 value 槽 → key 槽 = r - 8
                if (IsEntryValueSlot(r, out k, out v) && k == seed && HasCjk(v))
                {
                    if (!anchors.Contains(r)) anchors.Add(r);
                }
            }
            if (anchors.Count == 0) return false;

            // ④ 逐个锚点试着读出整张表，取条目最多的那一份
            //    （防误命中：例如 string[] 数组里相邻的 ASCII/中文指针也可能骗过单槽校验）
            Dictionary<string, string> best = null;
            for (int a = 0; a < anchors.Count; a++)
            {
                Dictionary<string, string> table = ReadLocalizationTable(anchors[a]);
                if (table == null || table.Count == 0) continue;
                if (best == null || table.Count > best.Count) best = table;
                if (best.Count >= TABLE_MIN_PLAUSIBLE) break;
            }
            if (best == null || best.Count == 0) return false;

            // ⑤ 登记（【t3 A-2】跨线程共享字典 → 持锁写入）
            lock (_locGate)
            {
                foreach (KeyValuePair<string, string> kv in best)
                    if (!_locNames.ContainsKey(kv.Key)) _locNames[kv.Key] = kv.Value;
                _locIndexEntries = _locNames.Count;
            }

            _locIndexSeed = seed;
            _locIndexBuilt = true;
            return true;
        }

        // ==================================================================
        // 【方案A】运行时别名表 aliases1/aliases2 —— 建立、索引与解析
        //   静态依据（反编译源码 LazyBearTechnology.cs，方法名 LLBase.L / HasL / AddAliases）：
        //     aliases1 / aliases2 = LLBase 的 public 实例字段，List<string>，
        //     无 [NonSerialized] ⇒ 随语言对象从 resources.assets 反序列化；
        //     持有者 = LLBase.currentLang（protected static LL）；LL : LLBase。
        //     分配器 AddAliases() 保证两表**等长**且键去重。
        //   定位链（与词表同构，全部由内容锚点自举，零地址硬编码）：
        //     锚点字符串 → MonoString → ScanPointersTo → String[] 数组头(r-0x20)
        //     → List<string>(_items@+0x10) → LL 实例（类名 == "LL" 且 id == "zh_cn"）
        //     → aliases1 @ 运行时标定偏移 / aliases2 = 该偏移 + 8
        //   验证不过 ⇒ 整体拒绝（_aliasIndexFailed），查询退回纯词表行为。
        // ==================================================================

        /// <summary>别名表是否已就绪（供 UI / 日志查询）。</summary>
        public bool IsAliasTableReady { get { return _aliasIndexBuilt; } }

        /// <summary>别名表的去重键条数（0 表示未建立或未采用）。</summary>
        public int AliasTableCount { get { return _aliasIndexEntries; } }

        /// <summary>
        /// 【t34 · U1】别名表的**行数**（`_aliasFrom.Count`，**含重复键**；实测 8709）。
        ///   ⚠ 与 <see cref="AliasTableCount"/>（**去重键数** `_aliasIndex.Count`，实测 8697）是
        ///   **两个不同口径**，差值正是 `.alias` 正文里重复 from 的条数（离线实测 12 条）。
        ///   凡是要与**缓存文件头**的条数比较的地方（`.alias` 的 `count`、名表缓存的 `alias=`）
        ///   **必须用行数** —— 两个缓存文件记的都是行数。
        ///   历史事故：自愈路径曾误传去重键数 8697，与 `.alias` 的 8709 不等 ⇒ 自愈写缓存之后
        ///   每次启动都被 F4 的条数交叉判据误拒、退回现场全量重建。
        /// </summary>
        public int AliasTableRowCount { get { return _aliasFrom.Count; } }

        /// <summary>别名表状态（供日志与报告使用）。</summary>
        public string AliasTableStatus
        {
            get
            {
                if (_aliasIndexBuilt)
                    return "已建立 " + _aliasIndexEntries + " 条（耗时 "
                        + _aliasIndexSeconds.ToString("F1") + "s，" + _aliasIndexDiag + "）";
                if (_aliasIndexFailed) return "建立失败：" + _aliasIndexDiag;
                return "尚未建立";
            }
        }

        /// <summary>建立别名表（幂等；已建立或已判定失败则直接返回）。</summary>
        private void EnsureAliasIndex()
        {
            if (_aliasIndexBuilt || _aliasIndexFailed) return;
            // 词表还没读到中文内容时不必建（交叉验证需要词表）
            if (_locNames.Count == 0) { _aliasIndexDiag = "词表为空，跳过"; return; }
            TryBuildAliasIndex();
        }

        /// <summary>
        /// 建立运行时别名表索引。失败即置 <see cref="_aliasIndexFailed"/> 并清空半成品：
        /// 「宁可不定位，不可选错」—— 验证不过就整体拒绝，绝不留半信半疑的表。
        /// </summary>
        private bool TryBuildAliasIndex()
        {
            if (_aliasIndexBuilt) return true;
            if (_aliasIndexFailed) return false;

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = false;
            try
            {
                ok = TryBuildAliasIndexCore();
            }
            catch (Exception ex)
            {
                AddDiagnostic("TryBuildAliasIndex", ex);
                _aliasIndexDiag = "异常 " + ex.GetType().Name;   // 只写异常类型名，不带地址
                ok = false;
            }
            sw.Stop();
            _aliasIndexSeconds = sw.Elapsed.TotalSeconds;

            if (!ok)
            {
                lock (_locGate)
                {
                    _aliasFrom.Clear();
                    _aliasTo.Clear();
                    _aliasIndex.Clear();
                }
                _aliasIndexEntries = 0;
                _aliasIndexFailed = true;
                AddNote("方案A 别名表未采用（保持纯词表行为）：" + _aliasIndexDiag);
                return false;
            }

            _aliasIndexBuilt = true;
            AddNote("方案A 别名表已建立：" + _aliasIndexEntries + " 条，耗时 "
                    + _aliasIndexSeconds.ToString("F1") + "s；" + _aliasIndexDiag);
            return true;
        }

        private bool TryBuildAliasIndexCore()
        {
            _aliasIndexDiag = "";

            // ① 内容锚点字符串（别名表首行 alias_from；是内容，不是地址）
            List<long> objs = FindMonoStringObjects(ALIAS_ANCHOR, 16);
            if (objs.Count == 0) { _aliasIndexDiag = "堆中未找到锚点字符串"; return false; }

            // ② 反查底层 String[]：锚点是数组第 0 个元素 ⇒ 数组头 = r - ARRAY_DATA
            List<long> refs = _mem.ScanPointersTo(objs, 20000);
            List<long> arrs = new List<long>();
            for (int i = 0; i < refs.Count; i++)
            {
                long r = refs[i];
                long head = r - ARRAY_DATA;
                if (head <= 0x10000) continue;
                string acn = GetClassName(_mem.ReadLong(head));
                if (acn == null || acn.IndexOf("[]") < 0) continue;
                long maxLen = _mem.ReadLong(head + ARRAY_LEN);
                if (maxLen < ALIAS_MIN_PLAUSIBLE || maxLen > ALIAS_MAX_PLAUSIBLE) continue;
                if (r + 8 > head + ARRAY_DATA + maxLen * 8) continue;
                if (ReadMonoString(_mem.ReadLong(head + ARRAY_DATA)) != ALIAS_ANCHOR) continue;
                if (!arrs.Contains(head)) arrs.Add(head);
            }
            if (arrs.Count == 0) { _aliasIndexDiag = "未能反推出别名表底层数组"; return false; }

            // ③ 数组 → List<string>（_items @ +0x10，_size @ +0x18）
            long arr1 = 0, list1 = 0;
            int size1 = 0;
            for (int i = 0; i < arrs.Count && list1 == 0; i++)
            {
                List<long> r2 = _mem.ScanPointersTo(new List<long>(new long[] { arrs[i] }), 64);
                for (int k = 0; k < r2.Count; k++)
                {
                    long cand = r2[k] - LIST_ARR;
                    if (cand <= 0x10000) continue;
                    string cn = GetClassName(_mem.ReadLong(cand));
                    if (cn == null || cn.IndexOf("List") < 0) continue;
                    int sz = _mem.ReadInt(cand + LIST_SIZE);
                    if (sz < ALIAS_MIN_PLAUSIBLE || sz > ALIAS_MAX_PLAUSIBLE) continue;
                    arr1 = arrs[i]; list1 = cand; size1 = sz;
                    break;
                }
            }
            if (list1 == 0) { _aliasIndexDiag = "未能反推出 List<string>"; return false; }

            // ④ List → LL 实例：逐个试字段偏移，且**必须选中当前语言**（id == "zh_cn"）。
            //    LL 实例可能不唯一（LL.englishLang 等）⇒ 不能见 LL 就用。
            long llObj = 0, list2 = 0;
            int off1 = 0;
            List<long> r3 = _mem.ScanPointersTo(new List<long>(new long[] { list1 }), 64);
            for (int k = 0; k < r3.Count && llObj == 0; k++)
            {
                for (int off = 0x08; off <= 0x200; off += 8)
                {
                    long cand = r3[k] - off;
                    if (cand <= 0x10000) break;
                    if (GetClassName(_mem.ReadLong(cand)) != "LL") continue;
                    if (ReadLangIdAt(cand) != "zh_cn") continue;
                    long l2 = _mem.ReadLong(cand + off + 8);
                    if (l2 <= 0x10000) continue;
                    string cn2 = GetClassName(_mem.ReadLong(l2));
                    if (cn2 == null || cn2.IndexOf("List") < 0) continue;
                    if (_mem.ReadInt(l2 + LIST_SIZE) != size1) continue;   // 两表等长（AddAliases 保证）
                    llObj = cand; off1 = off; list2 = l2;
                    break;
                }
            }
            if (llObj == 0) { _aliasIndexDiag = "未找到 id==zh_cn 的 LL 实例（或字段偏移不匹配）"; return false; }

            // ⑤ 读全量两表
            long arr2 = _mem.ReadLong(list2 + LIST_ARR);
            if (arr2 <= 0x10000) { _aliasIndexDiag = "aliases2 数组指针无效"; return false; }
            long a1len = _mem.ReadLong(arr1 + ARRAY_LEN);
            long a2len = _mem.ReadLong(arr2 + ARRAY_LEN);
            if (a1len != a2len) { _aliasIndexDiag = "两表底层数组长度不等"; return false; }
            if (a1len < ALIAS_MIN_PLAUSIBLE || a1len > ALIAS_MAX_PLAUSIBLE)
            { _aliasIndexDiag = "底层数组长度超出合理区间"; return false; }

            int n = (int)a1len;
            byte[] raw1 = _mem.ReadBytes(arr1 + ARRAY_DATA, n * 8);
            byte[] raw2 = _mem.ReadBytes(arr2 + ARRAY_DATA, n * 8);
            if (raw1 == null || raw1.Length != n * 8 || raw2 == null || raw2.Length != n * 8)
            { _aliasIndexDiag = "读取别名表原始槽失败"; return false; }

            List<string> frm = new List<string>(n);
            List<string> to = new List<string>(n);
            for (int i = 0; i < n; i++)
            {
                frm.Add(ReadMonoString(BitConverter.ToInt64(raw1, i * 8)));
                to.Add(ReadMonoString(BitConverter.ToInt64(raw2, i * 8)));
            }

            // ---------- 四重内容验证（任一不过 ⇒ 整体拒绝）----------
            // ① 条数：两表等长（已校验 a1len==a2len）且落在合理区间、与 List._size 一致
            if (n != size1) { _aliasIndexDiag = "List._size 与底层数组长度不一致"; return false; }

            Dictionary<string, int> probe = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
            {
                if (frm[i] == null || frm[i].Length == 0) continue;
                if (!probe.ContainsKey(frm[i])) probe[frm[i]] = i;   // 复刻 List.IndexOf 的首次出现语义
            }

            // 【t34 · F5】形态（②）改为**全量**统计：原实现等距抽 `ALIAS_VERIFY_SAMPLE`(=200) 条、
            //   步长 ≈43，真表里 7 行非标识符内容被整批漏检（判据报 200/200 全过）。
            //   ③④ 仍按抽样（需查词表 + 跟链，成本高一个量级）。
            //   阈值保持 90% **不收紧**：那 7 行是真实数据形态，收紧会误拒真表；
            //   本项改进的价值是「把真实数字报出来」，而不是改判据口径。
            int sample = Math.Min(n, ALIAS_VERIFY_SAMPLE);
            int shapeOk = 0, shapeBad = 0, outsideLoc = 0, cjkViaChain = 0;
            for (int i = 0; i < n; i++)
            {
                if (IsAsciiId(frm[i]) && IsAsciiId(to[i])) shapeOk++;
                else shapeBad++;
            }
            for (int s = 0; s < sample; s++)
            {
                int i = (int)((long)s * n / sample);
                string k = frm[i];
                // ③ 区分度：别名键必须**不在**中文词表里（否则读到的是词表，不是别名表）
                bool inLoc;
                lock (_locGate) { inLoc = _locNames.ContainsKey(k); }
                if (!inLoc) outsideLoc++;
                // ④ 交叉：跟链到不动点后应能在中文词表里解析出中文
                string tail = AliasChainOf(k, probe, to);
                string zh;
                bool hit;
                lock (_locGate) { hit = _locNames.TryGetValue(tail, out zh) && HasCjk(zh); }
                if (hit) cjkViaChain++;
            }
            if (shapeOk * 10 < n * 9)
            {
                _aliasIndexDiag = "形态验证不过（ASCII 标识符 " + shapeOk + "/" + n
                                + "，非标识符行 " + shapeBad + "）";
                return false;
            }
            if (outsideLoc * 10 < sample * 8)
            {
                _aliasIndexDiag = "与词表区分度不足（不在词表的键 " + outsideLoc + "/" + sample + "）";
                return false;
            }
            if (cjkViaChain * 10 < sample * 6)
            {
                _aliasIndexDiag = "交叉验证不过（链尾可解析中文 " + cjkViaChain + "/" + sample + "）";
                return false;
            }

            // ⑥ 登记（跨线程共享 → 持锁整体替换）
            lock (_locGate)
            {
                _aliasFrom.Clear();
                _aliasTo.Clear();
                _aliasIndex.Clear();
                for (int i = 0; i < n; i++)
                {
                    _aliasFrom.Add(frm[i]);
                    _aliasTo.Add(to[i]);
                    string k = frm[i];
                    if (k != null && k.Length > 0 && !_aliasIndex.ContainsKey(k))
                        _aliasIndex[k] = i;
                }
                _aliasIndexEntries = _aliasIndex.Count;
            }

            _aliasIndexDiag = "底层 " + n + " 条（形态全量 " + shapeOk + "/" + n
                + "，非标识符 " + shapeBad + "；抽样 " + sample + " 条：非词表键 " + outsideLoc
                + " / 链尾含中文 " + cjkViaChain + "），偏移 aliases1@+0x"
                + off1.ToString("X") + " aliases2@+0x" + (off1 + 8).ToString("X");
            return true;
        }

        /// <summary>
        /// 纯函数版别名跟链（建表验证期使用，不依赖已登记的 <see cref="_aliasIndex"/>）。
        /// 带防环：visited 集合 + <see cref="ALIAS_MAX_HOPS"/> 上限。
        /// </summary>
        private static string AliasChainOf(string key, Dictionary<string, int> probe, IList<string> to)
        {
            if (key == null || key.Length == 0) return key;
            string cur = key;
            HashSet<string> seen = new HashSet<string>();
            seen.Add(cur);
            for (int hop = 0; hop < ALIAS_MAX_HOPS; hop++)
            {
                int idx;
                if (!probe.TryGetValue(cur, out idx)) return cur;
                if (idx < 0 || idx >= to.Count) return cur;
                string nxt = to[idx];
                if (nxt == null || nxt.Length == 0) return cur;
                if (!seen.Add(nxt)) return cur;      // 环：在进入环之前截断
                cur = nxt;
            }
            return cur;                              // 达到跳数上限：返回当前值
        }

        /// <summary>
        /// 【方案A】只跟别名链，返回链尾（无别名命中则返回 key 本身）。
        /// 线程安全；带防环（visited + 跳数上限），并对截断记一条诊断。
        /// </summary>
        public string ResolveAliasChain(string key)
        {
            if (key == null || key.Length == 0) return key;
            if (!_aliasIndexBuilt) return key;

            string notes = null;
            string result = key;
            lock (_locGate)
            {
                if (_aliasIndex.Count == 0) return key;
                string cur = key;
                HashSet<string> seen = new HashSet<string>();
                seen.Add(cur);
                for (int hop = 0; hop < ALIAS_MAX_HOPS; hop++)
                {
                    int idx;
                    if (!_aliasIndex.TryGetValue(cur, out idx)) break;
                    if (idx < 0 || idx >= _aliasTo.Count) break;
                    string nxt = _aliasTo[idx];
                    if (nxt == null || nxt.Length == 0) break;
                    if (!seen.Add(nxt))
                    {
                        notes = "别名链存在环，已在 " + hop + " 跳后截断（key 前 32 字符："
                                + TruncateForLog(key, 32) + "）";
                        break;
                    }
                    cur = nxt;
                    if (hop == ALIAS_MAX_HOPS - 1)
                        notes = "别名链超过 " + ALIAS_MAX_HOPS + " 跳上限，已截断（key 前 32 字符："
                                + TruncateForLog(key, 32) + "）";
                }
                result = cur;
            }
            if (notes != null) AddNote(notes);
            return result;
        }

        /// <summary>
        /// 【方案A】按游戏 <c>LLBase.L(key)</c> 的语义解析中文名：
        ///   **别名优先**（命中即跟链到不动点）→ 查中文词表 → 都没有返回 null
        ///   （由调用方兜底显示**原始 id**）。
        /// 与游戏的一处**有意差异**（如实登记）：游戏在链尾也查不到时会返回链尾原文；
        /// 本方法返回 null，因为调用方的兜底是「显示原始 id」——观感一致，且不引入非中文伪结果。
        /// </summary>
        public string ResolveLocalizedWithAlias(string key)
        {
            if (key == null || key.Length == 0) return null;
            string tail = ResolveAliasChain(key);
            string hit;
            lock (_locGate)
            {
                if (_locNames.TryGetValue(tail, out hit) && hit != null && hit.Length > 0) return hit;
            }
            return null;
        }

        /// <summary>
        /// 【方案A · t29 返工】导出已登记的别名表（成对、**保序**、含重复键），供落盘缓存与
        /// UI 侧实例共享。导出的是**纯字符串数据**，不含任何地址或偏移。未就绪返回 false。
        /// </summary>
        public bool TryExportAliasTable(out List<string> from, out List<string> to)
        {
            from = null; to = null;
            if (!_aliasIndexBuilt) return false;
            lock (_locGate)
            {
                if (_aliasFrom.Count == 0 || _aliasFrom.Count != _aliasTo.Count) return false;
                from = new List<string>(_aliasFrom);
                to = new List<string>(_aliasTo);
            }
            return true;
        }

        /// <summary>
        /// 【方案A · t29 返工】从缓存导入别名表（缓存命中路径用；也可用于把现场建好的表
        /// 共享给 UI 侧另一个实例）。做**内容级**校验：
        ///   ① 两列等长且条数 ∈ [ALIAS_MIN_PLAUSIBLE, ALIAS_MAX_PLAUSIBLE]；
        ///   ② 抽样 ALIAS_VERIFY_SAMPLE 条，键与值都必须是 ASCII 标识符（比例 ≥ 90%）。
        /// 若当前**恰好**已有中文词表（刷新路径），额外做与现场建表同样的两项：
        ///   ③ 别名键不在词表（≥80%）；④ 链尾能在词表解析出中文（≥60%）。
        /// 缓存路径下词表为空时 ③④ 跳过，并在 note 里**如实标注**（不当成已验证）。
        /// 校验不过 ⇒ 拒绝导入并置 <see cref="_aliasIndexFailed"/>（下次启动会强制重试现场读取）。
        /// </summary>
        public bool TryImportAliasTable(IList<string> from, IList<string> to, out string note)
        {
            note = "";
            if (_aliasIndexBuilt) { note = "别名表已就绪，忽略导入"; return true; }
            if (from == null || to == null) { note = "导入数据为空"; return false; }
            int n = from.Count;
            if (n != to.Count)
            {
                _aliasIndexFailed = true;
                note = "两列长度不等（" + n + " vs " + to.Count + "），已拒绝（下次启动强制重试）";
                return false;
            }
            if (n < ALIAS_MIN_PLAUSIBLE || n > ALIAS_MAX_PLAUSIBLE)
            {
                _aliasIndexFailed = true;
                note = "条数超出合理区间（" + n + "），已拒绝（下次启动强制重试）";
                return false;
            }

            Dictionary<string, int> probe = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
            {
                string k = from[i];
                if (k == null || k.Length == 0) continue;
                if (!probe.ContainsKey(k)) probe[k] = i;
            }

            bool haveLoc;
            lock (_locGate) { haveLoc = _locNames.Count > 0; }

            // 【t34 · F5】形态检查改为**全量**：原实现等距抽 `ALIAS_VERIFY_SAMPLE`(=200) 条、
            //   步长 ≈43，实测真表里 7 行非标识符内容被整批漏检（判据报 200/200 全过）。
            //   全量统计的成本只是纯内存字符扫描（8709×2 次 `IsAsciiId`）。
            //   ③④ 仍按抽样（它们要查词表 + 跟链，成本高一个量级）。
            //   注意：阈值保持 90% **不收紧** —— 那 7 行是真实数据形态（如含 `:` 的混合键），
            //   收紧会误拒真表；本项改进的价值是「把真实数字报出来」，而不是改判据口径。
            int sample = Math.Min(n, ALIAS_VERIFY_SAMPLE);
            int shapeOk = 0, shapeBad = 0, outsideLoc = 0, cjkViaChain = 0;
            for (int i = 0; i < n; i++)
            {
                if (IsAsciiId(from[i]) && IsAsciiId(to[i])) shapeOk++;
                else shapeBad++;
            }
            for (int s = 0; s < sample; s++)
            {
                int i = (int)((long)s * n / sample);
                string k = from[i];
                if (!haveLoc) continue;
                bool inLoc;
                lock (_locGate) { inLoc = _locNames.ContainsKey(k); }
                if (!inLoc) outsideLoc++;
                string tail = AliasChainOf(k, probe, to);
                string zh;
                bool hit;
                lock (_locGate) { hit = _locNames.TryGetValue(tail, out zh) && HasCjk(zh); }
                if (hit) cjkViaChain++;
            }

            if (shapeOk * 10 < n * 9)
            {
                _aliasIndexFailed = true;
                note = "形态校验不过（ASCII 标识符 " + shapeOk + "/" + n
                     + "，非标识符行 " + shapeBad + "），已拒绝（下次启动强制重试）";
                return false;
            }
            if (haveLoc && outsideLoc * 10 < sample * 8)
            {
                _aliasIndexFailed = true;
                note = "与词表区分度不足（不在词表的键 " + outsideLoc + "/" + sample + "），已拒绝";
                return false;
            }
            if (haveLoc && cjkViaChain * 10 < sample * 6)
            {
                _aliasIndexFailed = true;
                note = "交叉校验不过（链尾可解析中文 " + cjkViaChain + "/" + sample + "），已拒绝";
                return false;
            }

            lock (_locGate)
            {
                _aliasFrom.Clear();
                _aliasTo.Clear();
                _aliasIndex.Clear();
                for (int i = 0; i < n; i++)
                {
                    _aliasFrom.Add(from[i]);
                    _aliasTo.Add(to[i]);
                    string k = from[i];
                    if (k != null && k.Length > 0 && !_aliasIndex.ContainsKey(k))
                        _aliasIndex[k] = i;
                }
                _aliasIndexEntries = _aliasIndex.Count;
            }
            _aliasIndexBuilt = true;
            note = "已从缓存导入别名表：" + n + " 行 / " + _aliasIndexEntries + " 键（形态全量 "
                 + shapeOk + "/" + n + "，非标识符行 " + shapeBad
                 + (haveLoc ? "；非词表键 " + outsideLoc + "/" + sample + "，链尾含中文 " + cjkViaChain + "/" + sample
                            : "；词表为空 ⇒ ③④ 跳过（未验证）")
                 + "）";
            return true;
        }

        /// <summary>别名表诊断文本（供日志与报告；**只含偏移与计数，绝不含地址**）。</summary>
        public string AliasTableDiag { get { return _aliasIndexDiag; } }

        /// <summary>
        /// 【t29 返工 · R1④ 补验】对**已登记的别名表**（含缓存导入的那种）在**词表就绪时**
        /// 补做 ③「别名键不在词表」与 ④「链尾能在词表解析出中文」两项抽查 ——
        /// 导入路径在词表为空时如实跳过了这两项，这里是有机会时的补验。
        /// 返回诊断文本；**表未就绪或词表为空时返回 null**（调用方据此判断"这次没跑成"，
        /// 并保留到下次机会，不要把"跳过"当成"通过"）。
        /// 【t34 · T3 订正】抽查**不过即撤销**：原实现「只把结论追加进
        /// <see cref="AliasTableDiag"/>、不撤销表」，属评审 t33 finding T3 指出的
        /// 「读了不用于拒绝」的形式化校验。现在补验结果**参与采用决策** ——
        /// 不过则清空表 + 置 `_aliasIndexFailed`，消费端立即退回纯词表行为，
        /// 下次启动强制现场重读（见方法内 `if (!pass)` 分支）。
        /// </summary>
        public string VerifyAliasAgainstLocTable()
        {
            if (!_aliasIndexBuilt) return null;
            List<string> frm;
            List<string> to;
            lock (_locGate)
            {
                if (_locNames.Count == 0) return null;      // 词表未就绪 ⇒ 无法补验
                if (_aliasFrom.Count == 0 || _aliasFrom.Count != _aliasTo.Count) return null;
                frm = new List<string>(_aliasFrom);
                to = new List<string>(_aliasTo);
            }

            Dictionary<string, int> probe = new Dictionary<string, int>();
            for (int i = 0; i < frm.Count; i++)
            {
                string k = frm[i];
                if (k == null || k.Length == 0) continue;
                if (!probe.ContainsKey(k)) probe[k] = i;
            }

            int n = frm.Count;
            int sample = Math.Min(n, ALIAS_VERIFY_SAMPLE);
            if (sample <= 0) return null;
            int outsideLoc = 0, cjkViaChain = 0;
            for (int s = 0; s < sample; s++)
            {
                int i = (int)((long)s * n / sample);
                string k = frm[i];
                bool inLoc;
                lock (_locGate) { inLoc = _locNames.ContainsKey(k); }
                if (!inLoc) outsideLoc++;
                string tail = AliasChainOf(k, probe, to);
                string zh;
                bool hit;
                lock (_locGate) { hit = _locNames.TryGetValue(tail, out zh) && HasCjk(zh); }
                if (hit) cjkViaChain++;
            }

            bool pass = (outsideLoc * 10 >= sample * 8) && (cjkViaChain * 10 >= sample * 6);
            if (!pass)
            {
                // 【t34 · T3 修复】补验未通过 ⇒ **真的撤销**已登记的表并置失败态。
                //   撤销后：① `IsAliasTableReady` 立即变 false ⇒ 消费端（AddByAliasChain 等）
                //   静默退回纯词表行为；② `_aliasIndexFailed = true` ⇒ 下次启动 `EnsureAliasIndex`
                //   直接返回失败态，强制走「现场读取 + 四重验证」，绝不把可疑表继续当数据用。
                //   注意：会话名表（`_itemNames`）里**已并入**的别名单条目不受影响，43 条显示不回退。
                lock (_locGate)
                {
                    _aliasFrom.Clear();
                    _aliasTo.Clear();
                    _aliasIndex.Clear();
                }
                _aliasIndexEntries = 0;
                _aliasIndexBuilt = false;
                _aliasIndexFailed = true;
            }
            string msg = "别名表 vs 词表补验：" + (pass ? "通过" : "未通过 ⇒ 已撤销该表并置失败态（下次启动强制现场重读）")
                       + "（非词表键 " + outsideLoc + "/" + sample
                       + "，链尾含中文 " + cjkViaChain + "/" + sample + "）";
            _aliasIndexDiag = _aliasIndexDiag + "；" + msg;
            return msg;
        }

        /// <summary>在 LL 实例的字段里找语言 id（形如 "zh_cn" / "en" …）；找不到返回 null。</summary>
        private string ReadLangIdAt(long llObj)
        {
            for (int off = 0x08; off <= 0x200; off += 8)
            {
                long p = _mem.ReadLong(llObj + off);
                if (p <= 0x10000) continue;
                string v;
                if (!ReadStringObj(p, out v)) continue;
                if (v == null || v.Length == 0) continue;
                for (int i = 0; i < KnownLangIds.Length; i++)
                    if (v == KnownLangIds[i]) return v;
            }
            return null;
        }

        /// <summary>日志用截断（不输出整串，避免日志过长）。</summary>
        private static string TruncateForLog(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max);
        }

        /// <summary>
        /// 从一个 Entry 的 value 槽出发，把它所在的 Dictionary Entry[] 整表读出
        /// （只登记 value 含中文的条目）。表中间允许空洞，故优先用数组对象的真实长度遍历。
        /// </summary>
        private Dictionary<string, string> ReadLocalizationTable(long anchor)
        {
            Dictionary<string, string> table = new Dictionary<string, string>();

            // 探出连续有效区（用于定位 Entry[] 数组对象）
            long first = anchor;
            while (first - ENTRY_STEP > anchor - ENTRY_MAX_SPAN)
            {
                string k, v;
                if (!IsEntryValueSlot(first - ENTRY_STEP, out k, out v)) break;
                first -= ENTRY_STEP;
            }
            long last = anchor;
            while (last + ENTRY_STEP < anchor + ENTRY_MAX_SPAN)
            {
                string k, v;
                if (!IsEntryValueSlot(last + ENTRY_STEP, out k, out v)) break;
                last += ENTRY_STEP;
            }

            long arr = FindEntryArrayFor(first);
            if (arr != 0)
            {
                long alen = _mem.ReadLong(arr + ARRAY_LEN);
                if (alen > 0 && alen <= ENTRY_MAX_LEN)
                {
                    for (long i = 0; i < alen; i++)
                    {
                        long p = arr + ARRAY_DATA + i * ENTRY_STEP + ENTRY_VALUE_OFF;
                        string k, v;
                        if (!IsEntryValueSlot(p, out k, out v)) continue;
                        if (!HasCjk(v)) continue;
                        string clean = StripMarkup(v);
                        if (clean.Length == 0) continue;
                        if (!table.ContainsKey(k)) table[k] = clean;
                    }
                    return table;
                }
            }

            // 兜底：定位不到数组对象时按连续区遍历（可能有空洞而被截断）
            for (long p = first; p <= last; p += ENTRY_STEP)
            {
                string k, v;
                if (!IsEntryValueSlot(p, out k, out v)) continue;
                if (!HasCjk(v)) continue;
                string clean = StripMarkup(v);
                if (clean.Length == 0) continue;
                if (!table.ContainsKey(k)) table[k] = clean;
            }
            return table;
        }

        /// <summary>
        /// 校验 p 是一个 Dictionary Entry 的 value 槽：
        ///   p-8 必须是指向 System.String 的指针，且内容像本地化 ID（纯 ASCII 标识符）；
        ///   p   必须是指向 System.String 的指针，且内容非空。
        /// </summary>
        private bool IsEntryValueSlot(long p, out string key, out string value)
        {
            key = null;
            value = null;
            long keyObj = _mem.ReadLong(p - ENTRY_KEY_OFF);
            string k;
            if (!ReadStringObj(keyObj, out k)) return false;
            if (!IsAsciiId(k)) return false;
            long valObj = _mem.ReadLong(p);
            string v;
            if (!ReadStringObj(valObj, out v)) return false;
            if (v.Length == 0) return false;
            key = k;
            value = v;
            return true;
        }

        /// <summary>
        /// 从连续区起点向上找到所属的 Entry[] 数组对象。
        /// 元素 i 的 value 槽 = arr + 0x20(元素区) + 0x10(Entry 内 value 偏移) + 24*i，
        /// 即 (firstSlot - arr - 0x30) 必是 24 的整数倍 —— 用该同余条件先过滤 2/3 的候选。
        /// 不用 GetClassName（避免污染类名缓存），直接读 MonoClass 名判断。
        /// </summary>
        private long FindEntryArrayFor(long firstSlot)
        {
            long start = firstSlot - ARRAY_DATA - ENTRY_VALUE_OFF;   // = firstSlot - 0x30
            long limit = firstSlot - ARRAY_SCAN_BACK;
            for (long cand = start; cand > limit; cand -= 8)
            {
                long off = firstSlot - ARRAY_DATA - ENTRY_VALUE_OFF - cand;
                if (off < 0) continue;
                if ((off % ENTRY_STEP) != 0) continue;

                long len = _mem.ReadLong(cand + ARRAY_LEN);
                if (len <= 0 || len > ENTRY_MAX_LEN) continue;
                if (firstSlot < cand + ARRAY_DATA) continue;
                if (firstSlot >= cand + ARRAY_DATA + len * ENTRY_STEP) continue;

                long vt = _mem.ReadLong(cand);
                if (!MaybeHeap(vt)) continue;
                if (!IsEntryArrayClass(vt)) continue;
                return cand;
            }
            return 0;
        }

        private bool IsEntryArrayClass(long vtable)
        {
            // 【t18 评审 F1】补上 GC 标记位掩码：本方法是全仓**最后一处**直接解引用对象首字
            // 取 klass 的地方（其余 29 处都经已掩码的 GetClassName / GetClassNameUncached）。
            // 不掩码**不会造成误判**（fail-safe：首字被标记时这里判 false，只让字典 Entry[] 的
            // 快路径失效并回退线性扫描），但补上更一致，也避免将来有人把它当成"掩码必须加"的反例。
            vtable &= GC_MARK_BIT_MASK;
            long klass = _mem.ReadLong(vtable + MONO_VTABLE_KLASS);
            if (klass <= 0x10000 || klass >= 0x7FFFFFFF0000L) return false;
            long namep = _mem.ReadLong(klass + MONO_CLASS_NAME);
            if (namep <= 0x10000 || namep >= 0x7FFFFFFF0000L) return false;
            string n = _mem.ReadAscii(namep, 64);
            if (n == null || n.Length == 0) return false;
            return n.IndexOf("Entry") >= 0;
        }

        /// <summary>读 MonoString 内容（支持任意 Unicode，含中文）；非字符串对象返回 false。</summary>
        private bool ReadMonoStringAny(long strObj, out string s)
        {
            s = null;
            if (strObj == 0 || !MaybeHeap(strObj)) return false;
            int len = _mem.ReadInt(strObj + MONO_STRING_LENGTH);
            if (len <= 0 || len > 4096) return false;
            byte[] b = _mem.ReadBytes(strObj + MONO_STRING_CHARS, len * 2);
            if (b == null || b.Length != len * 2) return false;
            char[] cs = new char[len];
            for (int i = 0; i < len; i++)
            {
                char c = (char)(b[i * 2] | (b[i * 2 + 1] << 8));
                if (c == 0) return false;
                cs[i] = c;
            }
            s = new string(cs);
            return true;
        }

        /// <summary>校验 obj 是 System.String 实例并读出内容（走类名缓存，够快）。</summary>
        private bool ReadStringObj(long obj, out string s)
        {
            s = null;
            if (obj == 0 || !MaybeHeap(obj)) return false;
            long vt = _mem.ReadLong(obj);
            if (!MaybeHeap(vt)) return false;
            if (GetClassName(vt) != "String") return false;
            return ReadMonoStringAny(obj, out s);
        }

        /// <summary>是否像本地化 ID（纯 ASCII 标识符）</summary>
        private static bool IsAsciiId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 200) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-' || c == ':' || c == '/';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>是否含中文字符（用于把中文表与英文表区分开）</summary>
        private static bool HasCjk(string s)
        {
            if (s == null) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 0x4E00 && c <= 0x9FFF) return true;   // CJK 统一汉字
                if (c >= 0x3400 && c <= 0x4DBF) return true;   // 扩展 A
                if (c >= 0xF900 && c <= 0xFAFF) return true;   // 兼容汉字
            }
            return false;
        }

        /// <summary>剥掉 LazyBear 富文本标记（如 &lt;nobr&gt;…&lt;/nobr&gt;）与零宽空格，得到干净显示名。</summary>
        private static string StripMarkup(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (inTag) continue;
                if (c == '\u200B') continue;   // 零宽空格
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
