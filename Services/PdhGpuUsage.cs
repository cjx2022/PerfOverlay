using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PerfOverlay.Services
{
    /// <summary>
    /// GPU 利用率：PDH 计数器 "\GPU Engine(*)\Utilization Percentage"。
    ///
    /// 数据源是 WDDM 图形内核的 VidSch（GPU 调度器），不经过任何厂商 SDK ——
    /// 这也是任务管理器自己的取数方式（它的导入表里有 pdh.dll，没有 nvml.dll、没有 atiadlxx.dll）。
    /// 所以 A / N / I 三家通用，且不需要管理员权限。
    ///
    /// 聚合规则（照任务管理器的口径，本机 zh-CN\Taskmgr.exe.mui 里官方 tooltip 原文
    /// 就是「所有 GPU 引擎的最高利用率」）：
    ///   1) 实例名形如 pid_29248_luid_0x00000000_0x0001101a_phys_0_eng_0_engtype_3d；
    ///   2) 先把同一 (LUID, phys, eng) 下各进程的值相加 = 该引擎的占用；
    ///   3) 再在这些引擎之间取最大值 = 整卡占用。
    /// 不是求和、也不是平均 —— 求和会把 3D 与 Copy 引擎叠加成 150%。
    /// </summary>
    public sealed class PdhGpuUsage : IDisposable
    {
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_MORE_DATA = 0x800007D2;
        private const uint ERROR_SUCCESS = 0;

        private IntPtr _query = IntPtr.Zero;
        private IntPtr _counter = IntPtr.Zero;
        private IntPtr _buffer = IntPtr.Zero;
        private uint _bufferSize;

        private bool _ok;
        private string _why = "未初始化";
        private long _luidFilter;          // 0 = 不过滤，取全机最忙引擎

        public string Status { get { return _why; } }
        public bool Available { get { return _ok; } }

        /// <summary>供探针核对：这两个结构必须是 16 / 24 字节，否则读数组会错位。</summary>
        public static int StructSizeValue { get { return Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE)); } }
        public static int StructSizeItem { get { return Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM_W)); } }

        /// <summary>最近一次聚合到的引擎数与被选中引擎的名字，供自检与 tooltip 说明。</summary>
        public string Detail { get; private set; }
        public int EngineCount { get; private set; }

        public PdhGpuUsage()
        {
            Detail = "";
        }

        /// <summary>只统计指定显卡（LUID）。传 0 表示不区分适配器。</summary>
        public void SetAdapter(long adapterLuid)
        {
            _luidFilter = adapterLuid;
        }

        public bool EnsureOpen()
        {
            if (_query != IntPtr.Zero) return _ok;

            uint rc = PdhOpenQueryW(null, IntPtr.Zero, out _query);
            if (rc != ERROR_SUCCESS)
            {
                _query = IntPtr.Zero;
                _why = string.Format("PDH 查询创建失败（0x{0:X8}）", rc);
                return false;
            }

            // 用 AddEnglishCounter：计数器名固定英文，不受系统语言影响
            rc = PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter);
            if (rc != ERROR_SUCCESS)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                _why = string.Format("GPU 引擎计数器不可用（0x{0:X8}）", rc);
                return false;
            }

            // 第一次采集只是建立基线，值还不成立，丢弃
            PdhCollectQueryData(_query);
            _ok = true;
            _why = "正常";
            return true;
        }

        /// <summary>
        /// 采样一次整卡利用率（0~100）。首次调用可能拿到 0，属正常（计数器需要两个采样点）。
        /// </summary>
        public bool TrySample(out double percent)
        {
            percent = 0;
            if (!EnsureOpen()) return false;

            uint rc = PdhCollectQueryData(_query);
            if (rc != ERROR_SUCCESS)
            {
                _why = string.Format("PDH 采集失败（0x{0:X8}）", rc);
                return false;
            }

            var engineSum = new Dictionary<string, double>(64);
            uint itemCount = 0;

            // 缓冲不够就扩容重取：一次典型全机有 300~500 个实例，需要几十 KB
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint size = _bufferSize;
                rc = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out itemCount, _buffer);

                if (rc == PDH_MORE_DATA)
                {
                    _bufferSize = size;
                    if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                    _buffer = Marshal.AllocHGlobal((int)_bufferSize);
                    continue;
                }

                if (rc != ERROR_SUCCESS)
                {
                    _why = string.Format("PDH 读取计数器数组失败（0x{0:X8}）", rc);
                    return false;
                }
                break;
            }

            if (_buffer == IntPtr.Zero || itemCount == 0)
            {
                _why = "没有 GPU 引擎实例";
                return false;
            }

            int stride = Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM_W));
            string bestKey = null;
            for (uint i = 0; i < itemCount; i++)
            {
                IntPtr p = new IntPtr(_buffer.ToInt64() + (long)i * stride);
                var item = (PDH_FMT_COUNTERVALUE_ITEM_W)Marshal.PtrToStructure(p, typeof(PDH_FMT_COUNTERVALUE_ITEM_W));
                if (item.szName == IntPtr.Zero) continue;

                string name = Marshal.PtrToStringUni(item.szName);
                if (string.IsNullOrEmpty(name)) continue;

                double v = ReadValue(item.FmtValue.CStatus, item.FmtValue.doubleValue);
                if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) continue;

                string key = EngineKey(name);
                if (key == null) continue;                  // LUID 不匹配（已按显卡过滤掉）

                double prev;
                if (engineSum.TryGetValue(key, out prev)) engineSum[key] = prev + v;
                else engineSum[key] = v;
            }

            EngineCount = engineSum.Count;
            if (engineSum.Count == 0)
            {
                // 桌面静止时全部引擎都是 0，这是真实值，不是失败
                percent = 0;
                Detail = "GPU 空闲（所有引擎 0%）";
                _why = "正常";
                _ok = true;
                return true;
            }

            double best = 0;
            foreach (var kv in engineSum)
            {
                if (kv.Value > best)
                {
                    best = kv.Value;
                    bestKey = kv.Key;
                }
            }

            if (best > 100) best = 100;
            percent = best;
            Detail = string.Format("{0} 个引擎，最忙：{1} {2:0}%", engineSum.Count, bestKey, best);
            _why = "正常";
            _ok = true;
            return true;
        }

        /// <summary>
        /// 从实例名抽出 (LUID, phys, eng) 作为聚合键。
        /// 返回 null 表示该实例不属于我们要统计的显卡。
        /// </summary>
        private string EngineKey(string instanceName)
        {
            // pid_29248_luid_0x00000000_0x0001101a_phys_0_eng_0_engtype_3d
            int pi = instanceName.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
            if (pi < 0) return instanceName;     // 老系统没有 luid 段，不做适配器过滤

            string rest = instanceName.Substring(pi + 6);

            if (_luidFilter != 0)
            {
                uint low = (uint)(_luidFilter & 0xFFFFFFFF);
                uint high = (uint)((_luidFilter >> 32) & 0xFFFFFFFF);
                string want = string.Format("0x{0:X8}_0x{1:X8}", high, low);
                if (rest.IndexOf(want, StringComparison.OrdinalIgnoreCase) != 0) return null;
            }

            return rest;
        }

        private static double ReadValue(uint cstatus, double value)
        {
            // PDH_CSTATUS_VALIDDATA = 0x00000000，PDH_CSTATUS_NEW_DATA = 0x00000001，
            // 两者都是有效数据；其余（NO_INSTANCE / NO_COUNTER / CALC_NEGATIVE…）直接丢弃。
            if (cstatus != 0 && cstatus != 1) return 0;
            return value;
        }

        public void Dispose()
        {
            if (_counter != IntPtr.Zero) _counter = IntPtr.Zero;
            if (_query != IntPtr.Zero)
            {
                try { PdhCloseQuery(_query); } catch (Exception) { }
                _query = IntPtr.Zero;
            }
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferSize = 0;
            }
            _ok = false;
        }

        // ---------------- P/Invoke ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double doubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE_ITEM_W
        {
            public IntPtr szName;
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath,
                                                         IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
                                                                ref uint bufferSize, out uint itemCount,
                                                                IntPtr buffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
