using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace PerfOverlay.Services
{
    /// <summary>
    /// PawnIO 的安装引导：检测、下载、静默安装。
    ///
    /// 这里刻意不做「自己带驱动」：新建的内核驱动必须由微软签名（EV 证书 + Partner Center），
    /// 顺带还要维护每代 CPU 的寄存器偏移表。PawnIO 把这两件事都解决了 ——
    /// 微软签名、HVCI 兼容、不在易受攻击驱动阻止列表里，官方还明确写了
    /// 「This installer can be redistributed unmodified」，所以我们只负责引导用户装一次。
    ///
    /// 三个硬事实（都在安装包/INF 里实测过）：
    ///   1. 安装器清单是 requireAdministrator → 必然弹一次 UAC，绕不过；
    ///   2. 设备安全描述符是 "D:P(A;;GA;;;SY)(A;;GA;;;BA)" → 装完之后
    ///      **读温度仍要求本进程以管理员运行**，这一步复用程序已有的提权链路；
    ///   3. `-install -silent` 是官方参数（"no UI, even on error"），所以成功与否
    ///      不能看有没有弹框，只能装完自己开设备验证。
    /// </summary>
    public static class PawnIoInstaller
    {
        public const string ServiceName = "PawnIO";
        public const string SetupFileName = "PawnIO_setup.exe";

        /// <summary>官方发布地址（latest 永远指向当前版本）。</summary>
        public const string OfficialUrl =
            "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

        /// <summary>签名主体必须包含这个字符串，否则拒绝执行（防止被替换成别的安装包）。</summary>
        private const string ExpectedSigner = "namazso";

        /// <summary>2.2.0 是 3,410,960 B。给个下限，挡住 HTML 错误页和截断的下载。</summary>
        private const long MinSetupBytes = 2 * 1024 * 1024;

        // ==================================================================
        // 检测
        // ==================================================================

        /// <summary>驱动服务是否已注册（决定要不要给「安装」按钮）。</summary>
        public static bool IsServiceRegistered()
        {
            return PawnIoBridge.IsServiceRegistered();
        }

        /// <summary>
        /// 设备能不能打开。true = 装好了且当前有权限；false 时 why 说明卡在哪一步。
        /// </summary>
        public static bool CanOpenDevice(out string why)
        {
            var io = new PawnIoBridge();
            try
            {
                return io.Open(out why);
            }
            finally
            {
                io.Dispose();
            }
        }

        /// <summary>
        /// 给 UI 用的一句话状态：
        ///   ready        —— 装好了、有权限、而且真的读出了温度
        ///   no_access    —— 装好了，但当前不是管理员
        ///   not_installed—— 没装
        ///   unsupported  —— 非 AMD 17h~1Ah，这条路本来就用不上
        /// 注意「设备能打开」不等于「能读温度」：驱动在但模块调不通时也要如实报出来，
        /// 所以这里用 Probe()（会真的读一次 SMN），不用 CanOpenDevice。
        /// </summary>
        public static string DescribeState()
        {
            if (!PawnIoCpuTemp.IsSupportedCpu) return "unsupported";

            var probe = new PawnIoCpuTemp();
            try
            {
                if (probe.Probe()) return "ready";
                return probe.DriverState;   // not_installed / no_access / read_failed / module_*
            }
            finally
            {
                probe.Dispose();
            }
        }

        // ==================================================================
        // 下载
        // ==================================================================

        /// <summary>
        /// 下载官方安装器到本地缓存目录，返回路径。同步方法，调用方自己放后台线程。
        /// 先找程序目录旁边的同名文件（用户手动下载后放这里），再走网络。
        /// </summary>
        public static string EnsureSetup(out string why)
        {
            why = "";

            // 1) 手工放置的（离线可用，也方便我们自己的安装包附带）
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SetupFileName);
            if (File.Exists(local) && Validate(local, out why)) return local;

            // 2) 缓存
            string cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PerfOverlay", "bin", SetupFileName);

            string cacheWhy;
            if (File.Exists(cache) && Validate(cache, out cacheWhy)) return cache;

            // 3) 下载
            string dir = Path.GetDirectoryName(cache);
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                why = "无法创建缓存目录：" + ex.Message;
                return null;
            }

            try
            {
                // GitHub 走 TLS 1.2；老框架默认可能是 TLS 1.0，会直接握手失败
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                string tmp = cache + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);

                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "PerfOverlay");
                    wc.DownloadFile(OfficialUrl, tmp);
                }

                if (!Validate(tmp, out why))
                {
                    try { File.Delete(tmp); } catch (Exception) { }
                    return null;
                }

                if (File.Exists(cache)) File.Delete(cache);
                File.Move(tmp, cache);
                return cache;
            }
            catch (Exception ex)
            {
                why = "下载失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 最低限度的事前校验：是 PE 文件、体积对得上、签名者是 namazso。
        /// 这三条挡住的是「下到 HTML 错误页」「下到半截」「被换成别的安装包」。
        /// </summary>
        public static bool Validate(string path, out string why)
        {
            why = "";
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { why = "文件不存在"; return false; }
                if (fi.Length < MinSetupBytes)
                {
                    why = string.Format("文件只有 {0:N0} 字节，不像安装包", fi.Length);
                    return false;
                }

                byte[] head = new byte[2];
                using (var fs = File.OpenRead(path))
                {
                    if (fs.Read(head, 0, 2) != 2) { why = "文件为空"; return false; }
                }
                if (head[0] != (byte)'M' || head[1] != (byte)'Z')
                {
                    why = "不是 PE 可执行文件";
                    return false;
                }

                X509Certificate signer = null;
                try { signer = X509Certificate.CreateFromSignedFile(path); }
                catch (Exception) { }

                if (signer == null)
                {
                    why = "文件没有数字签名";
                    return false;
                }

                if (signer.Subject.IndexOf(ExpectedSigner, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    why = "签名者不是预期的作者：" + signer.Subject;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                why = "校验失败：" + ex.Message;
                return false;
            }
        }

        // ==================================================================
        // 安装
        // ==================================================================

        /// <summary>
        /// 以管理员身份静默安装。调用方放后台线程（会阻塞到安装结束）。
        /// 返回 null 表示成功，否则返回失败原因。
        /// 注意：这里无论如何都不能弹自带 UI，安装器会自己请求 UAC。
        /// </summary>
        public static string Install(string setupPath)
        {
            if (!File.Exists(setupPath)) return "安装包不存在：" + setupPath;

            string why;
            if (!Validate(setupPath, out why)) return why;

            try
            {
                var psi = new ProcessStartInfo(setupPath, "-install -silent")
                {
                    UseShellExecute = true,   // 必须 true，否则 Verb="runas" 被忽略
                    Verb = "runas"
                };

                using (var p = Process.Start(psi))
                {
                    if (p == null) return "无法启动安装程序";
                    if (!p.WaitForExit(180000))
                    {
                        return "安装程序超时未结束（180 秒）";
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 1223 = 用户在 UAC 上点了「否」
                if (ex.NativeErrorCode == 1223) return "已取消（UAC 未授权）";
                return "启动安装程序失败：" + ex.Message;
            }
            catch (Exception ex)
            {
                return "安装失败：" + ex.Message;
            }

            // 不看退出码：-silent 下错误也是静默的。直接验证设备能不能开。
            string openWhy;
            if (CanOpenDevice(out openWhy)) return null;

            if (IsServiceRegistered())
                return "驱动已安装，但当前进程读不到设备：" + openWhy;

            return "安装未生效：" + openWhy;
        }

        /// <summary>供界面展示的安装位置提示。</summary>
        public static string CachePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PerfOverlay", "bin", SetupFileName);
            }
        }
    }
}
