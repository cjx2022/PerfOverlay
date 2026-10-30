using System;
using System.Runtime.InteropServices;

namespace PerfOverlay.Services
{
    /// <summary>
    /// CPU / 内存采样。
    ///
    /// 不用 PerformanceCounter 的原因：首次 NextValue 必为 0（要等一个采样周期），
    /// 计数器库损坏时会抛异常且耗时可达数十毫秒。GetSystemTimes + GlobalMemoryStatusEx
    /// 是同一份数据的内核直读版本，微秒级、无预热、无外部依赖。
    /// </summary>
    public sealed class SystemMetricsProvider
    {
        private ulong _prevIdle, _prevKernel, _prevUser;
        private bool _hasPrev;

        public double CpuPercent { get; private set; }
        public double MemoryPercent { get; private set; }
        public ulong MemoryTotalBytes { get; private set; }
        public ulong MemoryUsedBytes { get; private set; }
        public bool CpuAvailable { get; private set; }

        public SystemMetricsProvider()
        {
            CpuAvailable = false;
            Sample();   // 建立基线
        }

        public void Sample()
        {
            SampleCpu();
            SampleMemory();
        }

        private void SampleCpu()
        {
            FILETIME idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return;

            ulong i = idle.ToUInt64();
            ulong k = kernel.ToUInt64();
            ulong u = user.ToUInt64();

            if (!_hasPrev)
            {
                _prevIdle = i; _prevKernel = k; _prevUser = u;
                _hasPrev = true;
                return;
            }

            ulong dIdle = i - _prevIdle;
            ulong dKernel = k - _prevKernel;
            ulong dUser = u - _prevUser;

            _prevIdle = i; _prevKernel = k; _prevUser = u;

            // kernel 时间已含 idle，因此 total = kernel + user
            ulong total = dKernel + dUser;
            if (total == 0) return;

            double busy = (double)total - (double)dIdle;
            if (busy < 0) busy = 0;
            double pct = busy * 100.0 / (double)total;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;

            CpuPercent = pct;
            CpuAvailable = true;
        }

        private void SampleMemory()
        {
            var st = new MEMORYSTATUSEX();
            st.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref st)) return;

            MemoryPercent = st.dwMemoryLoad;
            MemoryTotalBytes = st.ullTotalPhys;
            MemoryUsedBytes = st.ullTotalPhys - st.ullAvailPhys;
        }

        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
            public ulong ToUInt64()
            {
                return ((ulong)dwHighDateTime << 32) | dwLowDateTime;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
