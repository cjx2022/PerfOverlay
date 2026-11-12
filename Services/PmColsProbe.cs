using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PerfOverlay.Services
{
    /// <summary>
    /// --pm-cols 探针：直接看 PresentMon 的原始 CSV，回答「延迟列到底有没有值」。
    ///
    /// 为什么需要它：浮窗显示 `--` 时，可能是①没开开关、②表头里没这一列、
    /// ③这一列恒为 0（渲染路径追踪不到显示环节）。前两个看一眼就知道，
    /// 第三个必须拿原始数据统计 —— 光看 UI 永远分不清「没有数据」和「数据是 0」。
    /// </summary>
    internal static class PmColsProbe
    {
        public static int Run(int seconds)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PerfOverlay PresentMon 列探针");
            sb.AppendLine();

            string pm = MonitorHost.PresentMonPath();
            sb.AppendLine("  采集器: " + pm);
            if (!File.Exists(pm))
            {
                sb.AppendLine("  ✗ 不存在");
                return Write(sb, 2);
            }

            string session = "PerfOverlayCols_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var psi = new ProcessStartInfo
            {
                FileName = pm,
                Arguments = "--output_stdout --no_console_stats --v1_metrics --session_name " + session,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(pm)
            };

            var lines = new List<string>(4096);
            var err = new StringBuilder();
            Process p = null;
            try
            {
                p = Process.Start(psi);
                if (p == null) { sb.AppendLine("  ✗ 启动失败"); return Write(sb, 2); }

                var t = new Thread(() =>
                {
                    try
                    {
                        string l;
                        while ((l = p.StandardOutput.ReadLine()) != null)
                        {
                            lock (lines) { if (lines.Count < 200000) lines.Add(l); }
                        }
                    }
                    catch (Exception) { }
                });
                t.IsBackground = true;
                t.Start();

                var te = new Thread(() =>
                {
                    try
                    {
                        string l;
                        while ((l = p.StandardError.ReadLine()) != null)
                            lock (err) { if (err.Length < 600) err.AppendLine(l); }
                    }
                    catch (Exception) { }
                });
                te.IsBackground = true;
                te.Start();

                Thread.Sleep(Math.Max(3, seconds) * 1000);
            }
            catch (Exception ex)
            {
                sb.AppendLine("  ✗ 异常: " + ex.Message);
                return Write(sb, 2);
            }
            finally
            {
                try { if (p != null && !p.HasExited) { p.Kill(); p.WaitForExit(1500); } } catch (Exception) { }
            }

            List<string> snap;
            lock (lines) { snap = new List<string>(lines); }

            sb.AppendLine("  读到行数: " + snap.Count);
            string e0;
            lock (err) { e0 = err.ToString().Trim(); }
            if (e0.Length > 0) sb.AppendLine("  采集器 stderr: " + e0.Replace("\n", " | "));
            sb.AppendLine();

            if (snap.Count == 0)
            {
                sb.AppendLine("✗ 一行都没有 —— 采集器没出数据（提权、会话、反作弊）");
                return Write(sb, 2);
            }

            var hdr = snap[0].Split(',');
            for (int i = 0; i < hdr.Length; i++) hdr[i] = hdr[i].Trim();
            int ia = Array.IndexOf(hdr, "Application");
            int ip = Array.IndexOf(hdr, "ProcessID");
            int id = Array.IndexOf(hdr, "msUntilDisplayed");
            int ib = Array.IndexOf(hdr, "msBetweenPresents");
            int im = Array.IndexOf(hdr, "msBetweenDisplayChange");
            int ir = Array.IndexOf(hdr, "msUntilRenderComplete");
            int ic = Array.IndexOf(hdr, "msInPresentAPI");

            sb.AppendLine("  列数: " + hdr.Length);
            sb.AppendLine("  Application=" + ia + "  ProcessID=" + ip
                          + "  msUntilDisplayed=" + id
                          + "  msBetweenPresents=" + ib
                          + "  msBetweenDisplayChange=" + im
                          + "  msUntilRenderComplete=" + ir
                          + "  msInPresentAPI=" + ic);
            sb.AppendLine();

            if (id < 0)
            {
                sb.AppendLine("✗ 表头里根本没有 msUntilDisplayed —— 采集器版本或参数不对");
                return Write(sb, 2);
            }

            // 按进程统计：总帧 / 延迟非 0 帧 / 延迟均值
            var total = new Dictionary<string, int>();
            var withDelay = new Dictionary<string, int>();
            var sumDelay = new Dictionary<string, double>();
            int rows = 0, nonzero = 0;
            double dmax = 0, dsum = 0;
            string sample = "";

            for (int i = 1; i < snap.Count; i++)
            {
                string l = snap[i];
                if (l.Length == 0) continue;
                var f = l.Split(',');
                if (f.Length <= Math.Max(id, Math.Max(ia, ip))) continue;
                rows++;
                string app = ia >= 0 ? f[ia] : "?";
                if (!total.ContainsKey(app)) { total[app] = 0; withDelay[app] = 0; sumDelay[app] = 0; }
                total[app]++;

                double d;
                if (!double.TryParse(f[id].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) continue;
                if (d > 0 && d < 10000)
                {
                    nonzero++;
                    withDelay[app]++;
                    sumDelay[app] += d;
                    dsum += d;
                    if (d > dmax) dmax = d;
                    if (sample.Length == 0) sample = l;
                }
            }

            sb.AppendLine("  数据行: " + rows + "   延迟非 0: " + nonzero
                          + "（" + (rows > 0 ? (100.0 * nonzero / rows).ToString("0.0") : "0") + "%）");
            if (nonzero > 0)
                sb.AppendLine("  延迟均值: " + (dsum / nonzero).ToString("0.00") + " ms   最大: " + dmax.ToString("0.00"));
            sb.AppendLine();

            // 出屏延迟拿不到时，看还有哪些列有值 —— 决定「能不能给个诚实的替代口径」。
            sb.AppendLine("  ---- 其余时间列的有效样本 ----");
            AppendCol(sb, snap, hdr, "msUntilRenderComplete", ir);
            AppendCol(sb, snap, hdr, "msInPresentAPI", ic);
            AppendCol(sb, snap, hdr, "msBetweenDisplayChange", im);
            AppendCol(sb, snap, hdr, "msBetweenPresents", ib);
            sb.AppendLine();

            sb.AppendLine("  ---- 各进程（帧数 / 有延迟帧数 / 均值）----");
            var apps = new List<KeyValuePair<string, int>>(total);
            apps.Sort((a, b) => b.Value.CompareTo(a.Value));
            int shown = 0;
            foreach (var kv in apps)
            {
                if (shown++ >= 12) break;
                int wd = withDelay[kv.Key];
                string avg = wd > 0 ? (sumDelay[kv.Key] / wd).ToString("0.00") : "--";
                sb.AppendLine(string.Format("    {0,-24} {1,7} / {2,7} / {3}", kv.Key, kv.Value, wd, avg));
            }
            sb.AppendLine();

            if (sample.Length > 0)
            {
                sb.AppendLine("  首个有延迟的原始行:");
                sb.AppendLine("    " + sample);
                sb.AppendLine("  表头:");
                sb.AppendLine("    " + snap[0]);
            }

            return Write(sb, nonzero > 0 ? 0 : 2);
        }

        private static void AppendCol(StringBuilder sb, List<string> snap, string[] hdr, string name, int idx)
        {
            if (idx < 0) { sb.AppendLine("    " + name + ": 表头里没有"); return; }
            int n = 0, valid = 0;
            double sum = 0, max = 0;
            for (int i = 1; i < snap.Count; i++)
            {
                var f = snap[i].Split(',');
                if (f.Length <= idx) continue;
                n++;
                double v;
                if (!double.TryParse(f[idx].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                if (v > 0 && v < 10000) { valid++; sum += v; if (v > max) max = v; }
            }
            string avg = valid > 0 ? (sum / valid).ToString("0.00") : "--";
            sb.AppendLine(string.Format("    {0,-24} 有效 {1}/{2}（{3}）  均值 {4}  最大 {5:0.00}",
                name, valid, n,
                n > 0 ? (100.0 * valid / n).ToString("0.0") + "%" : "0%", avg, max));
        }

        private static int Write(StringBuilder sb, int code)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "perfoverlay-pm-cols.txt");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
            return code;
        }
    }
}
