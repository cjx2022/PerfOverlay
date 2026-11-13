using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PerfOverlay.Services
{
    /// <summary>
    /// ETW 残留清理。
    ///
    /// 实时会话（PresentMon 开的那种）在被强杀时——进程崩溃、任务管理器结束进程、
    /// 直接断电——内核不会回收 %SystemRoot%\System32\LogFiles\WMI\RtBackup 下的备份
    /// 文件。这些文件没有任何界面能看到，只会一直攒，实测能到 GB 级把 C 盘吃掉，
    /// 而表现却是「帧数一直 N/A」：会话建立成功，事件却写不进配额已满的备份文件。
    ///
    /// 权限这一环是本机实测出来的坑：**即使进程已经是管理员，枚举 RtBackup 依然被拒**
    /// （默认 DACL 只给 SYSTEM / TrustedInstaller）。所以三级降级：
    ///   1) 直接干（有些系统 Administrators 本来就有权限）；
    ///   2) 给 Administrators 补一条完全控制 ACE，再干；
    ///   3) 还不行就建一个一次性 SYSTEM 计划任务，让 exe 自己以 SYSTEM 跑一遍，
    ///      跑完立刻删任务。
    /// 第 2 步只是往 DACL 里加一条 ACE，不删原有条目；管理员本来就能改系统目录 ACL，
    /// 所以安全边界不变。
    /// </summary>
    internal static class EtwCleaner
    {
        /// <summary>RtBackup 目录。系统保护目录，管理员也可能读不了。</summary>
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\LogFiles\WMI\RtBackup");

        /// <summary>
        /// 本程序的 ETW 会话前缀。
        ///
        /// 注意这里**没有**尾部下划线：主程序的会话叫 `PerfOverlay_xxxxxxxxxx`，
        /// 而 `--pm-cols` 探针起的叫 `PerfOverlayCols_xxxxxxxx`。早先写成
        /// `"PerfOverlay_"` 时前缀匹配不上探针那条，探针强杀 PresentMon 留下的会话
        /// 就永远清不掉 —— 它一直占着 DXGI 的事件通道，主程序之后无论重建多少次
        /// 会话都只能读到 0 行，表现为「帧数永久 N/A、延迟一直 --」。
        /// </summary>
        public const string SessionPrefix = "PerfOverlay";

        /// <summary>自动兜底的触发线，低于这个体量不值得动系统目录。</summary>
        public const long AutoThresholdBytes = 256L * 1024 * 1024;

        public const string TaskName = "PerfOverlayEtwClean";
        public const string SystemFlag = "--clean-etw-system";
        public const string ReportFlag = "--etw-report";

        public struct Result
        {
            public bool Ok;
            public string Note;
            public int FilesBefore;
            public long BytesBefore;
            public int FilesDeleted;
            public long BytesDeleted;
            public int FilesRemaining;
            public long BytesRemaining;
            public List<string> SessionsStopped;
            public List<string> Locked;
            public bool AdminAclGranted;
            public bool UsedSystemTask;
            /// <summary>SYSTEM 任务跑出来的现成文本报告，非空时优先于 Format。</summary>
            public string RawReport;
        }

        public static string Format(Result r)
        {
            if (!string.IsNullOrEmpty(r.RawReport)) return r.RawReport;

            var sb = new StringBuilder();
            sb.AppendLine("ETW residue cleanup");
            sb.AppendLine("dir=" + Dir);
            sb.AppendLine("admin=" + PrivilegeHelper.IsElevated);
            if (r.AdminAclGranted) sb.AppendLine("note=已为 Administrators 补充完全控制 ACE");
            if (r.UsedSystemTask) sb.AppendLine("note=以 SYSTEM 计划任务执行（管理员权限不足以访问该目录）");
            if (!string.IsNullOrEmpty(r.Note)) sb.AppendLine("note=" + r.Note);
            sb.AppendLine(string.Format("before  files={0}  size={1:0.0} MB", r.FilesBefore, r.BytesBefore / 1048576.0));
            sb.AppendLine(string.Format("deleted files={0}  size={1:0.0} MB", r.FilesDeleted, r.BytesDeleted / 1048576.0));
            sb.AppendLine(string.Format("remain  files={0}  size={1:0.0} MB", r.FilesRemaining, r.BytesRemaining / 1048576.0));
            if (r.SessionsStopped != null)
            {
                sb.AppendLine(string.Format("sessions stopped={0}{1}", r.SessionsStopped.Count,
                    r.SessionsStopped.Count > 0 ? "  [" + string.Join(", ", r.SessionsStopped.ToArray()) + "]" : ""));
            }
            if (r.Locked != null && r.Locked.Count > 0)
            {
                sb.AppendLine(string.Format("locked (held by live sessions)={0}", r.Locked.Count));
                int n = Math.Min(5, r.Locked.Count);
                for (int i = 0; i < n; i++) sb.AppendLine("  " + r.Locked[i]);
            }
            sb.AppendLine(r.Ok ? "RESULT: OK" : "RESULT: FAIL");
            return sb.ToString();
        }

        public static void Stat(out int files, out long bytes, out string err)
        {
            files = 0; bytes = 0; err = null;
            try
            {
                var di = new DirectoryInfo(Dir);
                if (!di.Exists) { err = "目录不存在"; return; }
                foreach (var f in di.GetFiles())
                {
                    files++;
                    bytes += f.Length;
                }
            }
            catch (Exception ex)
            {
                files = -1; bytes = -1; err = ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>
        /// 对外入口：能用当前的权限直接干就直接干，不行逐级升级。
        /// stopSessions=true 时先停本程序前缀的残留会话（只能在采集未启动时用）。
        /// </summary>
        public static Result Run(bool stopSessions)
        {
            int files; long bytes; string err;
            Stat(out files, out bytes, out err);
            if (err == null) return CleanDirect(stopSessions, false);

            // 第 2 级：补 ACE
            if (TryGrantAdmins())
            {
                Stat(out files, out bytes, out err);
                if (err == null) return CleanDirect(stopSessions, true);
            }

            // 第 3 级：一次性 SYSTEM 任务
            return RunViaSystemTask();
        }

        /// <summary>给 Administrators 补一条完全控制 ACE（只加不删）。</summary>
        private static bool TryGrantAdmins()
        {
            try
            {
                var di = new DirectoryInfo(Dir);
                DirectorySecurity sec = di.GetAccessControl();
                var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                sec.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                di.SetAccessControl(sec);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>有权限时的实际清理。</summary>
        public static Result CleanDirect(bool stopSessions, bool aclGranted)
        {
            var r = new Result
            {
                SessionsStopped = new List<string>(),
                Locked = new List<string>(),
                AdminAclGranted = aclGranted
            };

            int files; long bytes; string err;
            Stat(out files, out bytes, out err);
            if (err != null)
            {
                r.Note = err;
                return r;
            }
            r.FilesBefore = files;
            r.BytesBefore = bytes;

            if (stopSessions) StopResidualSessions(r.SessionsStopped);
            if (r.SessionsStopped.Count > 0) Thread.Sleep(600);

            FileInfo[] list;
            try { list = new DirectoryInfo(Dir).GetFiles(); }
            catch (Exception ex) { r.Note = "枚举失败：" + ex.Message; return r; }

            foreach (var f in list)
            {
                try
                {
                    long len = f.Length;
                    f.Delete();
                    r.FilesDeleted++;
                    r.BytesDeleted += len;
                }
                catch (Exception)
                {
                    r.Locked.Add(f.Name);
                }
            }

            Stat(out files, out bytes, out err);
            r.FilesRemaining = files < 0 ? 0 : files;
            r.BytesRemaining = bytes < 0 ? 0 : bytes;
            r.Ok = r.FilesDeleted > 0 || r.BytesRemaining < AutoThresholdBytes;
            return r;
        }

        /// <summary>
        /// 最后手段：建一次性 SYSTEM 任务，让本 exe 以 SYSTEM 跑一遍 --clean-etw-system。
        /// 报告由 SYSTEM 写到 %SystemRoot%\Temp，管理员可读；跑完立刻删任务和包装脚本。
        /// </summary>
        private static Result RunViaSystemTask()
        {
            var r = new Result { UsedSystemTask = true, SessionsStopped = new List<string>(), Locked = new List<string>() };

            string winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            string report = Path.Combine(winTemp, "perfoverlay-etw-report.txt");
            string wrapper = Path.Combine(winTemp, "perfoverlay-etw-run.cmd");

            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                try { if (File.Exists(report)) File.Delete(report); } catch (Exception) { }

                string cmd = "@echo off\r\n\""
                             + exe + "\" " + SystemFlag + " \"" + report + "\"\r\n";
                File.WriteAllText(wrapper, cmd, Encoding.Default);

                RunCapture("schtasks", "/create /tn " + TaskName
                                        + " /tr \"cmd /c " + wrapper + "\""
                                        + " /ru SYSTEM /rl HIGHEST /sc ONCE /st 23:59 /f", 15000);
                RunCapture("schtasks", "/run /tn " + TaskName, 10000);

                string text = null;
                for (int i = 0; i < 60 && text == null; i++)
                {
                    Thread.Sleep(300);
                    try
                    {
                        if (File.Exists(report))
                        {
                            using (var fs = new FileStream(report, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (var sr = new StreamReader(fs, Encoding.UTF8))
                                text = sr.ReadToEnd();
                        }
                    }
                    catch (Exception) { }
                }

                RunCapture("schtasks", "/delete /tn " + TaskName + " /f", 10000);

                if (!string.IsNullOrEmpty(text))
                {
                    r.RawReport = text;
                    r.Ok = text.IndexOf("RESULT: OK", StringComparison.Ordinal) >= 0;
                }
                else
                {
                    r.Note = "SYSTEM 任务未产出报告（可能被组策略禁止创建计划任务）";
                }
            }
            catch (Exception ex)
            {
                r.Note = "SYSTEM 任务失败：" + ex.Message;
            }
            finally
            {
                try { if (File.Exists(wrapper)) File.Delete(wrapper); } catch (Exception) { }
                try { if (File.Exists(report)) File.Delete(report); } catch (Exception) { }
            }
            return r;
        }

        /// <summary>从 logman 的会话列表里挑出本程序前缀的会话并 stop。</summary>
        public static void StopResidualSessions(List<string> stopped)
        {
            string text = RunCapture("logman", "query -ets", 8000);
            if (string.IsNullOrEmpty(text)) return;

            var names = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0 || line[0] == '-') continue;
                int sp = line.IndexOfAny(new[] { ' ', '\t' });
                string name = sp > 0 ? line.Substring(0, sp) : line;
                if (!name.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!names.Contains(name)) names.Add(name);
            }

            foreach (var name in names)
            {
                string outp = RunCapture("logman", "stop \"" + name + "\" -ets", 8000);
                if (!string.IsNullOrEmpty(outp)
                    && outp.IndexOf("找不到", StringComparison.Ordinal) < 0
                    && outp.IndexOf("错误", StringComparison.Ordinal) < 0)
                {
                    stopped.Add(name);
                }
            }
        }

        private static string RunCapture(string file, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    // logman / schtasks 在中文系统上是 GBK 输出，按系统默认编码解
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default
                };
                using (var p = Process.Start(psi))
                {
                    string outText = p.StandardOutput.ReadToEnd();
                    string errText = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        return null;
                    }
                    return outText + errText;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 启动后的自动兜底：延迟执行。
        /// 会话级清理每次都做（很轻，且能防止备份文件继续长大）；
        /// 文件级只在目录可访问且超过阈值时做，绝不为了清理去建计划任务。
        /// </summary>
        public static void AutoRunInBackground(Action<string> log)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Thread.Sleep(25000);
                    if (!PrivilegeHelper.IsElevated) return;

                    var stopped = new List<string>();
                    StopResidualSessions(stopped);
                    if (stopped.Count > 0 && log != null)
                        log("启动兜底：停掉残留 ETW 会话 " + stopped.Count + " 个 [" + string.Join(", ", stopped.ToArray()) + "]");

                    int files; long bytes; string err;
                    Stat(out files, out bytes, out err);
                    if (err != null || bytes < AutoThresholdBytes) return;

                    var r = CleanDirect(false, false);
                    if (log != null) log(Format(r).Replace(Environment.NewLine, " | "));
                }
                catch (Exception ex)
                {
                    if (log != null) log("自动清理异常：" + ex.Message);
                }
            });
        }
    }
}
