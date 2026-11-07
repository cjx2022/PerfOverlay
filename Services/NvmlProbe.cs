using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PerfOverlay.Services
{
    /// <summary>
    /// NVIDIA 显卡的温度与利用率，走驱动自带的 nvml.dll（与 nvidia-smi 同源，实测：
    /// 非管理员即可读到温度 / 利用率 / 功耗）。
    ///
    /// 为什么不用 PDH 的 "\GPU Engine(*)\Utilization Percentage"：
    /// 本机该集合有 320 个实例，每个实例都要单独构造 PerformanceCounter 才能取值，
    /// 0.5 秒一轮等于每秒几百次 P/Invoke —— 对一个常驻浮窗来说太重了。
    /// NVML 是一次调用直接拿整卡数字，微秒级，且不需要遍历实例。
    /// 代价是只认 N 卡：dll 加载失败就整体标记不可用，不做无谓重试。
    ///
    /// 用 LoadLibrary + GetProcAddress 手工绑定（而不是 DllImport）：非 N 卡机器上
    /// DllImport 会抛 DllNotFoundException 走异常路径，而且关掉开关时没法卸载。
    /// </summary>
    public sealed class NvmlProbe : IDisposable
    {
        private const int NvmlSuccess = 0;
        private const int TemperatureGpu = 0;

        private IntPtr _lib = IntPtr.Zero;
        private IntPtr _device = IntPtr.Zero;
        private bool _tried;
        private bool _ok;

        private InitFn _init;
        private ShutdownFn _shutdown;
        private CountFn _count;
        private HandleFn _handle;
        private TempFn _temp;
        private UtilFn _util;
        private NameFn _name;

        /// <summary>可用性判断过程的说明，自检报告里直接打印。</summary>
        public string Status { get; private set; }

        public string DeviceName { get; private set; }
        public ulong MemoryTotalBytes { get; private set; }

        public bool Available { get { return _ok; } }

        public NvmlProbe()
        {
            Status = "未初始化";
            DeviceName = "";
        }

        /// <summary>第一次真正需要读数时才加载 dll；失败结果会被记住，不反复折腾。</summary>
        public bool EnsureInit()
        {
            if (_tried) return _ok;
            _tried = true;

            try
            {
                _lib = LoadLibraryW("nvml.dll");
                if (_lib == IntPtr.Zero)
                {
                    Status = "系统里没有 nvml.dll（非 NVIDIA 显卡，或驱动未安装）";
                    return false;
                }

                _init = Bind<InitFn>("nvmlInit_v2") ?? Bind<InitFn>("nvmlInit");
                _shutdown = Bind<ShutdownFn>("nvmlShutdown");
                _count = Bind<CountFn>("nvmlDeviceGetCount_v2") ?? Bind<CountFn>("nvmlDeviceGetCount");
                _handle = Bind<HandleFn>("nvmlDeviceGetHandleByIndex_v2") ?? Bind<HandleFn>("nvmlDeviceGetHandleByIndex");
                _temp = Bind<TempFn>("nvmlDeviceGetTemperature");
                _util = Bind<UtilFn>("nvmlDeviceGetUtilizationRates");
                _name = Bind<NameFn>("nvmlDeviceGetName");

                if (_init == null || _count == null || _handle == null || _temp == null || _util == null)
                {
                    Status = "nvml.dll 缺少必要的导出函数（驱动版本过旧）";
                    Release();
                    return false;
                }

                int rc = _init();
                if (rc != NvmlSuccess)
                {
                    Status = "nvmlInit 失败 rc=" + rc;
                    Release();
                    return false;
                }

                uint count;
                if (_count(out count) != NvmlSuccess || count == 0)
                {
                    Status = "NVML 未报告任何显卡";
                    Release();
                    return false;
                }

                // 多卡机器取第一块：浮窗是单行文字，不展开每张卡
                if (_handle(0, out _device) != NvmlSuccess || _device == IntPtr.Zero)
                {
                    Status = "nvmlDeviceGetHandleByIndex 失败";
                    Release();
                    return false;
                }

                if (_name != null)
                {
                    var sb = new StringBuilder(96);
                    if (_name(_device, sb, (uint)sb.Capacity) == NvmlSuccess) DeviceName = sb.ToString();
                }

                // 实测一次再宣布可用：能建句柄不等于两项数据都读得到
                uint t;
                Utilization u;
                bool tempOk = _temp(_device, TemperatureGpu, out t) == NvmlSuccess;
                bool utilOk = _util(_device, out u) == NvmlSuccess;

                if (!tempOk && !utilOk)
                {
                    Status = "NVML 句柄可用但温度与利用率都读不到";
                    Release();
                    return false;
                }

                _ok = true;
                Status = string.Format("NVML 就绪：{0}（温度={1} 利用率={2}）",
                    DeviceName, tempOk ? "可读" : "不可读", utilOk ? "可读" : "不可读");
                return true;
            }
            catch (Exception ex)
            {
                Status = "NVML 初始化异常：" + ex.GetType().Name + " " + ex.Message;
                Release();
                return false;
            }
        }

        /// <summary>读一次。两个 out 值各自可能失败，用返回的两个 bool 区分。</summary>
        public bool TryRead(out double tempCelsius, out double utilPercent,
                            out bool tempOk, out bool utilOk)
        {
            tempCelsius = 0;
            utilPercent = 0;
            tempOk = false;
            utilOk = false;

            if (!EnsureInit()) return false;

            try
            {
                uint t;
                if (_temp(_device, TemperatureGpu, out t) == NvmlSuccess)
                {
                    tempCelsius = t;
                    tempOk = true;
                }

                Utilization u;
                if (_util(_device, out u) == NvmlSuccess)
                {
                    utilPercent = u.Gpu > 100 ? 100 : u.Gpu;
                    utilOk = true;
                }
            }
            catch (Exception)
            {
                // 驱动重启/显卡掉线时委托可能失效，标记不可用等下次重新初始化
                Release();
                return false;
            }

            return tempOk || utilOk;
        }

        private T Bind<T>(string export) where T : class
        {
            if (_lib == IntPtr.Zero) return null;
            IntPtr p = GetProcAddress(_lib, export);
            if (p == IntPtr.Zero) return null;
            try { return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T)); }
            catch (Exception) { return null; }
        }

        private void Release()
        {
            try
            {
                if (_ok && _shutdown != null) _shutdown();
            }
            catch (Exception) { }

            _device = IntPtr.Zero;
            _init = null;
            _shutdown = null;
            _count = null;
            _handle = null;
            _temp = null;
            _util = null;
            _name = null;
            _ok = false;

            if (_lib != IntPtr.Zero)
            {
                try { FreeLibrary(_lib); } catch (Exception) { }
                _lib = IntPtr.Zero;
            }
        }

        /// <summary>关掉「温度 / GPU」开关时整体卸载，把 dll 也一起退掉。</summary>
        public void Dispose()
        {
            Release();
            _tried = false;
            DeviceName = "";
            Status = "已释放";
        }

        // ------------------------------------------------------------------

        private delegate int InitFn();
        private delegate int ShutdownFn();
        private delegate int CountFn(out uint count);
        private delegate int HandleFn(uint index, out IntPtr device);
        private delegate int TempFn(IntPtr device, int sensorType, out uint celsius);
        private delegate int UtilFn(IntPtr device, out Utilization utilization);
        private delegate int NameFn(IntPtr device, StringBuilder name, uint length);

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procName);
    }
}
