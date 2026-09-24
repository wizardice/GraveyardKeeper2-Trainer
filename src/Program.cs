using System;
using System.Windows.Forms;

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
