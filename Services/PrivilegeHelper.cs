using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 权限诊断。「Performance Log Users」组（SID S-1-5-32-559）成员可以非提权采集 ETW 会话，
    /// 这是唯一能让「开机自启 + 有帧数」两件事同时成立的方案（管理员方案每次开机都会弹 UAC）。
    /// </summary>
    public static class PrivilegeHelper
    {
        public const string PerfLogUsersSid = "S-1-5-32-559";

        // 进程的登录令牌在生命周期内不会变（提权是另起一个进程），
        // 所以这两项查一次就够。它们原先每次访问都 GetCurrent() + 遍历组，
        // 而采样循环每 500 毫秒要问两三次 —— 纯属白烧 CPU。
        private static readonly Lazy<bool> Elevated = new Lazy<bool>(DetectElevated);
        private static readonly Lazy<bool> InPerfLogUsers = new Lazy<bool>(DetectInPerformanceLogUsers);

        public static bool IsElevated { get { return Elevated.Value; } }

        public static bool IsInPerformanceLogUsers { get { return InPerfLogUsers.Value; } }

        private static bool DetectElevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception) { return false; }
        }

        private static bool DetectInPerformanceLogUsers()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    if (id.Groups == null) return false;
                    foreach (var g in id.Groups)
                    {
                        try
                        {
                            if (g.Value == PerfLogUsersSid) return true;
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>当前会话是否具备 ETW 采集条件。</summary>
        public static bool CanCaptureFrames
        {
            get { return IsElevated || IsInPerformanceLogUsers; }
        }

        public static string DescribeCapability()
        {
            if (IsElevated) return "管理员权限：可采集帧数";
            if (IsInPerformanceLogUsers) return "性能日志用户组：可采集帧数";
            return "缺少 ETW 权限：帧数不可用";
        }

        /// <summary>
        /// 以管理员身份重启自身（会弹一次 UAC）。
        /// 返回 null 表示已成功拉起提权进程；否则返回给用户看的错误原因。
        /// 注意 UseShellExecute 必须为 true，否则 Verb="runas" 被忽略、UAC 根本不弹。
        /// </summary>
        public static string RestartElevated(string args)
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args ?? "",
                    UseShellExecute = true,
                    Verb = "runas",
                    // 提权进程的默认工作目录是 C:\Windows\System32，显式指定避免相对路径踩坑
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                Process.Start(psi);
                return null;
            }
            catch (System.ComponentModel.Win32Exception wex)
            {
                // 1223 = ERROR_CANCELLED，用户在 UAC 对话框里点了「否」
                if (wex.NativeErrorCode == 1223) return "已取消（UAC 未授权）";
                return "启动失败：" + wex.Message + "（错误码 " + wex.NativeErrorCode + "）";
            }
            catch (Exception ex)
            {
                return "启动失败：" + ex.Message;
            }
        }
    }

    /// <summary>前台窗口跟随：帧数只统计当前前台进程的 present。</summary>
    public static class ForegroundWatcher
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static IntPtr Handle
        {
            get { return GetForegroundWindow(); }
        }

        public static int CurrentPid
        {
            get
            {
                try
                {
                    var h = GetForegroundWindow();
                    if (h == IntPtr.Zero) return 0;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    return (int)pid;
                }
                catch (Exception) { return 0; }
            }
        }

        // PID → 进程名的小缓存。采样循环每个 tick 都会问一次「前台进程叫什么」，
        // 而前台进程通常几百毫秒甚至几分钟才换一次，没必要每次都去开进程句柄。
        // 只在采样线程访问，单槽缓存足够。
        private static int _cachedPid = -1;
        private static string _cachedName = "";

        public static string ProcessNameOf(int pid)
        {
            if (pid <= 0) return "";
            if (pid == _cachedPid) return _cachedName;
            try
            {
                string name;
                using (var p = Process.GetProcessById(pid)) name = p.ProcessName;
                _cachedPid = pid;
                _cachedName = name;
                return name;
            }
            catch (Exception)
            {
                _cachedPid = pid;
                _cachedName = "";
                return "";
            }
        }

        public static string WindowTitleOf(IntPtr hWnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                int n = GetWindowTextW(hWnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : "";
            }
            catch (Exception) { return ""; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    }

    /// <summary>开机启动：HKCU\...\Run，无需管理员，不改系统服务。</summary>
    public static class StartupHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PerfOverlay";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                    {
                        if (k == null) return false;
                        var v = k.GetValue(ValueName) as string;
                        return !string.IsNullOrEmpty(v);
                    }
                }
                catch (Exception) { return false; }
            }
        }

        public static bool Enable()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    k.SetValue(ValueName, "\"" + exe + "\" --startup", Microsoft.Win32.RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        public static bool Disable()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        public static bool Apply(bool enabled)
        {
            return enabled ? Enable() : Disable();
        }
    }
}
