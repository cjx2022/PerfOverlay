using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PerfOverlay.Services
{
    /// <summary>
    /// CSV 回放校验：拿一段「已知答案」的 PresentMon CSV 喂进真正的解析管线，
    /// 再比对 FPS / 1% Low。
    ///
    /// 为什么要有它：ETW 采集需要管理员权限，机器上没有权限时整条链只能显示 N/A，
    /// 那样等于「没法验证」。回放把解析、多链去重、缺列回填、1% Low 聚合全部覆盖到，
    /// 只剩「ETW 能不能取到数据」这一个未知项，而不是把七八个环节一起赌。
    ///
    /// 文件头约定（前两行，可只写第一行）：
    ///   # expect fps=60.0 low=10.0 frames=600 pid=4321
    ///   # desc 说明文字
    /// </summary>
    public static class CsvReplay
    {
        public static string Run(string path)
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;

            string[] files;
            if (Directory.Exists(path))
                files = Directory.GetFiles(path, "*.csv");
            else if (File.Exists(path))
                files = new[] { path };
            else
            {
                return "回放路径不存在: " + path;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            sb.AppendLine("=== CSV 回放校验 ===");
            sb.AppendLine("目录: " + path);
            sb.AppendLine("");

            foreach (var f in files)
            {
                string name = Path.GetFileName(f);
                double expFps = -1, expLow = -1;
                int expFrames = -1;
                string desc = "";

                var lines = File.ReadAllLines(f, Encoding.UTF8);
                int start = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    var ln = lines[i].Trim();
                    if (!ln.StartsWith("#", StringComparison.Ordinal)) { start = i; break; }
                    start = i + 1;
                    if (ln.StartsWith("# expect", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseExpect(ln, "fps", ref expFps);
                        ParseExpect(ln, "low", ref expLow);
                        int fr;
                        if (TryGetInt(ln, "frames", out fr)) expFrames = fr;
                    }
                    else if (ln.StartsWith("# desc", StringComparison.OrdinalIgnoreCase))
                    {
                        desc = ln.Substring(6).Trim();
                    }
                }

                var engine = new PresentMonEngine("__none__");
                for (int i = start; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    engine.FeedLine(lines[i]);
                }

                int pid = engine.LastSeenPid;
                engine.SetTarget(pid, "replay");
                var snap = engine.Snapshot();

                sb.AppendLine("-- " + name + (desc.Length > 0 ? "  (" + desc + ")" : ""));
                sb.AppendLine(string.Format("   数据: 帧数={0}  available={1}  msg={2}",
                    snap.FrameCount, snap.Available, snap.Message.Length > 0 ? snap.Message : "-"));
                sb.AppendLine(string.Format("   结果: FPS={0:0.00}  LOW={1:0.00}", snap.Fps, snap.LowFps));

                bool okAll = true;
                if (expFrames > 0)
                {
                    bool ok = snap.FrameCount == expFrames;
                    if (!ok) okAll = false;
                    sb.AppendLine(string.Format("   [{0}] 帧数 实际={1} 期望={2}", ok ? "通过" : "失败", snap.FrameCount, expFrames));
                }
                if (expFps >= 0)
                {
                    bool ok = Near(snap.Fps, expFps);
                    if (!ok) okAll = false;
                    sb.AppendLine(string.Format("   [{0}] FPS  实际={1:0.00} 期望={2:0.00}", ok ? "通过" : "失败", snap.Fps, expFps));
                }
                if (expLow >= 0)
                {
                    bool ok = Near(snap.LowFps, expLow);
                    if (!ok) okAll = false;
                    sb.AppendLine(string.Format("   [{0}] LOW  实际={1:0.00} 期望={2:0.00}", ok ? "通过" : "失败", snap.LowFps, expLow));
                }

                if (okAll) pass++; else fail++;
                sb.AppendLine();
                engine.Dispose();
            }

            sb.AppendLine(string.Format("SUMMARY: {0} 个场景通过，{1} 个失败", pass, fail));
            sb.AppendLine(fail == 0 ? "SUMMARY: ALL PASS" : "SUMMARY: FAIL");
            return sb.ToString();
        }

        /// <summary>容差 1%（或者 0.6fps，取大的那个）——帧率算到小数点后两位本来就有抖动。</summary>
        private static bool Near(double actual, double expect)
        {
            if (expect <= 0) return Math.Abs(actual) < 0.6;
            return Math.Abs(actual - expect) <= Math.Max(0.6, expect * 0.01);
        }

        private static void ParseExpect(string line, string key, ref double dst)
        {
            int at = line.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return;
            int p = at + key.Length + 1;
            int e = p;
            while (e < line.Length && (char.IsDigit(line[e]) || line[e] == '.' || line[e] == '-' || line[e] == '+')) e++;
            double v;
            if (double.TryParse(line.Substring(p, e - p), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                dst = v;
        }

        private static bool TryGetInt(string line, string key, out int value)
        {
            value = 0;
            int at = line.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            int p = at + key.Length + 1;
            int e = p;
            while (e < line.Length && char.IsDigit(line[e])) e++;
            return int.TryParse(line.Substring(p, e - p), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}
