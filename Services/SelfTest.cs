using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 自检：不开窗，把每一项采集能力实测一遍并写报告。
    /// 交付前用它确认「哪一项真的拿到了数据」，而不是靠界面看起来有数字。
    /// </summary>
    public static class SelfTest
    {
        /// <summary>
        /// 用已知序列验证 1% Low 的取值与算法，而不是"看着差不多"。
        /// 口径：最差 1% 帧的「平均帧时间」取倒数。
        /// </summary>
        private static void RunLowFpsChecks(Action<string> w)
        {
            int pass = 0, fail = 0;

            Action<string, double, double> check = (name, actual, expect) =>
            {
                double diff = Math.Abs(actual - expect);
                bool ok = diff <= Math.Max(0.6, expect * 0.01);   // 允许 1% 或 0.6fps 误差
                if (ok) pass++; else fail++;
                w(string.Format("  [{0}] {1}: 实际 {2:0.00} / 期望 {3:0.00}",
                    ok ? "通过" : "失败", name, actual, expect));
            };

            // 1) 稳定 60fps：1% Low 应当也是 60
            var a = new List<double>();
            for (int i = 0; i < 600; i++) a.Add(1000.0 / 60.0);
            check("稳定 60fps × 600 帧", FrameStats.LowFps(a, 0.01), 60.0);

            // 2) 599 帧 60fps + 1 帧 100ms（600 帧的 1% = 6 帧）
            //    最差 6 帧平均 = (100 + 5×16.667)/6 = 30.56ms → 32.7fps
            var b = new List<double>(a);
            b[0] = 100.0;
            check("1 次 100ms 尖刺", FrameStats.LowFps(b, 0.01), 1000.0 / ((100.0 + 5 * (1000.0 / 60.0)) / 6));

            // 3) 500 帧 60fps + 5 帧 50ms（505 帧的 1% → ceil=6 帧）
            //    最差 6 帧 = 5×50 + 1×16.667 = 266.67 → 平均 44.44ms → 22.5fps
            var c = new List<double>();
            for (int i = 0; i < 500; i++) c.Add(1000.0 / 60.0);
            for (int i = 0; i < 5; i++) c.Add(50.0);
            check("5 次 50ms 卡顿", FrameStats.LowFps(c, 0.01), 1000.0 / ((5 * 50.0 + 1000.0 / 60.0) / 6));

            // 4) 空数据 / 全零：必须回 0，不能算出 Infinity 或 NaN
            check("空数据", FrameStats.LowFps(new List<double>(), 0.01), 0.0);
            check("全零帧", FrameStats.LowFps(new List<double> { 0, 0, 0 }, 0.01), 0.0);

            // 5) 反例校验：如果误用「按 FPS 排序」会得到明显偏高的结果，
            //    这里确认我们用的是帧时间口径（60fps+50ms 的用例必须落在 22.5 附近而不是 50+）
            w(string.Format("  合计：通过 {0} 项，失败 {1} 项", pass, fail));
        }

        public static void Run()
        {
            var sb = new StringBuilder();
            Action<string> w = line => sb.AppendLine(line);

            w("=== PerfOverlay 自检 ===");
            w("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            w("OS:   " + Environment.OSVersion.VersionString + "  (" + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + " 系统)");
            w("进程: " + (Environment.Is64BitProcess ? "x64" : "x86") + "  CLR: " + Environment.Version);
            w("目录: " + AppDomain.CurrentDomain.BaseDirectory);
            w("");

            w("--- 权限 ---");
            w("管理员:               " + PrivilegeHelper.IsElevated);
            w("PerformanceLogUsers:  " + PrivilegeHelper.IsInPerformanceLogUsers);
            w("可采集帧数:           " + PrivilegeHelper.CanCaptureFrames);
            w("");

            w("--- PresentMon ---");
            string pm = MonitorHost.PresentMonPath();
            w("路径: " + pm);
            w("存在: " + File.Exists(pm));
            w("");

            w("--- CPU ---");
            var sys = new SystemMetricsProvider();
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(300);
                sys.Sample();
                w(string.Format("  采样 {0}: {1:0.0}%", i + 1, sys.CpuPercent));
            }
            w("可用: " + sys.CpuAvailable);
            w("");

            w("--- 内存 ---");
            w(string.Format("  占用 {0:0.0}%  {1:0.00} GB / {2:0.00} GB",
                sys.MemoryPercent,
                sys.MemoryUsedBytes / 1073741824.0,
                sys.MemoryTotalBytes / 1073741824.0));
            w("");

            w("--- 显存 (DXGI + GPU 计数器) ---");
            var gpu = new GpuMemoryProbe();
            bool init = gpu.Initialize();
            w("初始化: " + init + "  适配器: " + (gpu.AdapterName.Length > 0 ? gpu.AdapterName : "(未取到)"));
            for (int i = 0; i < gpu.Trace.Count; i++) w("  追踪 | " + gpu.Trace[i]);
            ulong used, total;
            bool okSample = gpu.TrySample(out used, out total);
            w("采样: " + okSample);
            if (okSample)
            {
                w(string.Format("  已用 = {0:0.00} GB", used / 1073741824.0));
                w(string.Format("  总量 = {0:0.00} GB", total / 1073741824.0));
                w(string.Format("  利用率 = {0:0.0}%", total > 0 ? used * 100.0 / total : 0));
            }
            w("计数器错误: " + (gpu.CounterError.Length > 0 ? gpu.CounterError : "(无)"));
            gpu.Dispose();
            w("");

            w("--- 帧统计算法自证（1% Low） ---");
            RunLowFpsChecks(w);
            w("");

            w("--- 前台窗口 ---");
            int fg = ForegroundWatcher.CurrentPid;
            w("PID: " + fg + "  进程: " + ForegroundWatcher.ProcessNameOf(fg));
            w("");

            w("--- PresentMon 试运行 (2s) ---");
            if (!File.Exists(pm))
            {
                w("跳过：文件不存在");
            }
            else
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = pm,
                        // 会话名必须和引擎一致并带 --stop_existing_session：
                        // 否则自检自己留下的会话会占住 DXGI provider，
                        // 之后连正片采集都会「进程活着但一帧收不到」。
                        Arguments = "--output_stdout --no_console_stats --v1_metrics "
                                  + "--session_name PerfOverlayTrace --stop_existing_session "
                                  + "--exclude PerfOverlay.exe --timed 2",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        WorkingDirectory = Path.GetDirectoryName(pm)
                    };
                    var p = new Process { StartInfo = psi };
                    var outSb = new StringBuilder();
                    var errSb = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null && outSb.Length < 1500) outSb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null && errSb.Length < 1500) errSb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(6000)) { try { p.Kill(); } catch (Exception) { } }
                    Thread.Sleep(300);

                    w("stdout 前 5 行:");
                    var lines = outSb.ToString().Split('\n');
                    for (int i = 0; i < Math.Min(5, lines.Length); i++)
                        w("  | " + lines[i].TrimEnd());
                    w("stderr:");
                    var elines = errSb.ToString().Split('\n');
                    for (int i = 0; i < Math.Min(4, elines.Length); i++)
                        w("  | " + elines[i].TrimEnd());
                }
                catch (Exception ex)
                {
                    w("异常: " + ex.Message);
                }
            }
            w("");
            w("=== 自检结束 ===");

            string path = Path.Combine(Path.GetTempPath(), "perfoverlay-selftest.txt");
            try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch (Exception) { }
        }
    }
}
