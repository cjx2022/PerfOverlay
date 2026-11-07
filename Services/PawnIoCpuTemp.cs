using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace PerfOverlay.Services
{
    /// <summary>
    /// CPU 温度的第二级路径：PawnIO 驱动 + AMDFamily17 模块读 SMN。
    ///
    /// 为什么必须走到内核：Windows 用户态没有任何稳定的读 CPU 温度接口，
    /// ACPI 热区（AcpiTempProbe）有没有值全看 BIOS 有没有实现 _TZ，台式 DIY 板子
    /// 经常直接回「不支持」。真正普适的办法只有读 AMD 的 SMN 寄存器
    /// （THM_TCON_CUR_TMP @ 0x00059800），而这需要内核态访问 PCI 配置空间。
    ///
    /// 这里不做「自己带驱动」那种事 —— 那要 EV 证书 + 微软签名流程。改用 PawnIO：
    /// 微软签名、HVCI 兼容、不在易受攻击驱动阻止列表里，用户装一次（3.25 MB 官方安装器）
    /// 之后所有走 PawnIO 的软件都能用。LHM 0.8 以后也是这么做的。
    ///
    /// 读温度的公式与 LibreHardwareMonitor 完全一致（同一颗寄存器、同一套掩码）：
    ///   raw = ReadSmn(0x00059800)
    ///   offset49 = (raw &amp; 0x80000) != 0 || (raw &amp; 0x30000) == 0x30000
    ///   t = (raw >> 21) * 0.125        // 单位 0.125℃
    ///   if (offset49) t -= 49          // 部分型号带 49℃ 偏移
    ///
    /// AMDFamily17 模块内部的家族白名单是 family 17h~1Ah，正好覆盖 Zen1 到 Zen5，
    /// 所以 Ryzen 5 5600（19h）和 Ryzen 5 9600X（1Ah）走的是同一条路径。
    /// </summary>
    public sealed class PawnIoCpuTemp : IDisposable
    {
        private const string ModuleResource = "PerfOverlay.AMDFamily17.bin";
        private const string ModuleName = "AMDFamily17.bin";

        /// <summary>THM_TCON_CUR_TMP：AMD 从 17h 到 1Ah 都用这一个地址报封装温度。</summary>
        private const uint ThmTconCurTmp = 0x00059800;
        private const uint RangeSelMask = 0x00080000;
        private const uint TjSelMask = 0x00030000;

        private PawnIoBridge _io;
        private bool _moduleLoaded;
        private bool _tried;
        private bool _ok;
        private string _why = "未探测";

        public string Status { get { return _why; } }
        public bool Available { get { return _ok; } }

        /// <summary>驱动侧状态，UI 用来决定提示「去安装」还是「去授权」。</summary>
        public string DriverState { get; private set; }

        public bool DriverInstalled { get; private set; }

        // ==================================================================
        // CPU 识别：只对 AMD 17h~1Ah 有意义（Intel 的 DTS 在 MSR 里，是另一条路）
        // ==================================================================

        public static int CpuFamily { get; private set; }
        public static int CpuModel { get; private set; }
        public static string CpuName { get; private set; }

        static PawnIoCpuTemp()
        {
            CpuName = "";
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                           @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null)
                    {
                        object id = k.GetValue("Identifier");
                        object nm = k.GetValue("ProcessorNameString");
                        if (nm != null) CpuName = nm.ToString().Trim();
                        if (id != null) ParseIdentifier(id.ToString());
                    }
                }
            }
            catch (Exception) { }
        }

        private static void ParseIdentifier(string id)
        {
            // 形如 "AMD64 Family 25 Model 33 Stepping 2"
            CpuFamily = 0;
            CpuModel = 0;
            string[] parts = id.Split(' ');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                int v;
                if (!int.TryParse(parts[i + 1], out v)) continue;
                if (string.Equals(parts[i], "Family", StringComparison.OrdinalIgnoreCase)) CpuFamily = v;
                else if (string.Equals(parts[i], "Model", StringComparison.OrdinalIgnoreCase)) CpuModel = v;
            }
        }

        /// <summary>模块内部白名单是 17h~1Ah；不在这区间就别去麻烦驱动了。</summary>
        public static bool IsSupportedCpu
        {
            get { return CpuFamily >= 0x17 && CpuFamily <= 0x1A; }
        }

        public static string CpuSummary
        {
            get
            {
                if (CpuName.Length == 0) return "未知处理器";
                return string.Format("{0}（family {1:X2}h model {2:X2}h）", CpuName, CpuFamily, CpuModel);
            }
        }

        // ==================================================================
        // 探测
        // ==================================================================

        /// <summary>
        /// 探测一次并缓存结论。用户可能在程序运行期间刚装完驱动，
        /// 所以设置面板点完安装后会调 Reset() 强制重新探测。
        /// </summary>
        public bool Probe()
        {
            if (_tried) return _ok;
            _tried = true;

            _ok = false;

            if (!IsSupportedCpu)
            {
                _why = "当前处理器不在 AMD 17h~1Ah 范围内（" + CpuSummary + "），不走这条路";
                DriverState = "not_applicable";
                return false;
            }

            if (_io == null) _io = new PawnIoBridge();

            string why;
            if (!_io.Open(out why))
            {
                // 「装没装」只能问注册表：设备不存在时非管理员也可能拿到 ACCESS_DENIED，
                // 按错误码猜会把「没装」误判成「没权限」，引导就指错方向了。
                bool registered = PawnIoBridge.IsServiceRegistered();
                DriverInstalled = registered;

                if (!registered)
                {
                    _why = "未安装 PawnIO 驱动（CPU 温度需要装一次组件）";
                    DriverState = "not_installed";
                }
                else
                {
                    _why = "PawnIO 已安装，但当前进程拿不到设备访问权（需要管理员运行）";
                    DriverState = "no_access";
                }

                // 打开失败时把对象扔掉，下次 Probe 重新来
                _io.Dispose();
                _io = null;
                return false;
            }

            DriverInstalled = true;

            if (!_moduleLoaded)
            {
                byte[] image = ReadModuleImage();
                if (image == null)
                {
                    _why = "安装包内缺少 " + ModuleName;
                    DriverState = "module_missing";
                    return false;
                }

                if (!_io.TryLoadModule(image, ModuleName, out why))
                {
                    _why = why;
                    DriverState = "module_failed";
                    return false;
                }
                _moduleLoaded = true;
            }

            double t;
            if (!ReadOnce(out t, out why))
            {
                _why = why;
                DriverState = "read_failed";
                return false;
            }

            _ok = true;
            _why = "正常";
            DriverState = "ready";
            return true;
        }

        /// <summary>清掉缓存结论，下次 Probe 重新走一遍（装完驱动后调用）。</summary>
        public void Reset()
        {
            _tried = false;
            _ok = false;
            _why = "未探测";
            _moduleLoaded = false;
            if (_io != null)
            {
                _io.Dispose();
                _io = null;
            }
        }

        public bool TryRead(out double celsius, out string why)
        {
            if (!Probe())
            {
                celsius = 0;
                why = _why;
                return false;
            }
            return ReadOnce(out celsius, out why);
        }

        // ==================================================================

        private bool ReadOnce(out double celsius, out string why)
        {
            celsius = 0;

            uint raw;
            if (!_io.TryReadSmn(ThmTconCurTmp, out raw, out why))
                return false;

            bool offset49 = (raw & RangeSelMask) != 0
                            || (raw & TjSelMask) == TjSelMask;

            double t = (raw >> 21) * 0.125;
            if (offset49) t -= 49.0;

            // 合理性闸门：读错寄存器或者驱动异常时 raw 往往是 0 或全 F，
            // 会算出 0℃ / 8000℃ 这种明显不是温度的数，宁可报不可用也别显示假值。
            if (t < 5.0 || t > 125.0)
            {
                celsius = 0;
                why = string.Format("读数不合理（raw=0x{0:X8} → {1:0.0}℃），已丢弃", raw, t);
                return false;
            }

            celsius = t;
            why = "";
            return true;
        }

        private static byte[] ReadModuleImage()
        {
            // 程序目录下的同名文件优先：
            //   1) LGPL-2.1 要求用户有能力替换被链接的库，给一个「放文件就生效」的入口比写文档实在；
            //   2) 排障时可以临时换一个模块版本，不用重新编译。
            string[] sidecars = new string[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", ModuleName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ModuleName)
            };

            foreach (string p in sidecars)
            {
                try
                {
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                }
                catch (Exception) { }
            }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream(ModuleResource))
                {
                    if (s == null) return null;
                    var buf = new byte[s.Length];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = s.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return buf;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_io != null)
            {
                try { _io.Dispose(); } catch (Exception) { }
                _io = null;
            }
            _moduleLoaded = false;
        }
    }
}
