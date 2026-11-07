using System;
using System.Runtime.InteropServices;

namespace PerfOverlay.Services
{
    /// <summary>
    /// GPU 核心温度：走 WDDM 内核接口 D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA)。
    ///
    /// 这是 Windows 任务管理器「GPU 温度」用的同一条路 —— 逆向 Taskmgr.exe 的导入表可见
    /// 它从 gdi32 导入 D3DKMTOpenAdapterFromLuid / D3DKMTQueryAdapterInfo / D3DKMTCloseAdapter，
    /// 且从不用 nvml.dll 或 atiadlxx.dll。
    ///
    /// 好处：A / N / I 三家通吃，**不需要管理员权限、不需要内核驱动**，也就不会被
    /// 内存完整性（HVCI）拦截 —— 这正是 NVML 做不到的（A 卡上根本没有 nvml.dll）。
    ///
    /// 本机实测（非提权）：rc=0，raw=524 → 52.4 ℃，与 NVML 的 52.0 同值。
    /// 注意 PrivateDriverDataSize 必须恰好 64（等于结构体大小），
    /// 传 128 或 60 一律返回 0xC000000D STATUS_INVALID_PARAMETER。
    /// </summary>
    public sealed class WddmGpuTemp : IDisposable
    {
        private const uint KMTQAITYPE_ADAPTERPERFDATA = 62;

        /// <summary>D3DKMT_ADAPTER_PERFDATA 的实际大小（含尾部对齐）。</summary>
        private const uint PerfDataSize = 64;

        /// <summary>Temperature 字段在 D3DKMT_ADAPTER_PERFDATA 里的字节偏移。</summary>
        private const int OffsetTemperature = 56;

        private bool _tried;
        private bool _ok;
        private long _luid;
        private string _why = "未初始化";

        public string Status { get { return _why; } }
        public bool Available { get { return _ok; } }

        /// <summary>本机是否支持这条路（结构体大小/接口存在），用于自检展示。</summary>
        public string Detail { get; private set; }

        public WddmGpuTemp()
        {
            Detail = "";
        }

        /// <summary>
        /// 绑定到指定适配器。传 0 表示用「当前最像主显卡」的适配器（由调用方决定）。
        /// 返回是否具备读取条件。
        /// </summary>
        public bool Attach(long adapterLuid)
        {
            _luid = adapterLuid;
            _tried = false;
            _ok = false;
            return Probe();
        }

        /// <summary>探测一次：打开适配器并查一次温度，成功即认为这条路可用。</summary>
        public bool Probe()
        {
            if (_tried) return _ok;
            _tried = true;

            double t;
            string why;
            if (TryQuery(out t, out why))
            {
                _ok = true;
                _why = "正常";
                return true;
            }

            _ok = false;
            _why = why;
            return false;
        }

        /// <summary>采样温度（摄氏度）。失败时保留 Status 说明原因。</summary>
        public bool TryRead(out double celsius)
        {
            celsius = 0;
            string why;
            if (!TryQuery(out celsius, out why))
            {
                _ok = false;
                _why = why;
                return false;
            }
            _ok = true;
            _why = "正常";
            return true;
        }

        private bool TryQuery(out double celsius, out string why)
        {
            celsius = 0;
            why = "";

            var open = new D3DKMT_OPENADAPTERFROMLUID();
            open.AdapterLuid.LowPart = (uint)(_luid & 0xFFFFFFFF);
            open.AdapterLuid.HighPart = (int)((_luid >> 32) & 0xFFFFFFFF);

            int rc = D3DKMTOpenAdapterFromLuid(ref open);
            if (rc != 0)
            {
                why = string.Format("WDDM 适配器打开失败（0x{0:X8}）", rc);
                return false;
            }

            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal((int)PerfDataSize);
                // 归零：驱动没填的字段读出来不该是垃圾
                for (int i = 0; i < (int)PerfDataSize; i += 4) Marshal.WriteInt32(buf, i, 0);

                var q = new D3DKMT_QUERYADAPTERINFO();
                q.hAdapter = open.hAdapter;
                q.Type = KMTQAITYPE_ADAPTERPERFDATA;
                q.pPrivateDriverData = buf;
                q.PrivateDriverDataSize = PerfDataSize;

                int rc2 = D3DKMTQueryAdapterInfo(ref q);
                if (rc2 != 0)
                {
                    why = string.Format("WDDM 温度查询失败（0x{0:X8}）", rc2);
                    return false;
                }

                int raw = Marshal.ReadInt32(buf, OffsetTemperature);
                if (raw <= 0)
                {
                    // 虚拟机、部分核显、远程桌面下没有温度传感器，驱动会填 0
                    why = "该显卡没有通过 WDDM 暴露温度传感器";
                    return false;
                }

                celsius = raw / 10.0;
                // 只报温度：这个结构里还有风扇/功耗字段，但多数驱动不填（本机读到 674 RPM
                // 与 0.2 W 这种明显无效值），写进 tooltip 只会误导，所以不要。
                Detail = string.Format("WDDM 传感器：{0:0.0} ℃", celsius);
                why = "";
                return true;
            }
            catch (Exception ex)
            {
                why = "WDDM 温度读取异常：" + ex.Message;
                return false;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                var close = new D3DKMT_CLOSEADAPTER();
                close.hAdapter = open.hAdapter;
                try { D3DKMTCloseAdapter(ref close); } catch (Exception) { }
            }
        }

        public void Dispose()
        {
            // 无持久句柄：每次查询即开即关，驱动侧不会留下引用
        }

        // ---------------- P/Invoke ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_OPENADAPTERFROMLUID
        {
            public LUID AdapterLuid;
            public uint hAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYADAPTERINFO
        {
            public uint hAdapter;
            public uint Type;
            public IntPtr pPrivateDriverData;
            public uint PrivateDriverDataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_CLOSEADAPTER
        {
            public uint hAdapter;
        }

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID pData);

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO pData);

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER pData);
    }
}
