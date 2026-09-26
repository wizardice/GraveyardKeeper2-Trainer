using System;
using System.Windows.Forms;

// ── PE 版本资源（用户右键 exe → 属性 → 详细信息）──────────────────────────────
// 【2026-09-26 P0】此前这些特性全为默认值：FileDescription / LegalCopyright 是空串、
// FileVersion 是 0.0.0.0 —— 正是启发式引擎判「来历不明的可执行文件」的画像（对照报告 §3.1）。
// 这里按 README 口径填全；**改版本号时四处一起改**，不得只改一处：
//   ① 本文件 AssemblyFileVersion / AssemblyVersion   ② TrainerForm.cs 的窗口标题
//   ③ README.md「版本」表                             ④ 发版时 GitHub Release 的 tag
// （第 ⑤ 处 assets\screenshot_main.png 里的标题栏随发版重拍。）
[assembly: System.Reflection.AssemblyTitle("守墓人2 修改器")]
[assembly: System.Reflection.AssemblyDescription("《守墓人2》(Graveyard Keeper 2) 正式版内存修改器：免安装单文件、不联网、不改动游戏文件。")]
[assembly: System.Reflection.AssemblyCompany("东皇钟")]
[assembly: System.Reflection.AssemblyProduct("GK2Trainer")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (C) 2026 东皇钟 · Apache License 2.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.1.0")]
[assembly: System.Reflection.AssemblyVersion("1.1.1.0")]

namespace GK2Trainer
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // 临时缓存目录的「异常退出兜底清理」（正常退出走 TrainerForm.OnClosing）
            NameCacheStore.HookProcessExitCleanup();
            // 【2026-09-24】低于正常优先级:启动加载期的全堆定位扫描不再与游戏抢 CPU
            // (游戏加载期扫描只在 CPU 空闲时推进,就绪后的热路径本就是微秒级读,不受影响)
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                      System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrainerForm());
        }
    }
}
