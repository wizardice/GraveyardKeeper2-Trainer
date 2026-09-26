using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace GK2Trainer
{
    /// <summary>
    /// 物品中文名表的「会话级磁盘缓存」（C# 5 语法，csc.exe 可直接编译）。
    ///
    /// 背景：整张游戏本地化词表（实测 9346 条）在**一次游戏主程序启动内不会变动**、
    /// 且**与进程内存地址无关**；每次「刷新」重建它要 4.0~4.6 s。这里把它精简成
    /// 「物品名表」（itemId → 中文名，实测 552 条 / 约 14 KB）扫描一次后落到系统
    /// 临时目录，供后续刷新直接命中。
    ///
    /// 文件内容**仅限**三类纯数据：
    ///   ① itemId → 中文名（两列文本行）
    ///   ② 游戏版本指纹（游戏主程序文件名 + 字节数 + 最后写入时间）与语言
    ///   ③ 生成时间
    /// 【红线】不写任何内存地址 / 虚方法表指针 / 堆区间 / 对象地址 —— 它们跨游戏启动必然失效，
    ///        复用会导致写到错误对象（本项目有历史事故）。
    ///
    /// 目录：%TEMP%\GK2Trainer\&lt;会话标识&gt;\GK2LocItems.cache
    ///   会话标识 = s&lt;本工具 PID&gt;_&lt;UTC ticks&gt;_&lt;8 位随机十六进制&gt;
    ///   —— 多实例各写自己的目录，互不覆盖。
    /// </summary>
    public static class NameCacheStore
    {
        public const string RootDirName = "GK2Trainer";
        public const string CacheFileName = "GK2LocItems.cache";
        /// <summary>
        /// 缓存格式标识。【2026-09-26 由 GK2LOC-CACHE-1 连升到 -5，共四次语义变更】
        ///   · -1 → -2：把星级变体的**备用键**并入名表。旧版只存「词表 ∩ 物品 id」的**精确匹配**
        ///     结果（552 条），而游戏中文词表里几乎没有 ":N" 键（9387 条中仅 9 条含冒号），
        ///     备用键（如 `cook_vegetable_salad`，它不是任何 ItemDef 的 id）才是词表里真实存在的那条。
        ///   · -2 → -3：候选键由「基名」扩展到「基名 + 通用名 + 通用名_1_1」
        ///     （见 TrainerForm.IntersectNames 与 coverage_report.txt）。
        ///   · -3 → -4【2026-09-26 · 方案A】：名表内容新增**运行时别名表**（aliases1/aliases2）
        ///     跟链产生的条目（43 条真实缺口，含 surgeon_mistake_* 6 条人体部件）。
        ///   · -4 → -5【t29 返工】：名表缓存头部**曾**新增 `alias=<条数>` 字段，并在读回时要求
        ///     `alias ≥ AliasMinEntries` —— 缺字段（-4 旧格式）或条数异常一律拒绝 ⇒ 强制重建。
        ///     目的是消除 t29 R1 指出的「一次别名表建表失败被同版本缓存永久固化且无自愈路径」。
        ///     【2026-09-26 · P2 取消】该字段与判据**已整体删除**，连同它唯一服务的
        ///     「与 `.alias` 头的 `count` 严格相等」跨文件交叉校验。本条目只作历史记录：
        ///     旧缓存文件里残留的 `alias=` 行会被读端**无害忽略**（头部解析只认已知键）。
        ///     别名表**内容**另有独立缓存（见 AliasMagic / GK2ItemAlias.cache）。
        ///
        /// 【2026-09-26 · P2】**本次删字段不升版号**（仍为 -5），理由：
        ///   删掉 `alias=` 没有改变这份缓存的**语义** —— 名表内容、行格式、其余全部字段
        ///   （magic/kind/game/lang/created/count）与内容级校验逐字不变，删掉的只是一个
        ///   **只服务于跨文件交叉校验**的字段。两个方向都不产生错误数据：
        ///     · 新端读旧文件 ⇒ 多一行被忽略的字段，命中结果与旧端逐字一致；
        ///     · 旧端读新文件 ⇒ `alias` 缺失（-1 &lt; AliasMinEntries）⇒ 拒绝并重建一次，
        ///       属**安全降级**（慢一次，不会用错数据）。
        ///   升版号反而白白作废全部现有缓存（多付一次约 8 s 现场重建），换不到任何正确性。
        /// 若沿用同一标识，跨会话持久缓存会继续被命中，修复在新会话里永远不生效；
        /// 升版后旧缓存因 magic 不符被安全拒绝 → 自动重建。
        /// （t5/t15 评审 F4：原注释只写「升版 1 → 2」与当时的常量不一致；
        /// 【t34 · T4 订正】本行不再复述具体版本号 —— 以「与 `Magic` 常量保持同步」为准，
        /// 避免每次升版又在这里留一句残句。）
        /// </summary>
        public const string Magic = "GK2LOC-CACHE-5";
        public const int MaxFileBytes = 400 * 1024;   // 缓存体量上限（验收：≤ 400 KB）
        public const int MinEntries = 8;              // 少于该条数视为「词表为空」

        private static readonly object Gate = new object();
        private static string _sessionTag = "";
        private static string _sessionDir = "";
        private static bool _exitHookReady = false;

        // ------------------------------------------------------------------
        // 路径与会话
        // ------------------------------------------------------------------

        /// <summary>本工具专属临时根目录：%TEMP%\GK2Trainer（不在其中放任何其它东西）。</summary>
        public static string TempRoot()
        {
            return Path.Combine(Path.GetTempPath(), RootDirName);
        }

        /// <summary>本次进程的会话标识（惰性生成，只生成一次）。</summary>
        public static string SessionTag()
        {
            lock (Gate)
            {
                if (_sessionTag.Length == 0)
                {
                    int pid;
                    try { pid = Process.GetCurrentProcess().Id; }
                    catch { pid = 0; }
                    string rnd = Guid.NewGuid().ToString("N");
                    if (rnd.Length > 8) rnd = rnd.Substring(0, 8);
                    _sessionTag = "s" + pid.ToString(CultureInfo.InvariantCulture) + "_"
                        + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "_" + rnd;
                }
                return _sessionTag;
            }
        }

        /// <summary>
        /// 本次会话的缓存目录；会自动创建。目录不可用时返回 null（调用方安全跳过）。
        /// </summary>
        public static string SessionDir()
        {
            lock (Gate)
            {
                if (_sessionDir.Length > 0) return _sessionDir;
                try
                {
                    string dir = Path.Combine(TempRoot(), SessionTag());
                    Directory.CreateDirectory(dir);
                    if (!Directory.Exists(dir)) return null;
                    _sessionDir = dir;
                    return _sessionDir;
                }
                catch { return null; }
            }
        }

        /// <summary>本次会话的缓存文件完整路径（目录不可用时为 null）。</summary>
        public static string CacheFilePath()
        {
            string dir = SessionDir();
            if (dir == null) return null;
            return Path.Combine(dir, CacheFileName);
        }

        /// <summary>非本次会话的缓存文件路径（用于「上一轮自己留下的缓存」探测，不创建目录）。</summary>
        private static string ProbeExistingCacheFile()
        {
            string dir = SessionDir();
            if (dir == null) return null;
            string p = Path.Combine(dir, CacheFileName);
            return File.Exists(p) ? p : null;
        }

        // ------------------------------------------------------------------
        // 跨会话持久缓存（t5 · P1-3）
        //
        // 为什么持久化：物品名表是**进程无关的纯数据**（itemId → 中文名 + 游戏指纹），
        //   不含任何内存地址 —— 硬约束②「地址缓存绝不落盘」依旧成立。
        //   原实现只写本次会话的 %TEMP% 目录且退出即清理，于是每次启动都要重付
        //   7.6~10.2 s 的整表提取（预热）；持久副本让第 2 次及以后启动毫秒级命中。
        //
        // 失效与安全：文件头写有 游戏指纹（exe 名 + 字节数 + 最后写入时间）+ 语言 + 创建时间，
        //   任一不符即安全失败并自动重建；文件损坏/截断由既有校验（magic/kind/count 一致性）拒绝。
        // ------------------------------------------------------------------

        /// <summary>持久缓存根目录：%LOCALAPPDATA%\GK2Trainer\ItemNames（不可用时返回 null）。</summary>
        public static string PersistentRoot()
        {
            try
            {
                string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (b == null || b.Length == 0) return null;
                return Path.Combine(Path.Combine(b, RootDirName), "ItemNames");
            }
            catch { return null; }
        }

        /// <summary>持久缓存文件路径（指纹安全化后作文件名；目录不可用时 null，永不抛异常）。</summary>
        public static string PersistentCacheFile(string fingerprint)
        {
            if (fingerprint == null || fingerprint.Length == 0) return null;
            string root = PersistentRoot();
            if (root == null) return null;
            try
            {
                Directory.CreateDirectory(root);
                if (!Directory.Exists(root)) return null;
                StringBuilder safe = new StringBuilder(96);
                for (int i = 0; i < fingerprint.Length && safe.Length < 80; i++)
                {
                    char c = fingerprint[i];
                    bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                        || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                    safe.Append(ok ? c : '_');
                }
                if (safe.Length == 0) return null;
                return Path.Combine(root, safe.ToString() + ".cache");
            }
            catch { return null; }
        }

        /// <summary>清除全部持久缓存（游戏更新后排障 / 手动重建用）。返回删除的文件数。</summary>
        public static int CleanupPersistent()
        {
            int n = 0;
            try
            {
                string root = PersistentRoot();
                if (root == null || !Directory.Exists(root)) return 0;
                string[] files = Directory.GetFiles(root, "*.cache");
                for (int i = 0; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); n++; }
                    catch { }
                }
            }
            catch { }
            return n;
        }

        /// <summary>持久缓存文件数上限（超出后按最后写入时间淘汰最旧的）。</summary>
        private const int MaxPersistentFiles = 8;

        private static void PrunePersistentCache()
        {
            try
            {
                string root = PersistentRoot();
                if (root == null) return;
                // 【t29 返工】别名表持久副本（*.alias）与名表持久副本（*.cache）一起纳入淘汰，
                //   避免别名表缓存无上限堆积（属性缓存 *.attrs 的淘汰是既有行为，未改动）。
                List<string> all = new List<string>();
                all.AddRange(Directory.GetFiles(root, "*.cache"));
                all.AddRange(Directory.GetFiles(root, "*.alias"));
                string[] files = all.ToArray();
                if (files.Length <= MaxPersistentFiles) return;
                Array.Sort(files, delegate(string a, string b)
                {
                    return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                });
                for (int i = 0; i < files.Length - MaxPersistentFiles; i++)
                {
                    try { File.Delete(files[i]); }
                    catch { }
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // 清理
        // ------------------------------------------------------------------

        /// <summary>
        /// 注册「异常退出兜底」清理：ProcessExit 在正常退出与未处理异常退出时都会触发。
        /// 重复调用无效。永不抛异常。
        /// </summary>
        public static void HookProcessExitCleanup()
        {
            lock (Gate)
            {
                if (_exitHookReady) return;
                _exitHookReady = true;
            }
            try
            {
                AppDomain.CurrentDomain.ProcessExit += delegate(object sender, EventArgs e)
                {
                    CleanupCurrentSession();
                };
            }
            catch { }
        }

        /// <summary>删除本次会话的缓存目录（只删自己那一个）。永不抛异常。</summary>
        public static void CleanupCurrentSession()
        {
            try
            {
                string dir = _sessionDir;
                if (dir == null || dir.Length == 0) return;
                if (!IsSafeChild(TempRoot(), dir)) return;
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
                _sessionDir = "";
            }
            catch { }
        }

        /// <summary>
        /// 启动时清理本工具专属目录下的**孤儿残留**（上次异常退出留下的目录）。
        /// 只处理「目录名符合本工具会话格式」且「该会话进程已不存在」的子目录；
        /// 非本工具命名格式、其它活动实例、以及 %TEMP% 本身一律不碰。返回删除数。
        /// </summary>
        public static int CleanupOrphans()
        {
            int removed = 0;
            try
            {
                string root = TempRoot();
                if (!Directory.Exists(root)) return 0;
                string[] dirs = Directory.GetDirectories(root);
                string mine = SessionTag();
                for (int i = 0; i < dirs.Length; i++)
                {
                    try
                    {
                        string name = Path.GetFileName(dirs[i]);
                        if (name == mine) continue;
                        int pid = ParseSessionPid(name);
                        if (pid <= 0) continue;               // 不是本工具创建的目录 → 绝不碰
                        if (IsProcessAlive(pid)) continue;    // 同机另一个实例仍在运行 → 不碰
                        if (!IsSafeChild(root, dirs[i])) continue;
                        Directory.Delete(dirs[i], true);
                        removed++;
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        /// <summary>是否是本工具会话目录名（s&lt;pid&gt;_&lt;ticks&gt;_&lt;8hex&gt;），匹配则返回其中的 pid，否则 -1。</summary>
        private static int ParseSessionPid(string tag)
        {
            if (tag == null || tag.Length < 12 || tag[0] != 's') return -1;
            int i = 1;
            long pid = 0;
            int d1 = 0;
            while (i < tag.Length && tag[i] >= '0' && tag[i] <= '9')
            {
                pid = pid * 10 + (tag[i] - '0');
                i++; d1++;
                if (d1 > 10) return -1;
            }
            if (d1 == 0 || pid <= 0 || pid > 0x7FFFFFFF) return -1;
            if (i >= tag.Length || tag[i] != '_') return -1;
            i++;
            int d2 = 0;
            while (i < tag.Length && tag[i] >= '0' && tag[i] <= '9') { i++; d2++; }
            if (d2 == 0) return -1;
            if (i >= tag.Length || tag[i] != '_') return -1;
            i++;
            int hex = 0;
            while (i < tag.Length)
            {
                char c = tag[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return -1;
                i++; hex++;
            }
            if (hex != 8) return -1;
            return (int)pid;
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                return p != null;
            }
            catch { return false; }
        }

        /// <summary>严格确认 child 位于 root 之下（规范化后前缀比较），防误删。</summary>
        private static bool IsSafeChild(string root, string child)
        {
            try
            {
                if (root == null || child == null) return false;
                char sep = Path.DirectorySeparatorChar;
                string r = Path.GetFullPath(root).TrimEnd(sep, Path.AltDirectorySeparatorChar);
                string c = Path.GetFullPath(child).TrimEnd(sep, Path.AltDirectorySeparatorChar);
                if (r.Length == 0 || c.Length <= r.Length + 1) return false;
                if (!c.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return false;
                if (c[r.Length] != sep && c[r.Length] != Path.AltDirectorySeparatorChar) return false;
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // 指纹
        // ------------------------------------------------------------------

        /// <summary>
        /// 游戏版本指纹 = 游戏主程序文件名 + 字节数 + 最后写入时间（UTC ticks）。
        /// 只取决于游戏安装文件，与进程、存档、进程内相对位置都无关；
        /// 游戏更新（exe 变化）后指纹自动失效，缓存不再命中。
        /// 取不到（权限不足等）时返回 null —— 调用方此时既不写也不读缓存。
        /// </summary>
        public static string GameFingerprint(int pid)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                string path = p.MainModule.FileName;
                if (path == null || path.Length == 0) return null;
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return null;
                return Path.GetFileName(path) + "|"
                    + fi.Length.ToString(CultureInfo.InvariantCulture) + "|"
                    + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------
        // 写入 / 读取
        // ------------------------------------------------------------------

        /// <summary>
        /// 写缓存。条目 = itemId → 中文名（调用方已把它精简为物品键）。
        /// 体量超过 MaxFileBytes、目录不可写、指纹缺失时**不写**并返回 false（安全跳过）。
        /// 永不抛异常。
        /// 【2026-09-26 · P2】原第 3 个形参 <c>aliasEntries</c>（写出名表缓存头的 `alias=`）已删除：
        ///   该字段的**唯一**用途是与 `.alias` 头的 `count` 做跨文件严格相等校验；判据取消后
        ///   字段不再有意义 ⇒ 字段与形参一起删除（不保留成"写了不用"）。名表缓存的语义未变
        ///   （内容、行格式、其余字段、全部内容级校验均逐字不变）⇒ **不升 Magic**，理由见类头注释。
        /// </summary>
        public static bool TryWrite(string fingerprint, IDictionary<string, string> names, out string note)
        {
            note = "";
            try
            {
                if (fingerprint == null || fingerprint.Length == 0)
                {
                    note = "无游戏版本指纹，跳过写缓存";
                    return false;
                }
                if (names == null || names.Count == 0)
                {
                    note = "物品名表为空，跳过写缓存";
                    return false;
                }
                string path = CacheFilePath();
                if (path == null)
                {
                    note = "临时缓存目录不可用，跳过写缓存";
                    return false;
                }

                List<string> keys = new List<string>(names.Keys);
                keys.Sort(StringComparer.Ordinal);

                StringBuilder body = new StringBuilder(64 * 1024);
                int written = 0;
                long bodyBytes = 0;
                for (int i = 0; i < keys.Count; i++)
                {
                    string id = keys[i];
                    if (!IsCacheableId(id)) continue;
                    string zh = CleanInline(names[id]);
                    if (zh.Length == 0) continue;
                    string line = id + "\t" + zh + "\n";
                    int n = Encoding.UTF8.GetByteCount(line);
                    if (bodyBytes + n > MaxFileBytes - 512) break;   // 体量守卫
                    body.Append(line);
                    bodyBytes += n;
                    written++;
                }
                if (written < MinEntries)
                {
                    note = "可用条目过少（" + written + " 条），跳过写缓存";
                    return false;
                }

                StringBuilder head = new StringBuilder(256);
                head.Append("# ").Append(Magic).Append('\n');
                head.Append("magic=").Append(Magic).Append('\n');
                head.Append("kind=item\n");
                head.Append("game=").Append(fingerprint).Append('\n');
                head.Append("lang=zh\n");
                head.Append("created=")
                    .Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
                    .Append('\n');
                head.Append("count=").Append(written.ToString(CultureInfo.InvariantCulture)).Append('\n');
                // 【2026-09-26 · P2 删除】原在此写入 `alias=<别名表行数>`，供读端与 `.alias` 头的
                //   `count` 做跨文件**严格相等**校验（F4 的防线）。该判据已被取消，字段一并删除
                //   （不保留成"读了不用"）。取消的代价是：「正文被截短 + 头部 count 同步改小」的
                //   **自洽残缺表**不再能被检出 —— 这是**已知边界，不是疏漏**，
                //   详见 TryReadAlias 上方注释与 P2 报告。
                head.Append("--\n");

                string content = head.ToString() + body.ToString();
                long total = Encoding.UTF8.GetByteCount(content);
                if (total > MaxFileBytes)
                {
                    note = "缓存体量 " + total + " 字节超过上限 " + MaxFileBytes + "，跳过写缓存";
                    return false;
                }

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                // 【t5 P1-3】再写一份跨会话持久副本（同一内容；失败不影响会话缓存，静默降级）
                string pnote = "";
                try
                {
                    string ppath = PersistentCacheFile(fingerprint);
                    if (ppath != null)
                    {
                        string ptmp = ppath + ".tmp";
                        File.WriteAllText(ptmp, content, new UTF8Encoding(false));
                        if (File.Exists(ppath)) File.Delete(ppath);
                        File.Move(ptmp, ppath);
                        PrunePersistentCache();
                        pnote = "；并已持久化";
                    }
                }
                catch { pnote = ""; }

                note = "已写缓存 " + written + " 条 / " + total + " 字节：" + path + pnote;
                return true;
            }
            catch (Exception ex)
            {
                note = "写缓存失败（已忽略）：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 读缓存。以下任一情况都返回 false 并给出可读原因，由调用方回退到「内存直读」：
        /// 文件不存在 / 标识不符（损坏）/ 指纹不符（游戏已更新）/ 语言不符 / 条目为空 / 声明与实际条数不符。
        /// 永不抛异常。
        /// 【2026-09-26 · P2】原 4 参重载（额外 `out` 导出名表缓存头的 `alias=`，供调用方与
        ///   `.alias` 头的 `count` 做跨文件交叉校验）已删除：`alias=` 字段本身已取消，没有基准可导出。
        ///   **取消的只是跨文件互验**；本文件自身的校验（标识 / 类型 / 指纹 / 语言 / 条数下限 /
        ///   声明与实际条数一致）与指纹绑定一字未动 —— 见 `TryReadFile`。
        /// </summary>
        public static bool TryRead(string fingerprint, out Dictionary<string, string> names, out string note)
        {
            names = null;
            note = "";
            if (fingerprint == null || fingerprint.Length == 0)
            {
                note = "无游戏版本指纹，缓存不参与";
                return false;
            }
            // 【t5 P1-3】优先读跨会话持久缓存（毫秒级命中；校验规则与下面完全一致）。
            //    指纹不符 / 语言不符 / 文件损坏 / 条目数与声明不符 → 安全失败并回退重建。
            string ppath = PersistentCacheFile(fingerprint);
            if (ppath != null && File.Exists(ppath))
            {
                Dictionary<string, string> ptable;
                string pnote;
                if (TryReadFile(ppath, fingerprint, out ptable, out pnote))
                {
                    names = ptable;
                    note = "持久缓存命中 " + ptable.Count + " 条（" + pnote + "）";
                    return true;
                }
            }
            string path = ProbeExistingCacheFile();
            if (path == null)
            {
                note = "本次会话还没有缓存文件";
                return false;
            }
            return TryReadFile(path, fingerprint, out names, out note);
        }

        /// <summary>
        /// 按磁盘文件读一次缓存并完成全部**本文件内**的校验。永不抛异常。
        /// 【2026-09-26 · P2】原第 5 个形参 <c>out int seenAliasEntries</c>（导出头部 `alias=`）
        ///   已删除：该值只被跨文件交叉校验消费，字段与判据一并取消后无基准可导出。
        /// </summary>
        private static bool TryReadFile(string path, string fingerprint, out Dictionary<string, string> names,
                                        out string note)
        {
            names = null;
            note = "";
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines == null || lines.Length < 8)
                {
                    note = "缓存文件损坏（行数不足）";
                    return false;
                }

                string magic = null, kind = null, game = null, lang = null, created = null;
                int declared = -1;
                int bodyStart = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null) continue;
                    if (line == "--") { bodyStart = i + 1; break; }
                    if (line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq);
                    string v = line.Substring(eq + 1);
                    if (k == "magic") magic = v;
                    else if (k == "kind") kind = v;
                    else if (k == "game") game = v;
                    else if (k == "lang") lang = v;
                    else if (k == "created") created = v;
                    else if (k == "count") int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out declared);
                    // 【2026-09-26 · P2】旧缓存文件里残留的 `alias=<行数>` 走这条"未知键"路径被
                    //   **无害忽略** —— 这与本方法对其它未知键的既有处理一致，无需额外兼容分支。
                }

                if (bodyStart < 0 || magic != Magic)
                {
                    note = "缓存文件损坏（标识不符）";
                    return false;
                }
                if (kind != "item")
                {
                    note = "缓存文件类型不符（" + (kind == null ? "?" : kind) + "）";
                    return false;
                }
                if (game != fingerprint)
                {
                    note = "游戏版本指纹不符（缓存已过期，将重新扫描）";
                    return false;
                }
                if (lang != "zh")
                {
                    note = "缓存语言指纹不符（" + (lang == null ? "?" : lang) + "）";
                    return false;
                }
                if (declared < MinEntries)
                {
                    note = "缓存词表为空（声明 " + declared + " 条）";
                    return false;
                }
                // 【2026-09-26 · P2 删除】原在此校验 `alias ≥ AliasMinEntries`（缺字段或条数异常
                //   ⇒ 整份名表缓存被拒 ⇒ 强制重建）。该判据随 `alias=` 字段一并取消：
                //   它把「别名表可用性」这一**另一个文件的状态**当作否决**本文件**的理由，
                //   实测已两次造成确定性故障（口径不一致导致的每次启动误拒）。
                //   本方法的其余校验（标识 / 类型 / 指纹 / 语言 / 条数下限 / 声明与实际一致）
                //   全部保留 —— 取消的只是跨文件互验，不是"不校验"。

                Dictionary<string, string> table = new Dictionary<string, string>();
                for (int i = bodyStart; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null || line.Length == 0) continue;
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string id = line.Substring(0, tab);
                    string zh = line.Substring(tab + 1);
                    if (!IsCacheableId(id) || zh.Length == 0) continue;
                    if (table.ContainsKey(id)) continue;
                    table[id] = zh;
                }
                if (table.Count != declared)
                {
                    note = "缓存条目数与声明不符（声明 " + declared + "，实际 " + table.Count + "，文件可能被截断）";
                    return false;
                }
                names = table;
                note = "命中 " + table.Count + " 条（生成于 " + (created == null ? "?" : created) + "）";
                return true;
            }
            catch (Exception ex)
            {
                names = null;
                note = "读缓存失败（将回退内存直读）：" + ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 【2026-09-26 新增】物品属性缓存（id → redSkulls / whiteSkulls）
        //
        // 为什么单独一个文件：名表缓存的格式与三重校验（magic / 指纹 / count 一致）已经很稳，
        // 把属性塞进去要改行格式并升版，牵连面大。属性是**独立的一组进程无关纯数据**，
        // 用同目录下的第二个文件承载，风险最低。
        // 内容纪律与名表缓存完全一致：只有 id + 两个小整数 + 游戏指纹，**绝不含任何内存地址**。
        // ------------------------------------------------------------------

        public const string AttrCacheFileName = "GK2ItemAttrs.cache";
        public const string AttrMagic = "GK2ATTR-CACHE-1";

        /// <summary>属性缓存文件路径（目录不可用时返回 null）。</summary>
        public static string AttrCacheFilePath(string fingerprint)
        {
            string dir = SessionDir();
            if (dir == null) return null;
            return Path.Combine(dir, AttrCacheFileName);
        }

        /// <summary>
        /// 属性缓存的**跨会话持久路径**（与主名表缓存同一机制，后缀 `.attrs` 以区分）。
        ///
        /// 【当前行为（t21 评审 H1 更正口径）】属性缓存有**两份**：会话目录一份 + 本持久路径一份；
        /// **读取时优先持久路径**（见 <c>TryReadAttrs</c>）⇒ **会话内命中 + 跨会话命中**，
        /// 不存在「每次启动都要补扫」这一常态。实测持久文件：
        /// `%LOCALAPPDATA%\GK2Trainer\ItemNames\&lt;指纹&gt;.attrs` = 14,823 B / magic=GK2ATTR-CACHE-1 / count=814。
        ///
        /// 【以下为历史说明，仅用于解释为什么会加持久副本，**不代表当前行为**】
        /// 最初只写会话目录（`%TEMP%\GK2Trainer\s&lt;pid&gt;_&lt;ticks&gt;_&lt;8hex&gt;`），而该目录在进程退出时
        /// 由 <see cref="CleanupCurrentSession"/> **整体删除**，故当时属性缓存跨会话不命中、
        /// 每次启动需靠 `TrainerForm.TryFillAttrsOnly` 补扫（多花约 1~2 s）。该状态已被本方法修正。
        /// t19 的完成记录当时写成「供后续会话的缓存命中路径使用」，**与实现不符**（由 t20 指出）。
        /// 现在补上持久副本，使描述与行为一致：**会话内命中 + 跨会话命中**。
        /// </summary>
        public static string PersistentAttrCacheFile(string fingerprint)
        {
            if (fingerprint == null || fingerprint.Length == 0) return null;
            string root = PersistentRoot();
            if (root == null) return null;
            try
            {
                Directory.CreateDirectory(root);
                StringBuilder safe = new StringBuilder(96);
                for (int i = 0; i < fingerprint.Length && safe.Length < 72; i++)
                {
                    char c = fingerprint[i];
                    bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                        || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                    safe.Append(ok ? c : '_');
                }
                if (safe.Length == 0) return null;
                return Path.Combine(root, safe.ToString() + ".attrs");
            }
            catch { return null; }
        }

        /// <summary>
        /// 写属性缓存。失败（无指纹 / 空表 / 目录不可写）一律安全跳过并返回 false，永不抛异常。
        /// 行格式：<c>id\tred\twhite</c>
        /// </summary>
        public static bool TryWriteAttrs(string fingerprint, IDictionary<string, int[]> attrs, out string note)
        {
            note = "";
            try
            {
                if (fingerprint == null || fingerprint.Length == 0) { note = "无游戏版本指纹，跳过写属性缓存"; return false; }
                if (attrs == null || attrs.Count == 0) { note = "属性表为空，跳过写属性缓存"; return false; }
                string path = AttrCacheFilePath(fingerprint);
                if (path == null) { note = "临时缓存目录不可用，跳过写属性缓存"; return false; }

                List<string> keys = new List<string>(attrs.Keys);
                keys.Sort(StringComparer.Ordinal);
                StringBuilder body = new StringBuilder(64 * 1024);
                int written = 0;
                for (int i = 0; i < keys.Count; i++)
                {
                    string id = keys[i];
                    if (!IsCacheableId(id)) continue;
                    int[] v = attrs[id];
                    if (v == null || v.Length < 2) continue;
                    body.Append(id).Append('\t')
                        .Append(v[0].ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(v[1].ToString(CultureInfo.InvariantCulture)).Append('\n');
                    written++;
                }
                if (written < MinEntries) { note = "属性条目过少（" + written + " 条），跳过写属性缓存"; return false; }

                StringBuilder head = new StringBuilder(256);
                head.Append("magic=").Append(AttrMagic).Append('\n');
                head.Append("kind=itemattr\n");
                head.Append("game=").Append(fingerprint).Append('\n');
                head.Append("created=").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
                head.Append("count=").Append(written.ToString(CultureInfo.InvariantCulture)).Append('\n');
                head.Append("--\n");
                string content = head.ToString() + body.ToString();

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                // 【t20 修正】再写一份**跨会话持久副本**（与主名表缓存同一做法；
                // 失败不影响会话缓存，静默降级）——原实现缺这一步，导致属性缓存随会话目录被删除。
                string pnote = "";
                try
                {
                    string ppath = PersistentAttrCacheFile(fingerprint);
                    if (ppath != null)
                    {
                        string ptmp = ppath + ".tmp";
                        File.WriteAllText(ptmp, content, new UTF8Encoding(false));
                        if (File.Exists(ppath)) File.Delete(ppath);
                        File.Move(ptmp, ppath);
                        pnote = "；并已持久化";
                    }
                }
                catch { pnote = ""; }

                note = "已写属性缓存 " + written + " 条：" + path + pnote;
                return true;
            }
            catch (Exception ex) { note = "写属性缓存失败（已忽略）：" + ex.Message; return false; }
        }

        /// <summary>
        /// 读属性缓存。指纹不符 / 损坏 / 条目数与声明不符一律返回 false，由调用方回退到实时扫描。
        /// 永不抛异常。
        /// </summary>
        public static bool TryReadAttrs(string fingerprint, out Dictionary<string, int[]> attrs, out string note)
        {
            attrs = null;
            note = "";
            if (fingerprint == null || fingerprint.Length == 0) { note = "无游戏版本指纹，属性缓存不参与"; return false; }
            // 【t20 修正】优先读**跨会话持久副本**（会话目录退出即删，若只读会话目录则跨会话永不命中）
            string path = null;
            string ppath = PersistentAttrCacheFile(fingerprint);
            if (ppath != null && File.Exists(ppath)) path = ppath;
            if (path == null)
            {
                string spath = AttrCacheFilePath(fingerprint);
                if (spath != null && File.Exists(spath)) path = spath;
            }
            if (path == null) { note = "本次会话与持久目录都没有属性缓存文件"; return false; }
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines == null || lines.Length < 6) { note = "属性缓存损坏（行数不足）"; return false; }
                string magic = null, game = null;
                int declared = -1, bodyStart = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null) continue;
                    if (line == "--") { bodyStart = i + 1; break; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq);
                    string v = line.Substring(eq + 1);
                    if (k == "magic") magic = v;
                    else if (k == "game") game = v;
                    else if (k == "count") int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out declared);
                }
                if (bodyStart < 0 || magic != AttrMagic) { note = "属性缓存损坏（标识不符）"; return false; }
                if (game != fingerprint) { note = "游戏版本指纹不符（属性缓存已过期）"; return false; }
                if (declared < MinEntries) { note = "属性缓存为空（声明 " + declared + " 条）"; return false; }

                Dictionary<string, int[]> t = new Dictionary<string, int[]>();
                for (int i = bodyStart; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3) continue;
                    string id = parts[0];
                    if (!IsCacheableId(id)) continue;
                    int red, white;
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out red)) continue;
                    if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out white)) continue;
                    if (t.ContainsKey(id)) continue;
                    t[id] = new int[] { red, white };
                }
                if (t.Count != declared) { note = "属性缓存条目数与声明不符（" + t.Count + " vs " + declared + "）"; return false; }
                attrs = t;
                note = "命中属性缓存 " + t.Count + " 条";
                return true;
            }
            catch (Exception ex) { attrs = null; note = "读属性缓存失败（将回退实时扫描）：" + ex.Message; return false; }
        }

        // ------------------------------------------------------------------
        // 别名表缓存（【方案A · t29 返工】aliases1/aliases2 的**纯数据**镜像）
        //   为什么可以落盘：别名表是**游戏资源里的静态数据**，与存档无关、与进程地址无关，
        //   也**不是地址**（红线「地址不落盘」只约束地址；本文件不含任何地址或偏移）。
        //   为什么需要落盘：现场读取实测 +8.09 s（名表/框2 就绪 9.49 s → 17.58 s），
        //   暖启动读回为毫秒级；且**它的内容是语言无关的**（12 个语言的 aliases 文件字节相同）。
        //   绑定：**游戏指纹**（复用 GameFingerprint）+ **独立表结构版本号** AliasMagic；
        //   指纹或标识不符 ⇒ 整体拒绝 ⇒ 回退现场读取。
        // ------------------------------------------------------------------

        public const string AliasCacheFileName = "GK2ItemAlias.cache";
        /// <summary>别名表缓存标识（独立于主名表 Magic 与 AttrMagic；升版即整体失效重建）。</summary>
        public const string AliasMagic = "GK2ALIAS-CACHE-1";
        /// <summary>别名表条数合理区间（与 GameResLocator.ALIAS_MIN/MAX_PLAUSIBLE 同口径）。</summary>
        public const int AliasMinEntries = 1000;
        public const int AliasMaxEntries = 20000;
        /// <summary>
        /// 【t34 · T7】别名表缓存的**专属**体量上限（2 MiB）。
        ///   口径说明（重要，避免误读）：`MaxFileBytes = 400 KB` 只约束**名表缓存**（`TryWrite`）；
        ///   别名表缓存（`.alias`）**天然更大** —— 实测 8709 行 ≈ 440,385 B，**超过** 400 KB
        ///   属**预期**、不是缺陷。这里单独设上限的原因：原实现只靠 `AliasMaxEntries = 20000`
        ///   隐含封顶（理论上可达 MB 级），而 `File.WriteAllText` 本身没有体量保护。
        ///   2 MiB 对当前 440 KB 有 ≈4.5× 余量，且远小于 `AliasMaxEntries` 的隐含上界。
        /// </summary>
        public const int AliasMaxFileBytes = 2 * 1024 * 1024;

        public static string AliasCacheFilePath(string fingerprint)
        {
            string dir = SessionDir();
            if (dir == null) return null;
            return Path.Combine(dir, AliasCacheFileName);
        }

        public static string PersistentAliasCacheFile(string fingerprint)
        {
            if (fingerprint == null || fingerprint.Length == 0) return null;
            try
            {
                string root = PersistentRoot();
                if (root == null) return null;
                Directory.CreateDirectory(root);
                if (!Directory.Exists(root)) return null;
                StringBuilder safe = new StringBuilder();
                for (int i = 0; i < fingerprint.Length && safe.Length < 72; i++)
                {
                    char c = fingerprint[i];
                    bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                              || (c >= '0' && c <= '9') || c == '_' || c == '-';
                    safe.Append(ok ? c : '_');
                }
                if (safe.Length == 0) return null;
                return Path.Combine(root, safe.ToString() + ".alias");
            }
            catch { return null; }
        }

        /// <summary>
        /// 写别名表缓存。行格式：<c>序号\talias_from\talias_to</c>
        /// （**保序、允许重复键** —— 运行时表确有 12 条重复 from，且 List.IndexOf 取首次出现，
        /// 顺序必须原样保留，否则链的解析结果会变）。失败一律安全跳过、永不抛异常。
        /// </summary>
        public static bool TryWriteAlias(string fingerprint, IList<string> from, IList<string> to,
                                         bool verifiedAgainstLoc, out string note)
        {
            note = "";
            try
            {
                if (fingerprint == null || fingerprint.Length == 0) { note = "无游戏版本指纹，跳过写别名表缓存"; return false; }
                if (from == null || to == null) { note = "别名表为空，跳过写别名表缓存"; return false; }
                int n = from.Count;
                if (n != to.Count) { note = "别名表两列长度不等，拒绝写缓存"; return false; }
                if (n < AliasMinEntries || n > AliasMaxEntries) { note = "别名表条数异常（" + n + "），拒绝写缓存"; return false; }
                string path = AliasCacheFilePath(fingerprint);
                if (path == null) { note = "临时缓存目录不可用，跳过写别名表缓存"; return false; }

                StringBuilder body = new StringBuilder(n * 28);
                for (int i = 0; i < n; i++)
                {
                    string a = from[i], b = to[i];
                    if (a == null || b == null) continue;
                    body.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(a).Append('\t').Append(b).Append('\n');
                }

                StringBuilder head = new StringBuilder(256);
                head.Append("magic=").Append(AliasMagic).Append('\n');
                head.Append("kind=itemalias\n");
                head.Append("game=").Append(fingerprint).Append('\n');
                head.Append("created=").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
                head.Append("count=").Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n');
                // 【t29 返工】记录「写出时是否在词表就绪下完成了 ③④ 交叉验证」：
                //   loc-checked = 写之前已用中文词表验证过（链尾确实能解析出中文）；
                //   pending     = 写的时候词表不在内存里（跳过 ③④）⇒ 读回时**必须如实显示**，
                //                 并在下一次词表就绪时补验（不得把"未验证"当成"通过"）。
                // 【t34 · F4】三态取值，避免「没做交叉验证」被读成「已验证」：
                //   loc-checked       = 写出前已在词表就绪下完成 ③④ 交叉验证且通过；
                //   unverified-no-loc = 写出时词表不在内存（③④ 跳过）⇒ **未做**交叉验证；
                //   （旧值 `pending` 读取端同样按「未验证」对待，见 TryReadAlias 的 verify 解析。）
                head.Append("verify=").Append(verifiedAgainstLoc ? "loc-checked" : "unverified-no-loc").Append('\n');
                head.Append("--\n");
                string content = head.ToString() + body.ToString();

                // 【t34 · T7】体量守卫：别名表缓存单独设上限（与名表缓存的 `MaxFileBytes` 是两把尺子）。
                long aliasBytes = Encoding.UTF8.GetByteCount(content);
                if (aliasBytes > AliasMaxFileBytes)
                {
                    note = "别名表缓存体量 " + aliasBytes + " 字节超过上限 " + AliasMaxFileBytes
                         + "，跳过写别名表缓存（本次仍使用内存中的表）";
                    return false;
                }

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                string pnote = "";
                try
                {
                    string ppath = PersistentAliasCacheFile(fingerprint);
                    if (ppath != null)
                    {
                        string ptmp = ppath + ".tmp";
                        File.WriteAllText(ptmp, content, new UTF8Encoding(false));
                        if (File.Exists(ppath)) File.Delete(ppath);
                        File.Move(ptmp, ppath);
                        pnote = "；并已持久化";
                    }
                }
                catch { pnote = ""; }

                note = "已写别名表缓存 " + n + " 条：" + path + pnote;
                return true;
            }
            catch (Exception ex) { note = "写别名表缓存失败（已忽略）：" + ex.Message; return false; }
        }

        /// <summary>
        /// 读别名表缓存。标识 / 指纹 / 声明条数 / 两列长度任一不符即返回 false（调用方回退现场读取）。
        /// 本方法只做**格式级**校验；**内容级**校验由
        /// <c>GameResLocator.TryImportAliasTable</c> 完成（条数区间 + 两列等长 + 全量形态）。
        /// 【t29 R1-2】声明条数异常（0 / 半截 / 超界）⇒ 拒绝 ⇒ 下次启动**强制重试**现场读取，
        /// 绝不会把「建表失败」的状态永久固化。
        ///
        /// 【2026-09-26 · P2】**跨文件严格相等判据已取消**（原 5 参重载，把本文件头的 `count`
        ///   与名表缓存头的 `alias=` 做严格相等比较）。取消原因与代价：
        ///   · 判据要求两个**互相独立**的持久文件口径严格一致，而两条写 `.cache` 的路径口径
        ///     曾经不同（行数 8709 / 去重键数 8697）⇒ 判据把一次口径笔误放大成「每次启动都被
        ///     判残缺并拒绝」的确定性故障（U1）。用户裁决：让每个缓存文件**各自自洽**即可。
        ///   · 删掉 `.cache` 头的 `alias=` 字段后已无基准可用 ⇒ 形参与判据分支一并删除，
        ///     不保留成"传 0 跳过"的死代码。
        ///   ⚠ **已知边界（不是疏漏）**：一张「正文被截短、同时把头部 `count` 改成一致的」
        ///     **自洽残缺表**将**不再被检出** —— 本方法的条数区间（`AliasMinEntries` = 1000）
        ///     拦不住（例如 4000 行 > 1000），缓存命中路径又没有词表可交叉验证。这是用户已
        ///     明确知晓并接受的**防护降级**。
        ///     **缓解手段**：任何"拒绝/损坏"现在都能靠 P1 的写回通路自愈（`.alias` 不可用或
        ///     被拒 ⇒ 现场读取 ⇒ `LocAliasBackfillWorker` 补写回），不再永久退化（F-A 已修）。
        ///     残余未覆盖面：内容级损坏（&gt;10% 非法标识符）会置 `_aliasIndexFailed`，
        ///     使现场重建被 `EnsureAliasIndex` 早退挡下 ⇒ 该路径**仍不自愈**（P1 §5.2 已登记）。
        /// </summary>
        public static bool TryReadAlias(string fingerprint, out List<string> from, out List<string> to,
                                        out string verify, out string note)
        {
            from = null; to = null; verify = ""; note = "";
            if (fingerprint == null || fingerprint.Length == 0) { note = "无游戏版本指纹，别名表缓存不参与"; return false; }
            string path = null;
            string ppath = PersistentAliasCacheFile(fingerprint);
            if (ppath != null && File.Exists(ppath)) path = ppath;
            if (path == null)
            {
                string spath = AliasCacheFilePath(fingerprint);
                if (spath != null && File.Exists(spath)) path = spath;
            }
            if (path == null) { note = "本次会话与持久目录都没有别名表缓存文件"; return false; }
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines == null || lines.Length < 6) { note = "别名表缓存损坏（行数不足）"; return false; }
                string magic = null, game = null;
                int declared = -1, bodyStart = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null) continue;
                    if (line == "--") { bodyStart = i + 1; break; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq);
                    string v = line.Substring(eq + 1);
                    if (k == "magic") magic = v;
                    else if (k == "game") game = v;
                    else if (k == "count") int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out declared);
                    else if (k == "verify") verify = v;
                }
                if (bodyStart < 0 || magic != AliasMagic) { note = "别名表缓存损坏（标识不符）"; return false; }
                if (game != fingerprint) { note = "游戏版本指纹不符（别名表缓存已过期）"; return false; }
                if (declared < AliasMinEntries || declared > AliasMaxEntries)
                { note = "别名表缓存条数异常（声明 " + declared + " 条），已拒绝并将在下次强制重试"; return false; }
                // 【2026-09-26 · P2 删除】原在此做跨文件严格相等校验（`declared != expectAliasEntries`
                //   ⇒ 拒绝），用于检出「正文截短 + count 同步改小」的残缺表注入。该判据已随
                //   `.cache` 头的 `alias=` 字段一并取消 —— 代价与缓解见本方法上方注释的
                //   「已知边界」段落：自洽残缺表**不再被检出**（预期降级），
                //   但任何拒绝都能靠 P1 的写回通路自愈，不再永久退化。
                //   保留下面的自洽性校验（条数区间 + 两列等长 + 条目数与声明一致）。

                List<string> f = new List<string>(declared);
                List<string> t = new List<string>(declared);
                for (int i = bodyStart; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3) continue;
                    if (parts[1].Length == 0 || parts[2].Length == 0) continue;
                    f.Add(parts[1]);
                    t.Add(parts[2]);
                }
                if (f.Count != declared || f.Count != t.Count)
                { note = "别名表缓存条目数与声明不符（" + f.Count + " vs " + declared + "）"; return false; }
                from = f; to = t;
                // 【2026-09-26 · P2】原 note 末尾还带一段「条数交叉校验通过 / ⚠ 无交叉基准」的标注 ——
                //   随判据取消一并删除（不再存在"判据有没有执行"这件事）。verify 状态照旧如实标注。
                note = "命中别名表缓存 " + f.Count + " 条（写出时验证状态："
                     + (verify.Length == 0 ? "未标注（旧格式）" : verify) + "）";
                return true;
            }
            catch (Exception ex) { from = null; to = null; note = "读别名表缓存失败（将回退现场读取）：" + ex.Message; return false; }
        }

        // ------------------------------------------------------------------
        // 小工具
        // ------------------------------------------------------------------

        /// <summary>
        /// 可作为缓存条目的 id：ASCII 标识符，长度 1..64，允许 <c>_ . - : /</c>
        /// （与 GameResLocator.IsAsciiId 同一字符集 —— 品质变体 id 形如 <c>cabbage:1</c>，
        /// 它们同样是真实物品，不能被漏掉）。这些字符都不会破坏「id \t 名称」行格式。
        /// </summary>
        private static bool IsCacheableId(string s)
        {
            if (s == null || s.Length == 0 || s.Length > 64) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '_'
                    || c == '.' || c == '-' || c == ':' || c == '/';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>把显示名压成单行（缓存是行式文本，不能含制表符/换行）。</summary>
        private static string CleanInline(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\t' || c == '\r' || c == '\n') { sb.Append(' '); continue; }
                if (c < 0x20) continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
