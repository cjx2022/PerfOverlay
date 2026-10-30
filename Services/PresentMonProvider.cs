using System;
using System.IO;
using System.Reflection;

namespace PerfOverlay.Services
{
    /// <summary>
    /// PresentMon.exe 的取用策略。
    ///
    /// 发布物是**单个 exe**，所以 PresentMon 以嵌入资源的形式打进程序集，
    /// 首次运行时释放到 %LOCALAPPDATA%\PerfOverlay\bin\&lt;版本&gt;\PresentMon.exe。
    /// 用版本号做子目录：升级程序后自然换到新目录，不会用到旧版本的旧二进制。
    ///
    /// 如果程序目录下已有 tools\PresentMon.exe（例如用户想自己换一个更新版本），
    /// 优先用那个 —— 这样不必重新编译程序。
    /// </summary>
    public static class PresentMonProvider
    {
        private const string ResourceName = "PerfOverlay.PresentMon.exe";
        private const string ExeName = "PresentMon.exe";

        private static string _resolved;

        /// <summary>解析（按需释放）出可用的 PresentMon 路径。失败时返回程序目录下的候选路径。</summary>
        public static string Resolve()
        {
            if (_resolved != null) return _resolved;

            string sidecar = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools"), ExeName);
            if (File.Exists(sidecar)) return _resolved = sidecar;

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var probe = asm.GetManifestResourceStream(ResourceName))
                {
                    if (probe == null) return _resolved = sidecar;

                    long length = probe.Length;
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PerfOverlay", "bin", VersionTag(asm));
                    string target = Path.Combine(dir, ExeName);

                    // 已经释放过（长度一致）就不再写盘：正常启动只是两次 IO 判断
                    var fi = new FileInfo(target);
                    if (fi.Exists && fi.Length == length) return _resolved = target;

                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    string tmp = target + ".tmp";
                    using (var dst = File.Create(tmp)) probe.CopyTo(dst);
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);

                    return _resolved = target;
                }
            }
            catch (Exception)
            {
                // 释放失败不该让程序起不来：回落成「找不到 PresentMon」，
                // 帧数显示 N/A，其余指标照常工作。
                return _resolved = sidecar;
            }
        }

        private static string VersionTag(Assembly asm)
        {
            try
            {
                var v = asm.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
            catch (Exception) { return "0.0.0"; }
        }
    }
}
