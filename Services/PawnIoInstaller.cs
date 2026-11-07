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
        ///   unsupported  —— 架构不在支持范围（AMD 非 17h~1Ah、且不是 Intel Core）
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
        // 残留清理（「装过 → 卸载过 → 再装」的唯一坑）
        // ==================================================================
        //
        // 实测现场（setupapi.dev.log，2026-11-07 21:42）：
        //   ndv: Driver package '...\DriverStore\FileRepository\pawnio.inf_amd64_xxx\pawnio.inf'
        //        is already imported.
        //   sto: Driver package already imported as 'oem53.inf'.
        //   inf: Failed to query service configuration. Error = 0x00000002
        //   ndv: Failed to install device instance 'ROOT\PAWNIO\0000'. Error = 0x00000002
        // 原因：官方卸载器把 .sys 与服务键删了，却把导入的驱动包**留在 DriverStore**。
        // 再跑 -install 时 setup 一看包已导入就跳过导入，直接装设备 —— 而包里那份
        // 服务所需的文件落不到位，AddService 直接 0x2。整个过程是 -silent，用户的
        // 观感就是「点一下立刻说没装上」。
        //
        // 解法：装之前把这块残留拆掉（删服务键 + pnputil 删包），逼 setup 走一遍
        // 「首次安装」路径 —— 那条路实测是通的。只有「包在、服务不在」才触发，
        // 正常机器和已装好的机器都不会被碰。

        /// <summary>DriverStore 里是否已导入 PawnIO 驱动包。</summary>
        public static bool HasStorePackage()
        {
            try
            {
                string repo = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"System32\DriverStore\FileRepository");
                if (!Directory.Exists(repo)) return false;

                foreach (var d in Directory.GetDirectories(repo, "pawnio.inf_*"))
                    return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// 是否是「装过又卸过」的残留状态：DriverStore 有包，但服务用不起来。
        /// 全新机器（没包）和已装好的机器（服务在）都不算。
        /// </summary>
        public static bool HasStaleState()
        {
            return HasStorePackage() && !IsServiceRegistered();
        }

        /// <summary>
        /// 是否是「装得不完整」：包在，但设备实例或服务缺一半。
        /// 官方卸载器留下的就是这种（删设备、删 .sys，服务键与驱动包都留着）。
        /// 这种状态直接重装通常也能成，但先清干净更稳 —— 代价只是多一秒。
        /// </summary>
        public static bool HasPartialState()
        {
            return HasStorePackage()
                   && (!IsServiceRegistered() || !PawnIoBridge.DeviceInstancePresent());
        }

        /// <summary>在 %windir%\INF 里找出 PawnIO 对应的 oemXX.inf（pnputil 认这个名字）。</summary>
        private static string FindOemInf()
        {
            try
            {
                string inf = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
                if (!Directory.Exists(inf)) return null;

                foreach (var f in Directory.GetFiles(inf, "oem*.inf"))
                {
                    try
                    {
                        // 驱动 INF 只有 1~2 KB，直接读全文最稳（不依赖 pnputil 的本地化输出）
                        string text = File.ReadAllText(f);
                        if (text.IndexOf("PawnIO", StringComparison.OrdinalIgnoreCase) >= 0
                            && text.IndexOf("namazso", StringComparison.OrdinalIgnoreCase) >= 0)
                            return Path.GetFileName(f);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// 拆掉残留：删服务键 → pnputil 删驱动包（含设备实例）。
        /// 返回 null 表示清干净了（或本来就没残留），否则返回失败原因。
        /// 需要管理员：写 SCM/DriverStore 都要提权，非管理员会明确报出来。
        /// </summary>
        public static string CleanupStale(out string detail)
        {
            detail = "";

            if (!HasStorePackage()) return null;

            var sb = new System.Text.StringBuilder();

            // 1) 服务键。正常卸载后它已经不在了，sc 会返回 1060，忽略即可。
            int scExit;
            string scOut;
            RunTool("sc.exe", "delete PawnIO", out scExit, out scOut);
            sb.AppendLine("sc delete PawnIO -> exit " + scExit);
            if (!string.IsNullOrEmpty(scOut)) sb.AppendLine("  " + scOut.Trim());

            // 2) 驱动包。/uninstall 顺带处理设备实例，/force 应付「设备正在使用」。
            string oem = FindOemInf();
            if (oem == null)
            {
                // 找不到 oem 名就没法删包。这种情况极少（包在、INF 被清理过），
                // 直接让安装器去试，失败后由调用方把现场报给用户。
                sb.AppendLine("未在 %windir%\\INF 找到 PawnIO 的 oem*.inf，跳过删包");
                detail = sb.ToString();
                return null;
            }

            int pnpExit;
            string pnpOut;
            RunTool("pnputil.exe", "/delete-driver " + oem + " /uninstall /force", out pnpExit, out pnpOut);
            sb.AppendLine("pnputil /delete-driver " + oem + " -> exit " + pnpExit);
            if (!string.IsNullOrEmpty(pnpOut))
            {
                foreach (var line in pnpOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (line.Trim().Length > 0) sb.AppendLine("  " + line.Trim());
            }

            detail = sb.ToString();

            // 判据只看结果，不看退出码：包真的从 DriverStore 消失了才算清干净。
            if (HasStorePackage())
            {
                if (!PrivilegeHelper.IsElevated)
                    return "旧的驱动包需要管理员权限才能清理，请以管理员身份运行本程序后重试";
                return "清理旧驱动包失败（pnputil exit " + pnpExit + "）";
            }

            return null;
        }

        /// <summary>跑一个控制台工具，拿退出码与合并输出（输出编码按 OEM 解，只用于日志展示）。</summary>
        private static void RunTool(string exe, string args, out int exitCode, out string output)
        {
            exitCode = -1;
            output = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var p = Process.Start(psi))
                {
                    if (p == null) return;
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        output = "超时";
                        return;
                    }
                    exitCode = p.ExitCode;
                    output = (so + "\r\n" + se).Trim();
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
            }
        }

        // ==================================================================
        // 安装
        // ==================================================================

        /// <summary>
        /// 以管理员身份静默安装。调用方放后台线程（会阻塞到安装结束）。
        /// 返回 null 表示驱动装好且可用（含「装好了但本进程没权限」—— 那是权限问题，
        /// 不是安装问题，由调用方走提权引导）；否则返回失败原因。
        /// 注意：这里无论如何都不能弹自带 UI，安装器会自己请求 UAC。
        /// </summary>
        public static string Install(string setupPath)
        {
            return Install(setupPath, null);
        }

        /// <summary>
        /// 同上，额外带出过程日志（给 --pawnio-install 与排障用）。
        /// </summary>
        public static string Install(string setupPath, System.Text.StringBuilder trace)
        {
            if (!File.Exists(setupPath)) return "安装包不存在：" + setupPath;

            string why;
            if (!Validate(setupPath, out why)) return why;

            // 0) 装过又卸过的机器：先把 DriverStore 里的旧包拆掉，否则安装器会
            //    跳过导入、直接装设备，然后 0x2 静默失败（见上面那段现场日志）。
            //
            //    分两档：
            //      hard —— 服务键也没了。这种状态安装器 100% 失败，清理必须成功。
            //      soft —— 服务键还在、只是设备实例缺（官方卸载器的典型残留）。
            //              直接装一般也能成，清理只是让它更干净，失败了不拦。
            bool hardStale = HasStaleState();
            if (hardStale || HasPartialState())
            {
                if (trace != null) trace.AppendLine("  检测到旧的驱动包残留，先清理（关键状态："
                                                    + (hardStale ? "服务键缺失" : "设备实例缺失") + "）");
                string cdetail;
                string cerr = CleanupStale(out cdetail);
                if (trace != null && !string.IsNullOrEmpty(cdetail)) trace.AppendLine(cdetail);
                if (cerr != null)
                {
                    if (trace != null) trace.AppendLine("  清理失败：" + cerr);
                    if (hardStale) return cerr;          // 不清理就一定装不上
                    if (trace != null) trace.AppendLine("  继续尝试安装");
                }
            }

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

            // 不看退出码：-silent 下错误也是静默的，只能自己验。
            // 设备对象有时比进程退出晚几十毫秒才出现，最多等 3 秒。
            string openWhy = "";
            for (int i = 0; i < 15; i++)
            {
                if (CanOpenDevice(out openWhy)) return null;
                if (IsServiceRegistered() && PawnIoBridge.DeviceInstancePresent()) break;
                System.Threading.Thread.Sleep(200);
            }

            // 服务 + 设备实例都在，就说明「装上了」。设备打不开只可能是 ACL —— INF 里
            // 写死只有 SYSTEM 与 Administrators 能访问，非管理员必然 ACCESS_DENIED。
            // 这是权限问题、不是安装问题，返回成功让上层走「以管理员身份重启」，
            // 别在这里报「安装未生效」把人引到错方向（0.9.5 之前的真实误判）。
            if (IsServiceRegistered() && PawnIoBridge.DeviceInstancePresent())
            {
                if (trace != null) trace.AppendLine("  服务与设备实例都在；设备打不开 = 权限问题：" + openWhy);
                return null;
            }

            if (trace != null) trace.AppendLine("  验证失败：" + openWhy);
            return "安装未生效：" + openWhy + "。" + Diagnose();
        }

        /// <summary>安装失败时的一句话现场，用来区分「没装上」「装上了但文件缺」「环境拦住了」。</summary>
        public static string Diagnose()
        {
            string dev = PawnIoBridge.DeviceInstancePresent() ? "在" : "无";
            string svc = IsServiceRegistered() ? "在" : "无";
            string sys = DriverFilePresent() ? "在" : "无";
            string pkg = HasStorePackage() ? "有" : "无";
            string detail = "设备实例=" + dev + "，服务键=" + svc + "，驱动文件=" + sys + "，DriverStore 包=" + pkg;

            if ("在".Equals(dev) && "在".Equals(svc)) return "（诊断：" + detail + "）";
            return "（诊断：" + detail + "；可到「应用和功能」里卸载 PawnIO 后重试）";
        }

        /// <summary>服务真正要加载的那个 .sys 是否在（按服务 ImagePath 还原，不猜路径）。</summary>
        public static bool DriverFilePresent()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                           @"SYSTEM\CurrentControlSet\Services\" + ServiceName))
                {
                    if (k == null) return false;
                    string image = k.GetValue("ImagePath") as string;
                    if (string.IsNullOrEmpty(image)) return false;
                    return File.Exists(PawnIoBridge.ResolveImagePath(image));
                }
            }
            catch (Exception)
            {
                return false;
            }
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
