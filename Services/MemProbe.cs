using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 内存体检（--mem）：把常驻占用拆成「托管堆 / 模块映射 / GDI 与 USER 对象」三块，
    /// 并量一次 EmptyWorkingSet 的真实效果——它只把页面推回分页文件，提交量一点没少。
    /// 数字摆在这里，省得把「任务管理器那列变小了」当成真的省了内存。
    /// </summary>
    internal static class MemProbe
    {
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        /// <summary>
        /// 把本进程的工作集页推回分页文件。注意它**不减少提交量**：
        /// Private 该多少还是多少，只是把「任务管理器里那一列」压下去。
        /// 只有在窗口不可见时调用才划算——可见时下一帧重绘又会把页全部读回来，
        /// 变成反复换页，越修越慢。
        /// </summary>
        public static void TrimWorkingSet()
        {
            try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
            catch (Exception) { }
        }

        public static string Capture(bool trimWorkingSet)
        {
            var p = Process.GetCurrentProcess();
            p.Refresh();

            var sb = new StringBuilder();
            sb.AppendLine("== PerfOverlay 内存体检 ==");
            sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(string.Format(
                "WorkingSet {0:N1} MB / Private {1:N1} MB / Paged {2:N1} MB / PeakWS {3:N1} MB",
                p.WorkingSet64 / 1048576.0, p.PrivateMemorySize64 / 1048576.0,
                p.PagedMemorySize64 / 1048576.0, p.PeakWorkingSet64 / 1048576.0));
            sb.AppendLine(string.Format("线程 {0} / 句柄 {1} / GDI {2} / USER {3}",
                p.Threads.Count, p.HandleCount,
                GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1)));

            sb.AppendLine(string.Format("托管堆 {0:N1} MB（回收次数 Gen0 {1} / Gen1 {2} / Gen2 {3}）",
                GC.GetTotalMemory(false) / 1048576.0,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));

            var asms = AppDomain.CurrentDomain.GetAssemblies();
            sb.AppendLine("托管程序集 " + asms.Length + " 个：");
            foreach (var a in asms.OrderBy(x => x.GetName().Name, StringComparer.Ordinal))
                sb.AppendLine("  " + a.GetName().Name);

            ProcessModuleCollection mods = null;
            try { mods = p.Modules; }
            catch (Exception ex) { sb.AppendLine("模块读取失败：" + ex.GetType().Name); }

            if (mods != null)
            {
                var list = new List<ProcessModule>();
                foreach (ProcessModule m in mods) list.Add(m);
                sb.AppendLine("模块（按映射大小 top 15，共 " + list.Count + " 个）：");
                foreach (var m in list.OrderByDescending(x => x.ModuleMemorySize).Take(15))
                    sb.AppendLine(string.Format("  {0,-30} {1,6:N1} MB",
                        m.ModuleName, m.ModuleMemorySize / 1048576.0));
            }

            if (trimWorkingSet)
            {
                long wsBefore = p.WorkingSet64;
                TrimWorkingSet();
                p.Refresh();
                sb.AppendLine(string.Format(
                    "EmptyWorkingSet：WS {0:N1} → {1:N1} MB，但 Private 仍是 {2:N1} MB（提交量没变）",
                    wsBefore / 1048576.0, p.WorkingSet64 / 1048576.0, p.PrivateMemorySize64 / 1048576.0));

                long heapBefore = GC.GetTotalMemory(false);
                long heapAfter = GC.GetTotalMemory(true);
                sb.AppendLine(string.Format("强制回收：托管堆 {0:N1} → {1:N1} MB",
                    heapBefore / 1048576.0, heapAfter / 1048576.0));
                p.Refresh();
                sb.AppendLine(string.Format("回收后 WorkingSet {0:N1} MB / Private {1:N1} MB",
                    p.WorkingSet64 / 1048576.0, p.PrivateMemorySize64 / 1048576.0));
            }
            else
            {
                sb.AppendLine("（本次不做强制回收 / 工作集修剪，保持真实常驻状态）");
            }

            sb.AppendLine();
            return sb.ToString();
        }
    }
}
