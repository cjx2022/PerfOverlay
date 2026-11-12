using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 显示延迟自检：自己开一个持续出帧的小窗口，让 PresentMon 采它，
    /// 看 msUntilDisplayed 这一列到底有没有值。
    ///
    /// 为什么要自建窗口：延迟这一列不是「有帧就一定给」——独立翻转、覆盖层、
    /// Electron 这类路径整段都是 0。用一个已知会走普通窗口合成路径的窗口当靶子，
    /// 才能把「我们的代码不读」和「PresentMon 没给」区分开。
    ///
    /// 窗口放在屏幕外侧：不打扰用户，同时仍然是 IsVisible=true 的正常窗口
    /// （DWM 仍会合成，所以 Present 事件照常产生）。
    /// </summary>
    public static class LatencyProbe
    {
        public static int Run(int seconds)
        {
            if (seconds < 6) seconds = 6;
            var sb = new StringBuilder();
            sb.AppendLine("PerfOverlay latency probe");
            sb.AppendLine();

            int fails = 0;
            if (!PrivilegeHelper.IsElevated)
            {
                sb.AppendLine("   ✗ 需要管理员权限才能开 ETW 会话（当前是普通权限）");
                sb.AppendLine();
                sb.AppendLine("RESULT: FAIL (1)");
                Emit(sb);
                return 1;
            }

            string exe = MonitorHost.PresentMonPath();
            sb.AppendLine("   PresentMon: " + (exe ?? "(未找到)"));
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                sb.AppendLine("   ✗ 缺少 PresentMon.exe");
                sb.AppendLine();
                sb.AppendLine("RESULT: FAIL (1)");
                Emit(sb);
                return 1;
            }

            // ── 靶子窗口：一个不停换色的矩形，走普通窗口合成 ──
            // 必须落在屏幕可见区域内：完全在屏幕外的窗口 DWM 会跳过合成，
            // 于是根本没有 present 事件 —— 实测就是这个原因导致一帧都采不到。
            // 尺寸压到最小、不抢焦点、不置顶，12 秒后自己关。
            var area = SystemParameters.WorkArea;
            var win = new Window
            {
                Width = 160,
                Height = 100,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = false,
                Left = area.Right - 168,
                Top = area.Bottom - 108,
                Background = Brushes.Black
            };
            win.Show();
            double phase = 0;
            EventHandler onRendering = (s, e) =>
            {
                phase += 0.08;
                byte g = (byte)(128 + 100 * Math.Sin(phase));
                win.Background = new SolidColorBrush(Color.FromRgb((byte)(phase * 20 % 255), g, 200));
            };
            CompositionTarget.Rendering += onRendering;

            var engine = new PresentMonEngine(exe, excludeSelf: false);
            engine.CaptureLatency = true;
            bool started = engine.Start();
            sb.AppendLine("   引擎启动: " + (started ? "OK" : "✗ " + engine.LastError));
            if (!started) fails++;

            int selfPid = Process.GetCurrentProcess().Id;
            engine.SetTarget(selfPid, "PerfOverlay");

            // 泵消息等 seconds 秒：PushFrame 期间动画照跑，Present 事件才会持续产生
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(seconds)
            };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);

            var snap = engine.Snapshot();

            sb.AppendLine(string.Format("  自检进程 pid={0}  目标 pid={1}  读到的行数={2}",
                selfPid, engine.TargetPid, engine.LinesSeen));
            sb.AppendLine("  采集器报错: " + (engine.LastError.Length > 0 ? engine.LastError : "(无)"));
            sb.AppendLine("  队列: " + engine.QueueStat.Replace(Environment.NewLine, " | "));
            sb.AppendLine(string.Format("  自我采集: FPS {0:0.0}  帧数 {1}  目标 {2}",
                snap.Fps, snap.FrameCount, snap.TargetProcess));
            sb.AppendLine(string.Format("  延迟列: 样本 {0}  P50 {1:0.00} ms  P95 {2:0.00} ms  状态 {3}",
                snap.LatencySamples, snap.LatencyP50Ms, snap.LatencyP95Ms,
                snap.LatencyOk ? "OK" : (snap.LatencyMessage.Length > 0 ? snap.LatencyMessage : "无数据")));

            if (!snap.Available || snap.FrameCount <= 0)
            {
                sb.AppendLine("   ✗ 一帧都没采到 —— 采集链路本身有问题，先查帧数");
                fails++;
            }
            else if (!snap.LatencyOk)
            {
                sb.AppendLine("   ✗ 有帧但拿不到显示延迟 —— 这条渲染路径追踪不到显示环节");
                fails++;
            }

            engine.Stop();
            engine.Dispose();
            CompositionTarget.Rendering -= onRendering;
            try { win.Close(); } catch (Exception) { }

            sb.AppendLine();
            sb.AppendLine(fails == 0 ? "RESULT: OK" : "RESULT: FAIL (" + fails + ")");
            Emit(sb);
            return fails == 0 ? 0 : 1;
        }

        private static void Emit(StringBuilder sb)
        {
            string path = Path.Combine(Path.GetTempPath(), "perfoverlay-latency.txt");
            try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch (Exception) { }
            Console.WriteLine(sb.ToString());
        }
    }
}
