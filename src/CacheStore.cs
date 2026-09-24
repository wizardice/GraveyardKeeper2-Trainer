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
        public const string Magic = "GK2LOC-CACHE-1";
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
                string[] files = Directory.GetFiles(root, "*.cache");
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

        /// <summary>按磁盘文件读一次缓存并完成全部校验。永不抛异常。</summary>
        private static bool TryReadFile(string path, string fingerprint, out Dictionary<string, string> names, out string note)
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
