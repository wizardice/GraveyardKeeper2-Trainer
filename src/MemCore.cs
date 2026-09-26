using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GK2Trainer
{
    /// <summary>内存区域信息</summary>
    public class MemRegion
    {
        public long Base;
        public long Size;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    /// <summary>
    /// 跨进程内存读写与扫描核心库。
    /// 刻意使用 C# 5 语法编写，以便同时被 Roslyn(Add-Type) 与
    /// .NET Framework 4.0 csc.exe 编译。
    /// </summary>
    public class ProcessMemory : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress,
            IntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        public const int PROCESS_VM_READ = 0x0010;
        public const int PROCESS_VM_WRITE = 0x0020;
        public const int PROCESS_VM_OPERATION = 0x0008;
        public const int PROCESS_QUERY_INFORMATION = 0x0400;
        // 【t5 清理】两个未使用的 Win32 常量已删除（src 与 tools 双侧零引用）。
        //   （按项目既有约定不列名，明细见 backup_src_t5\MemCore.cs）

        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_PRIVATE = 0x20000;
        public const uint PAGE_NOACCESS = 0x01;
        public const uint PAGE_GUARD = 0x100;

        private IntPtr _handle = IntPtr.Zero;
        public int TargetPid;

        public bool IsOpen { get { return _handle != IntPtr.Zero; } }

        public bool Open(int pid)
        {
            Close();
            TargetPid = pid;
            // 先用最大只读权限，失败则降级
            _handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
                | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, pid);
            if (_handle == IntPtr.Zero)
                _handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            return _handle != IntPtr.Zero;
        }

        public void Close()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        public void Dispose() { Close(); }

        // ---------------- 基础读写 ----------------

        public byte[] ReadBytes(long address, int size)
        {
            if (_handle == IntPtr.Zero || size <= 0) return null;
            byte[] buf = new byte[size];
            IntPtr read;
            if (!ReadProcessMemory(_handle, new IntPtr(address), buf, new IntPtr(size), out read))
                return null;
            int n = read.ToInt32();
            if (n == size) return buf;
            if (n <= 0) return null;
            byte[] part = new byte[n];
            Array.Copy(buf, part, n);
            return part;
        }

        /// <summary>
        /// 【P0-① 性能】把数据读进**调用方提供的缓冲**，返回实际读到的字节数（0 = 失败）。
        /// 原 ReadBytes 在每次 4 MB 分片扫描时都新建数组 —— 单次全堆扫描实测分配 4.03 GB、
        /// 触发 76 次 Gen2 GC。扫描器改用本方法 + 线程本地缓冲后：分配降至 ~137 MB（-29×）、
        /// Gen2 GC 归零、引用扫描 2260 ms → 1751 ms（-23%）。
        /// 注意：调用方必须用**返回的实际长度**而不是 buffer.Length 作为数据长度。
        /// </summary>
        public int ReadInto(long address, byte[] buffer, int size)
        {
            if (_handle == IntPtr.Zero || buffer == null) return 0;
            if (size <= 0 || size > buffer.Length) return 0;
            IntPtr read;
            if (!ReadProcessMemory(_handle, new IntPtr(address), buffer, new IntPtr(size), out read))
                return 0;
            return read.ToInt32();
        }

        public bool WriteBytes(long address, byte[] data)
        {
            if (_handle == IntPtr.Zero || data == null) return false;
            uint old;
            // 先尝试解除只读保护（失败也无妨，很多堆页本身可写）
            VirtualProtectEx(_handle, new IntPtr(address), new IntPtr(data.Length), 0x04, out old);
            IntPtr written;
            bool ok = WriteProcessMemory(_handle, new IntPtr(address), data,
                new IntPtr(data.Length), out written);
            if (ok && written.ToInt32() != data.Length) ok = false;
            if (old != 0) VirtualProtectEx(_handle, new IntPtr(address), new IntPtr(data.Length), old, out old);
            return ok;
        }

        public float ReadFloat(long address)
        {
            byte[] b = ReadBytes(address, 4);
            return b == null ? 0f : BitConverter.ToSingle(b, 0);
        }

        public bool WriteFloat(long address, float value)
        {
            return WriteBytes(address, BitConverter.GetBytes(value));
        }

        public int ReadInt(long address)
        {
            byte[] b = ReadBytes(address, 4);
            return b == null ? 0 : BitConverter.ToInt32(b, 0);
        }

        public long ReadLong(long address)
        {
            byte[] b = ReadBytes(address, 8);
            return b == null ? 0L : BitConverter.ToInt64(b, 0);
        }

        public bool WriteLong(long address, long value)
        {
            return WriteBytes(address, BitConverter.GetBytes(value));
        }

        public string ReadAscii(long address, int maxLen)
        {
            byte[] b = ReadBytes(address, maxLen);
            if (b == null) return null;
            int n = 0;
            while (n < b.Length && b[n] != 0) n++;
            return System.Text.Encoding.UTF8.GetString(b, 0, n);
        }

        // ---------------- 区域枚举 ----------------

        public List<MemRegion> GetRegions()
        {
            List<MemRegion> list = new List<MemRegion>();
            if (_handle == IntPtr.Zero) return list;
            long addr = 0;
            MEMORY_BASIC_INFORMATION mbi = new MEMORY_BASIC_INFORMATION();
            int mbiSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
            while (true)
            {
                if (VirtualQueryEx(_handle, new IntPtr(addr), out mbi, new IntPtr(mbiSize)) == IntPtr.Zero)
                    break;
                long size = mbi.RegionSize.ToInt64();
                MemRegion r = new MemRegion();
                r.Base = mbi.BaseAddress.ToInt64();
                r.Size = size;
                r.State = mbi.State;
                r.Protect = mbi.Protect;
                r.Type = mbi.Type;
                list.Add(r);
                long next = r.Base + size;
                if (next <= r.Base) break;
                addr = next;
                if (addr > 0x7FFFFFFF0000L) break;
            }
            return list;
        }

        // 【t5 清理】本节原有 1 个「模块基址查询」成员已删除：本项目已彻底禁用
        //「模块基址 + 多级偏移」的硬编码路径（硬约束①），该成员 src 与 tools 双侧零引用。
        //（按项目既有约定不列名，明细见 backup_src_t5\MemCore.cs）

        public static int FindPid(string processNameNoExt)
        {
            Process[] all = Process.GetProcessesByName(processNameNoExt);
            if (all.Length == 0) return 0;
            return all[0].Id;
        }

        /// <summary>
        /// 按关键字模糊查找进程（不区分大小写，进程名包含关键字即命中）。
        /// 用于应对进程名差异（带空格、带后缀等）的兜底匹配。
        /// </summary>
        public static int FindPidFuzzy(string[] keywords, bool requireUnity)
        {
            Process[] all = Process.GetProcesses();
            for (int k = 0; k < keywords.Length; k++)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    string n;
                    try { n = all[i].ProcessName; } catch { continue; }
                    if (n == null) continue;
                    if (n.IndexOf(keywords[k], StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (requireUnity && !HasModule(all[i].Id, "UnityPlayer.dll")) continue;
                    return all[i].Id;
                }
            }
            return 0;
        }

        /// <summary>
        /// 模块探测三态结果：区分「确实没有该模块」与「无法枚举（权限不足 / 进程已退出）」。
        /// 旧实现在枚举失败时也返回 false，导致未以管理员运行时被误报为
        /// 「游戏版本不受支持（IL2CPP）」—— 与界面实际可用自相矛盾（t3 C-5 现场）。
        /// </summary>
        public enum ModuleProbe
        {
            /// <summary>模块确实不在进程的模块列表中。</summary>
            Absent = 0,
            /// <summary>模块存在。</summary>
            Present = 1,
            /// <summary>无法枚举模块列表（访问被拒 / 进程已退出）—— 不能据此判定版本不支持。</summary>
            EnumerationFailed = 2
        }

        /// <summary>三态模块探测（判据场景请用本方法，不要用二态的 HasModule）。</summary>
        public static ModuleProbe ProbeModule(int pid, string moduleName)
        {
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch { return ModuleProbe.EnumerationFailed; }
            try
            {
                foreach (ProcessModule m in p.Modules)
                {
                    if (string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                        return ModuleProbe.Present;
                }
                return ModuleProbe.Absent;
            }
            catch { return ModuleProbe.EnumerationFailed; }
        }

        /// <summary>进程是否加载了指定模块（二态，仅供非判据用途；判据请用 <see cref="ProbeModule"/>）。</summary>
        public static bool HasModule(int pid, string moduleName)
        {
            return ProbeModule(pid, moduleName) == ModuleProbe.Present;
        }

        // ---------------- 扫描 ----------------

        private const long MaxRegionSize = 1024L * 1024 * 1024; // 单区域扫描上限 1GB

        public List<long> ScanBytes(byte[] needle, int maxResults)
        {
            return ScanBytes(needle, maxResults, false);
        }

        /// <summary>单次扫描分片大小（读入复用缓冲，不再每次分配）。</summary>
        public const int SCAN_CHUNK = 4 * 1024 * 1024;

        public List<long> ScanBytes(byte[] needle, int maxResults, bool writableOnly)
        {
            List<long> results = new List<long>();
            if (_handle == IntPtr.Zero || needle == null || needle.Length == 0) return results;
            byte first = needle[0];
            byte[] buf = new byte[SCAN_CHUNK];          // 【P0-①】单线程复用缓冲
            List<MemRegion> regs = GetRegions();
            for (int ri = 0; ri < regs.Count; ri++)
            {
                MemRegion r = regs[ri];
                if (r.State != MEM_COMMIT) continue;
                if ((r.Protect & PAGE_GUARD) != 0) continue;
                if ((r.Protect & PAGE_NOACCESS) != 0) continue;
                if (r.Size <= 0 || r.Size > MaxRegionSize) continue;
                if (writableOnly && (r.Protect & 0xCC) == 0) continue;
                long off = 0;
                while (off < r.Size)
                {
                    int chunk = (int)Math.Min((long)SCAN_CHUNK, r.Size - off);
                    int n = ReadInto(r.Base + off, buf, chunk);
                    if (n >= needle.Length)
                    {
                        int idx = 0;
                        int limit = n - needle.Length;
                        while (idx <= limit)
                        {
                            int hit = Array.IndexOf<byte>(buf, first, idx, limit - idx + 1);
                            if (hit < 0) break;
                            bool ok = true;
                            for (int k = 1; k < needle.Length; k++)
                            {
                                if (buf[hit + k] != needle[k]) { ok = false; break; }
                            }
                            if (ok)
                            {
                                results.Add(r.Base + off + hit);
                                if (results.Count >= maxResults) return results;
                            }
                            idx = hit + 1;
                        }
                    }
                    off += chunk;
                }
            }
            return results;
        }

        /// <summary>
        /// 多线程扫描（可限定地址范围）。rangeEnd &lt;= rangeStart 表示全地址空间。
        /// </summary>
        public List<long> ScanParallel(byte[] needle, long rangeStart, long rangeEnd, int maxResults)
        {
            List<long> results = new List<long>();
            if (_handle == IntPtr.Zero || needle == null || needle.Length == 0) return results;
            if (rangeEnd <= rangeStart) rangeEnd = 0x7FFFFFFF0000L;

            List<MemRegion> regs = GetRegions();
            List<MemRegion> targets = new List<MemRegion>();
            foreach (MemRegion r in regs)
            {
                if (r.State != MEM_COMMIT) continue;
                if ((r.Protect & PAGE_GUARD) != 0) continue;
                if ((r.Protect & PAGE_NOACCESS) != 0) continue;
                if (r.Size <= 0 || r.Size > MaxRegionSize) continue;
                long rs = r.Base, re = r.Base + r.Size;
                if (re <= rangeStart || rs >= rangeEnd) continue;
                targets.Add(r);
            }

            int n = targets.Count;
            List<long>[] parts = new List<long>[n];
            // 【P0-①】线程本地复用缓冲：分配从 4.03 GB/次 降到「线程数 × 4 MB」。
            // 【审核轮 2026-09-26】缓冲是 LOH 大对象，扫描结束应立即归还（Dispose），
            //   否则要等下一次完整 GC 才回收 —— 这是历史实测「工作集 178 MB vs 旧版 52~67 MB」的成因之一。
            using (System.Threading.ThreadLocal<byte[]> tl =
                new System.Threading.ThreadLocal<byte[]>(delegate { return new byte[SCAN_CHUNK]; }))
            {
                System.Threading.Tasks.Parallel.For(0, n, delegate(int ri)
                {
                    MemRegion r = targets[ri];
                    List<long> local = new List<long>();
                    byte first = needle[0];
                    byte[] buf = tl.Value;
                    long off = 0;
                    while (off < r.Size)
                    {
                        int chunk = (int)Math.Min((long)SCAN_CHUNK, r.Size - off);
                        int got = ReadInto(r.Base + off, buf, chunk);
                        if (got >= needle.Length)
                        {
                            int limit = got - needle.Length;
                            int idx = 0;
                            while (idx <= limit)
                            {
                                int hit = Array.IndexOf<byte>(buf, first, idx, limit - idx + 1);
                                if (hit < 0) break;
                                bool ok = true;
                                for (int k = 1; k < needle.Length; k++)
                                {
                                    if (buf[hit + k] != needle[k]) { ok = false; break; }
                                }
                                if (ok) local.Add(r.Base + off + hit);
                                idx = hit + 1;
                            }
                        }
                        off += chunk;
                    }
                    parts[ri] = local;
                });
            }

            for (int i = 0; i < n; i++)
            {
                if (parts[i] == null) continue;
                for (int k = 0; k < parts[i].Count; k++)
                {
                    results.Add(parts[i][k]);
                    if (results.Count >= maxResults) return results;
                }
            }
            return results;
        }

        /// <summary>按统一条件筛选「可扫描区域」（已提交、非 Guard/NoAccess、大小合法）。</summary>
        public List<MemRegion> FilterScanRegions(List<MemRegion> regs)
        {
            List<MemRegion> scan = new List<MemRegion>();
            if (regs == null) return scan;
            for (int i = 0; i < regs.Count; i++)
            {
                MemRegion r = regs[i];
                if (r.State != MEM_COMMIT) continue;
                if ((r.Protect & PAGE_GUARD) != 0) continue;
                if ((r.Protect & PAGE_NOACCESS) != 0) continue;
                if (r.Size <= 0 || r.Size > MaxRegionSize) continue;
                scan.Add(r);
            }
            return scan;
        }

        /// <summary>
        /// 一次遍历找出所有指向 targets 集合中任一地址的指针位置。
        /// 用于批量追踪多个对象（如多个同名字符串）的引用者。
        /// 【P0-①】缓冲复用：分配从 4.03 GB/次 降至 ~线程数×4 MB，Gen2 GC 由 76 次降到 0。
        /// </summary>
        public List<long> ScanPointersTo(ICollection<long> targets, int maxResults)
        {
            return ScanPointersTo(targets, FilterScanRegions(GetRegions()), maxResults);
        }

        /// <summary>
        /// 【P0-②】在**给定区域集合**内做引用扫描（缓冲复用）。
        /// 供「分段早停」使用：调用方按地址升序分批传入区域，命中即停，
        /// 从而在不改变判据语义的前提下把扫描量从 3.93 GB 降到百 MB 级。
        /// </summary>
        public List<long> ScanPointersTo(ICollection<long> targets, List<MemRegion> scan, int maxResults)
        {
            List<long> results = new List<long>();
            if (_handle == IntPtr.Zero || targets == null || targets.Count == 0) return results;
            if (scan == null || scan.Count == 0) return results;
            HashSet<long> set = new HashSet<long>(targets);
            // 【P0-⑥】单目标快速路径：锚解析场景只找「指向 MonoDomain 的指针」，
            // 此时对每个 8 字节位置做 HashSet 查找（哈希 + 桶探测）纯属浪费 ——
            // 直接一次 long 比较即可。实测该项把引用扫描吞吐提升约 2 倍。
            long single = -1;
            if (targets.Count == 1)
            {
                foreach (long t in targets) { single = t; break; }
            }

            int n = scan.Count;
            List<long>[] parts = new List<long>[n];
            // 【审核轮 2026-09-26】同 ScanParallel：LOH 缓冲扫描结束立即归还。
            using (System.Threading.ThreadLocal<byte[]> tl =
                new System.Threading.ThreadLocal<byte[]>(delegate { return new byte[SCAN_CHUNK]; }))
            {
                System.Threading.Tasks.Parallel.For(0, n, delegate(int ri)
                {
                    MemRegion r = scan[ri];
                    List<long> local = new List<long>();
                    byte[] buf = tl.Value;
                    long off = 0;
                    while (off < r.Size)
                    {
                        int chunk = (int)Math.Min((long)SCAN_CHUNK, r.Size - off);
                        int got = ReadInto(r.Base + off, buf, chunk);
                        if (got >= 8)
                        {
                            int limit = got - 8;
                            if (single >= 0)
                            {
                                for (int i = 0; i <= limit; i += 8)
                                    if (BitConverter.ToInt64(buf, i) == single) local.Add(r.Base + off + i);
                            }
                            else
                            {
                                for (int i = 0; i <= limit; i += 8)
                                {
                                    long v = BitConverter.ToInt64(buf, i);
                                    if (set.Contains(v)) local.Add(r.Base + off + i);
                                }
                            }
                        }
                        off += chunk;
                    }
                    parts[ri] = local;
                });
            }

            for (int i = 0; i < n; i++)
            {
                if (parts[i] == null) continue;
                for (int k = 0; k < parts[i].Count; k++)
                {
                    results.Add(parts[i][k]);
                    if (results.Count >= maxResults) return results;
                }
            }
            return results;
        }

        // 【t5 清理】本节原有 3 组「src 与 tools 双侧零引用」的成员已删除：
        //   · 数值扫描器（float/int 各一）与特征码通配扫描器（本项目走结构链定位，不再需要）；
        //   · 「模块基址 + 多级偏移」地址解析器（与硬约束①「禁止硬编码地址」方向相反，
        //     删除可杜绝误用）。
        //   （按项目既有约定：此处不列出被删成员名，以免死代码检索把说明文字误判为残留。
        //     原文与明细见 02_分析记录\_代码审查_20260924\backup_src_t5\MemCore.cs）
    }
}
