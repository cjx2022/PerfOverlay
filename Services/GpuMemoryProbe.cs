using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 显存利用率探针。
    ///
    /// 走过的两条路（都实测过，结论写在这里避免下次踩）：
    ///
    /// 1) IDXGIAdapter3::QueryVideoMemoryInfo —— 拿到 hr=S_OK，但 CurrentUsage 恒为 0。
    ///    原因是它返回的是**本进程**在该适配器上的显存占用；监控浮窗自己不渲染，
    ///    自然是 0。所以它不能用来做「整机显存利用率」。
    ///
    /// 2) 现已采用：DXGI 只负责拿适配器名 / 物理显存容量 / LUID，
    ///    整机已用显存走 Windows GPU 计数器 "\GPU Adapter Memory(*)\Dedicated Usage"
    ///    （与任务管理器「专用 GPU 内存」同一数据源，按 LUID 匹配到目标显卡）。
    ///
    /// 另外注意：ComImport 接口方法必须标 [PreserveSig]，否则 HRESULT 失败会被
    /// 包装成 COMException 抛出（EnumAdapters1 枚举到尾就是 DXGI_ERROR_NOT_FOUND）。
    /// </summary>
    public sealed class GpuMemoryProbe : IDisposable
    {
        private IDXGIFactory1 _factory;
        private bool _initialized;
        private bool _failed;
        private bool _comInitialized;

        private string _adapterName = "";
        private ulong _dedicatedTotal;
        private long _adapterLuid;

        private PerformanceCounter[] _counters;
        private string _counterCategory = "";
        private string _counterName = "";
        private string _counterError = "";
        private int _consecutiveFailures;

        /// <summary>初始化过程的分步记录，用于自检和排障。</summary>
        public readonly List<string> Trace = new List<string>();

        public string AdapterName { get { return _adapterName; } }
        public ulong DedicatedTotal { get { return _dedicatedTotal; } }
        public string CounterError { get { return _counterError; } }

        /// <summary>能取到「容量 + 已用」两个数才算可用。</summary>
        public bool Available { get { return _initialized && _counters != null && _counters.Length > 0; } }

        public bool Initialize()
        {
            if (_initialized) return Available;
            if (_failed) return false;

            try
            {
                int hr = CoInitializeEx(IntPtr.Zero, 0x0 /* COINIT_MULTITHREADED */);
                _comInitialized = (hr >= 0);
                Trace.Add(string.Format("CoInitializeEx hr=0x{0:X8}", hr));

                var iidFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                IntPtr pFactory;
                int hrCreate = CreateDXGIFactory1(ref iidFactory1, out pFactory);
                Trace.Add(string.Format("CreateDXGIFactory1 hr=0x{0:X8}", hrCreate));

                if (hrCreate < 0 || pFactory == IntPtr.Zero)
                {
                    _failed = true;
                    return false;
                }

                _factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(pFactory);
                Marshal.Release(pFactory);

                // 选物理显存最大的适配器（独显优先），纯集显机器则取第一个
                ulong bestVram = 0;
                for (uint i = 0; i < 16; i++)
                {
                    IDXGIAdapter1 a1;
                    int hrEnum = _factory.EnumAdapters1(i, out a1);
                    if (hrEnum != 0 || a1 == null) break;

                    try
                    {
                        DXGI_ADAPTER_DESC1 desc;
                        if (a1.GetDesc1(out desc) != 0) continue;

                        ulong vram = (ulong)desc.DedicatedVideoMemory.ToUInt64();
                        string name = (desc.Description ?? "").Trim();
                        Trace.Add(string.Format("  适配器 {0}: {1}  dedicated={2:0.00} GB  luid=0x{3:X16}",
                            i, name, vram / 1073741824.0, desc.AdapterLuid));

                        if (vram > bestVram || _adapterName.Length == 0)
                        {
                            bestVram = vram;
                            _adapterName = name;
                            _dedicatedTotal = vram;
                            _adapterLuid = desc.AdapterLuid;
                        }
                    }
                    finally
                    {
                        // COM 接口用完必须显式释放：靠 GC 回收 RCW 会让 DXGI 的
                        // 适配器对象在进程里挂很久，常驻程序没必要白占这份内存。
                        try { Marshal.FinalReleaseComObject(a1); } catch (Exception) { }
                    }
                }

                Trace.Add("选中适配器: " + _adapterName + "  " + (_dedicatedTotal / 1073741824.0).ToString("0.00") + " GB");

                InitCounters();

                _initialized = true;
                if (_counters == null || _counters.Length == 0) _failed = false; // 允许后续重试
                Trace.Add("初始化结果: " + Available + (CounterError.Length > 0 ? "  计数器问题: " + CounterError : ""));
                return Available;
            }
            catch (Exception ex)
            {
                Trace.Add("异常: " + ex.GetType().Name + " " + ex.Message);
                _failed = true;
                return false;
            }
        }

        private void InitCounters()
        {
            try
            {
                // 诊断轨迹只服务于排障，别让它无限长下去
                if (Trace.Count > 200) Trace.Clear();

                if (_counters != null)
                {
                    for (int i = 0; i < _counters.Length; i++)
                    {
                        try { _counters[i].Dispose(); } catch (Exception) { }
                    }
                    _counters = null;
                }

                // GPU 是 V2 计数器：PerformanceCounterCategory.GetCategories() 未必列得出来，
                // 但按名字直接构造往往可以。所以先试名字，再退回枚举。
                var candidates = new List<string> { "GPU Adapter Memory" };
                try
                {
                    var cats = PerformanceCounterCategory.GetCategories();
                    Trace.Add("  GetCategories 返回 " + cats.Length + " 个集合");
                    for (int i = 0; i < cats.Length; i++)
                    {
                        string n = cats[i].CategoryName;
                        if (n != null && n.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0
                            && !candidates.Contains(n))
                        {
                            candidates.Add(n);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.Add("  GetCategories 异常: " + ex.GetType().Name + " " + ex.Message);
                }

                for (int c = 0; c < candidates.Count; c++)
                {
                    string catName = candidates[c];
                    try
                    {
                        var cat = new PerformanceCounterCategory(catName);
                        string[] instances = cat.GetInstanceNames();
                        int instCount = instances == null ? 0 : instances.Length;
                        Trace.Add(string.Format("  候选集合 '{0}': 实例 {1} 个", catName, instCount));
                        if (instCount == 0) continue;

                        string counter = FindDedicatedUsageCounter(cat);
                        if (counter == null)
                        {
                            Trace.Add("    没有 Dedicated Usage 计数器");
                            continue;
                        }

                        string luidKey = LuidKey(_adapterLuid);
                        var picked = new List<string>();
                        for (int i = 0; i < instances.Length; i++)
                        {
                            if (instances[i].IndexOf(luidKey, StringComparison.OrdinalIgnoreCase) >= 0)
                                picked.Add(instances[i]);
                        }
                        bool exact = picked.Count > 0;
                        if (!exact) picked.AddRange(instances);

                        var list = new List<PerformanceCounter>();
                        for (int i = 0; i < picked.Count; i++)
                        {
                            try { list.Add(new PerformanceCounter(catName, counter, picked[i], true)); }
                            catch (Exception ex2) { Trace.Add("    构造计数器失败: " + ex2.Message); }
                        }
                        if (list.Count == 0) continue;

                        // 立刻试读一次：构造成功不等于读得到
                        try { list[0].NextSample(); }
                        catch (Exception ex3)
                        {
                            Trace.Add("    试读失败: " + ex3.GetType().Name + " " + ex3.Message);
                            for (int i = 0; i < list.Count; i++) { try { list[i].Dispose(); } catch (Exception) { } }
                            continue;
                        }

                        _counterCategory = catName;
                        _counterName = counter;
                        _counters = list.ToArray();
                        _counterError = "";
                        Trace.Add(string.Format("计数器就绪: {0}\\{1}  实例 {2} 个（LUID 精确匹配={3}）",
                            catName, counter, _counters.Length, exact));
                        return;
                    }
                    catch (Exception ex)
                    {
                        Trace.Add(string.Format("  候选集合 '{0}' 不可用: {1}", catName, ex.Message));
                    }
                }

                _counterError = "未找到可读的 GPU Adapter Memory 计数器";
            }
            catch (Exception ex)
            {
                _counterError = ex.GetType().Name + " " + ex.Message;
                Trace.Add("计数器初始化异常: " + _counterError);
            }
        }

        private static string FindDedicatedUsageCounter(PerformanceCounterCategory cat)
        {
            try
            {
                var counters = cat.GetCounters();
                if (counters != null && counters.Length > 0)
                {
                    string fallback = null;
                    for (int i = 0; i < counters.Length; i++)
                    {
                        string n = counters[i].CounterName;
                        if (n == null) continue;
                        if (string.Equals(n, "Dedicated Usage", StringComparison.OrdinalIgnoreCase)) return n;
                        if (fallback == null
                            && (n.IndexOf("Dedicated", StringComparison.OrdinalIgnoreCase) >= 0
                                || n.IndexOf("专用", StringComparison.Ordinal) >= 0))
                        {
                            fallback = n;
                        }
                    }
                    if (fallback != null) return fallback;
                }
            }
            catch (Exception) { }

            // GetCounters() 对 V2 计数器常常返回空（PDH 能读、老 API 列不出来），
            // 所以这里直接按英文名硬试 —— 真正能否读由构造后的试读来判定。
            return "Dedicated Usage";
        }

        private static string LuidKey(long luid)
        {
            uint low = (uint)(luid & 0xFFFFFFFF);
            uint high = (uint)((luid >> 32) & 0xFFFFFFFF);
            return string.Format("luid_0x{0:X8}_0x{1:X8}", high, low);
        }

        /// <summary>采样一次。false 表示当前拿不到数据。</summary>
        public bool TrySample(out ulong usedBytes, out ulong totalBytes)
        {
            usedBytes = 0;
            totalBytes = _dedicatedTotal;

            if (_counters == null || _counters.Length == 0)
            {
                TryRebuildCounters();
                return false;
            }

            try
            {
                long sum = 0;
                for (int i = 0; i < _counters.Length; i++)
                {
                    var sample = _counters[i].NextSample();
                    if (sample.RawValue > 0) sum += sample.RawValue;
                }
                usedBytes = (ulong)Math.Max(0, sum);
                _consecutiveFailures = 0;
                return totalBytes > 0;
            }
            catch (Exception)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= 3) TryRebuildCounters();
                return false;
            }
        }

        // 重建计数器要枚举整个性能计数器注册表（PerformanceCounterCategory.GetCategories()
        // 是毫秒级甚至几十毫秒的重活）。原来的写法是「连续失败 3 次就重建」，
        // 在没有 GPU 计数器的机器上等于每 1.5 秒把系统扫一遍 —— 常驻程序里这是最贵的一处浪费。
        // 改成最多每 30 秒试一次。
        private DateTime _lastRebuildUtc = DateTime.MinValue;
        private const double RebuildIntervalSeconds = 30.0;

        private void TryRebuildCounters()
        {
            if (_failed) return;
            if ((DateTime.UtcNow - _lastRebuildUtc).TotalSeconds < RebuildIntervalSeconds) return;
            _lastRebuildUtc = DateTime.UtcNow;
            _consecutiveFailures = 0;
            InitCounters();
        }

        public void Dispose()
        {
            try
            {
                if (_counters != null)
                {
                    for (int i = 0; i < _counters.Length; i++)
                    {
                        try { _counters[i].Dispose(); } catch (Exception) { }
                    }
                    _counters = null;
                }
                if (_factory != null)
                {
                    try { Marshal.FinalReleaseComObject(_factory); } catch (Exception) { }
                    _factory = null;
                }
                if (_comInitialized) { CoUninitialize(); _comInitialized = false; }
            }
            catch (Exception) { }
            _initialized = false;
        }

        // ------------------------------------------------------------------
        // DXGI 互操作
        // ------------------------------------------------------------------

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public long AdapterLuid;
            public uint Flags;
        }

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr pUnknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);

            [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr pWindowHandle);
            [PreserveSig] int CreateSwapChain(IntPtr pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);

            [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 ppAdapter1);
            [PreserveSig] int IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr pUnknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);

            [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
            [PreserveSig] int GetDesc(IntPtr pDesc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long pUMDVersion);

            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
        }
    }
}
