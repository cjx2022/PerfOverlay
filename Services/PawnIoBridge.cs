using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PerfOverlay.Services
{
    /// <summary>
    /// PawnIO 设备桥接：手写 DeviceIoControl，与 \\.\PawnIO 直接对话。
    ///
    /// 为什么不用官方 PawnIOLib.dll：PawnIO 是 GPL-2.0，官方许可里有一条明确例外 ——
    /// 「只通过 device IO control 接口与 PawnIO 通信的独立模块」不受 GPL 传染。
    /// 一旦链接 PawnIOLib.dll，本项目整体就成了 GPL 派生作品，必须开源。
    /// 所以这里一个字节都不链接，全部按 pawnio_um.h / PawnIOLib.cpp 的线格式自己拼。
    ///
    /// 三个 IOCTL 的线格式（都是 METHOD_BUFFERED）：
    ///   VERSION       in 无      out ULONG 版本
    ///   LOAD_BINARY   in 模块镜像 out 无
    ///   EXECUTE_FN    in [32 字节函数名(ASCII,NUL 填充)][cell0][cell1]...  out [cell0][cell1]...
    /// 注意 cell 是 **64 位**，不是 32 位。
    /// </summary>
    public sealed class PawnIoBridge : IDisposable
    {
        public const string DevicePath = @"\\.\PawnIO";
        public const string ServiceName = "PawnIO";

        // CTL_CODE(41394, fn, METHOD_BUFFERED, FILE_ANY_ACCESS)
        private const uint DeviceType = 41394;              // 0xA1B2，见 pawnio_um.h
        private const uint IoctlLoadBinary = (DeviceType << 16) | (0x821u << 2);
        private const uint IoctlExecuteFn = (DeviceType << 16) | (0x841u << 2);
        private const uint IoctlVersion = (DeviceType << 16) | (0x861u << 2);

        /// <summary>函数名槽位固定 32 字节（PawnIOLib.cpp 里 FN_NAME_LENGTH），所以名字最长 31 字符。</summary>
        private const int FnNameLength = 32;

        private const uint GenericReadWrite = 0x80000000u | 0x40000000u;
        private const uint FileShareReadWrite = 0x00000001u | 0x00000002u;
        private const uint OpenExisting = 3;

        private SafeFileHandle _handle;
        private string _why = "未打开";
        private uint _version;
        private Mutex _pciMutex;

        public bool IsOpen { get { return _handle != null && !_handle.IsInvalid; } }
        public string Status { get { return _why; } }
        public uint Version { get { return _version; } }

        public string VersionText
        {
            get
            {
                if (_version == 0) return "未知";
                return string.Format("{0}.{1}.{2}",
                    (_version >> 16) & 0xFF, (_version >> 8) & 0xFF, _version & 0xFF);
            }
        }

        // ==================================================================
        // 打开 / 关闭
        // ==================================================================

        /// <summary>
        /// 打开设备并按需启动驱动服务。
        /// 失败原因分三类，UI 要据此决定给「安装」还是「以管理员重启」：
        ///   未装驱动 → 找不到设备对象（ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND）
        ///   已装但没权限 → ERROR_ACCESS_DENIED（INF 里 SDDL 只授 SYSTEM 与 Administrators）
        ///   其它 → 原样带错误码，别猜
        /// </summary>
        public bool Open(out string why)
        {
            if (IsOpen) { why = "已打开"; return true; }

            _handle = CreateFileW(DevicePath, GenericReadWrite, FileShareReadWrite,
                                  IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

            if (_handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                _handle = null;
                _why = DescribeOpenError(err);
                why = _why;
                return false;
            }

            uint v;
            if (TryVersion(out v, out why))
            {
                _version = v;
                _why = "已连接（驱动 " + VersionText + "）";
                // PCI 配置空间访问必须串行化：PawnIO 官方要求先拿全局互斥体，
                // 否则与 HWiNFO / LHM 这类同样在读 SMN 的程序撞车会读到垃圾。
                TryOpenPciMutex();
                return true;
            }

            return true; // 设备打开成功但版本查询失败：仍然可用，只是拿不到版本号
        }

        private static string DescribeOpenError(int err)
        {
            switch (err)
            {
                case 2:  // ERROR_FILE_NOT_FOUND
                case 3:  // ERROR_PATH_NOT_FOUND
                    return "未安装 PawnIO 驱动";
                case 5:  // ERROR_ACCESS_DENIED
                    return string.Format("无法访问 PawnIO 设备（错误 {0}）", err);
                default:
                    return string.Format("无法打开 PawnIO 设备（错误 {0}）", err);
            }
        }

        /// <summary>
        /// 驱动服务是否已注册。**判断「装没装」只能看这个，不能看 CreateFile 的错误码**：
        /// 设备对象不存在时，非管理员进程拿到的也可能是 ERROR_ACCESS_DENIED（不是 FILE_NOT_FOUND），
        /// 照错误码猜会把「没装」误判成「没权限」，UI 就会给出错误的引导。
        /// </summary>
        public static bool IsServiceRegistered()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                           @"SYSTEM\CurrentControlSet\Services\" + ServiceName))
                {
                    return k != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void TryOpenPciMutex()
        {
            if (_pciMutex != null) return;
            try
            {
                // 已存在就打开，不存在就创建 —— 谁先来都能用
                _pciMutex = new Mutex(false, @"Global\Access_PCI");
            }
            catch (Exception)
            {
                _pciMutex = null;
            }
        }

        // ==================================================================
        // IOCTL
        // ==================================================================

        private bool TryVersion(out uint version, out string why)
        {
            version = 0;
            why = "";
            var buf = new byte[4];
            uint ret;

            if (!DeviceIoControl(_handle, IoctlVersion, null, 0, buf, 4, out ret, IntPtr.Zero))
            {
                why = "版本查询失败：" + LastErrorText();
                return false;
            }

            version = BitConverter.ToUInt32(buf, 0);
            return true;
        }

        /// <summary>加载模块镜像（.bin 原始字节）。成功返回 true。</summary>
        public bool TryLoadModule(byte[] image, string name, out string why)
        {
            why = "";
            if (!IsOpen) { why = _why; return false; }
            if (image == null || image.Length == 0) { why = "模块镜像为空"; return false; }

            uint ret;
            if (!DeviceIoControl(_handle, IoctlLoadBinary, image, (uint)image.Length,
                                 null, 0, out ret, IntPtr.Zero))
            {
                why = "加载模块 " + name + " 失败：" + LastErrorText();
                return false;
            }
            return true;
        }

        /// <summary>
        /// 调用模块里的导出函数。inCells 与 outCells 都是 64 位 cell。
        /// </summary>
        public bool TryExecute(string fn, ulong[] inCells, int outCount, out ulong[] outCells, out string why)
        {
            outCells = new ulong[outCount];
            why = "";

            if (!IsOpen) { why = _why; return false; }
            if (fn == null || fn.Length >= FnNameLength)
            {
                why = "函数名过长（上限 " + (FnNameLength - 1) + " 字符）：" + fn;
                return false;
            }

            int inCount = inCells == null ? 0 : inCells.Length;
            var inBuf = new byte[FnNameLength + inCount * 8];
            byte[] nameBytes = Encoding.ASCII.GetBytes(fn);
            Array.Copy(nameBytes, 0, inBuf, 0, nameBytes.Length);
            for (int i = 0; i < inCount; i++)
                BitConverter.GetBytes(inCells[i]).CopyTo(inBuf, FnNameLength + i * 8);

            var outBuf = new byte[outCount * 8];
            uint ret;

            // 读 SMN 这类操作要和别的监控软件串行化，否则 PCI 索引/数据寄存器会串台
            bool locked = false;
            if (_pciMutex != null)
            {
                try { locked = _pciMutex.WaitOne(300); } catch (Exception) { locked = false; }
            }

            bool ok;
            try
            {
                ok = DeviceIoControl(_handle, IoctlExecuteFn,
                                     inBuf, (uint)inBuf.Length,
                                     outCount > 0 ? outBuf : null, (uint)outBuf.Length,
                                     out ret, IntPtr.Zero);
            }
            finally
            {
                if (locked) { try { _pciMutex.ReleaseMutex(); } catch (Exception) { } }
            }

            if (!ok)
            {
                why = fn + " 调用失败：" + LastErrorText();
                return false;
            }

            for (int i = 0; i < outCount; i++)
                outCells[i] = BitConverter.ToUInt64(outBuf, i * 8);
            return true;
        }

        private static string LastErrorText()
        {
            int err = Marshal.GetLastWin32Error();
            string text;
            try { text = new Win32Exception(err).Message; }
            catch (Exception) { text = ""; }
            return string.Format("0x{0:X8} {1}", err, text).TrimEnd();
        }

        // ==================================================================
        // 便捷封装（只需要一两行的地方直接调）
        // ==================================================================

        /// <summary>
        /// 读一个 SMN 寄存器。函数名是 <c>ioctl_read_smn</c>（AMDFamily17 模块的导出名）——
        /// 注意别写成 RyzenSMU 模块的 <c>ioctl_read_smu_register</c>：两个模块名字不同，
        /// 调错会返回「系统找不到指定的文件」(0x2)，看起来像驱动没装，其实只是函数名错。
        /// </summary>
        public bool TryReadSmn(uint address, out uint value, out string why)
        {
            value = 0;
            ulong[] o;
            if (!TryExecute("ioctl_read_smn", new ulong[] { address }, 1, out o, out why))
                return false;
            value = (uint)(o[0] & 0xFFFFFFFFu);
            return true;
        }

        /// <summary>
        /// 读一个 MSR（走 IntelMSR 模块的 ioctl_read_msr）。模块内部有白名单，
        /// 只放行 IA32_TEMPERATURE_TARGET / PACKAGE_THERM_STATUS / THERM_STATUS 等
        /// 只读的性能与温度寄存器，写操作是另一个 ioctl，本程序从不调用。
        /// </summary>
        public bool TryReadMsr(uint index, out ulong value, out string why)
        {
            value = 0;
            ulong[] o;
            if (!TryExecute("ioctl_read_msr", new ulong[] { index }, 1, out o, out why))
                return false;
            value = o[0];
            return true;
        }

        public void Dispose()
        {
            if (_pciMutex != null)
            {
                try { _pciMutex.Dispose(); } catch (Exception) { }
                _pciMutex = null;
            }
            if (_handle != null)
            {
                try { _handle.Dispose(); } catch (Exception) { }
                _handle = null;
            }
            _why = "已关闭";
        }

        // ==================================================================

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
                                                        IntPtr security, uint disposition,
                                                        uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
                                                   byte[] inBuffer, uint inSize,
                                                   byte[] outBuffer, uint outSize,
                                                   out uint returned, IntPtr overlapped);
    }
}
