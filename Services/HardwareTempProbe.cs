using System;
using LibreHardwareMonitor.Hardware;

namespace PerfOverlay.Services
{
    /// <summary>
    /// CPU / GPU 温度探针，走 LibreHardwareMonitor（它自带已签名的内核驱动，
    /// 从 MSR / SMN 直读 DTS，这是 Windows 上唯一能拿到 Ryzen Tctl 的路子 ——
    /// ACPI 热区在 MSI 这块板子上根本没暴露，WMI 直接回「不支持」）。
    ///
    /// 代价有两个，都通过了设计规避：
    /// 1) 必须管理员 —— 浮窗本来就要跑 PresentMon，已经是管理员了；
    /// 2) 要加载内核驱动 —— 所以只有用户打开「温度」时才 Open()，关掉立刻 Close()，
    ///    默认状态下驱动不会加载、dll 不会加载、进程里不多一个句柄。
    ///
    /// 实测（Ryzen 5 5600 + B550M）：Core (Tctl/Tdie) 44.0 °C，CCD1 42.8 °C；
    /// 主板 SuperIO（Nuvoton NCT6687D）另有 CPU / System / VRM / PCH 等，这里不取。
    /// </summary>
    public sealed class HardwareTempProbe : IDisposable
    {
        private Computer _pc;
        private ISensor _cpuSensor;
        private ISensor _gpuSensor;

        private bool _tried;
        private bool _open;

        private DateTime _lastSampleUtc = DateTime.MinValue;
        private double _cpu = -1, _gpu = -1;
        private bool _cpuOk, _gpuOk;

        /// <summary>CPU 温度不必每 0.5 秒读一次：DTS 的精度也就 1 度，2 秒足够。</summary>
        private const double SampleIntervalSeconds = 2.0;

        public string Status { get; private set; }
        public string CpuSensorName { get; private set; }
        public string GpuSensorName { get; private set; }
        public bool Available { get { return _open; } }

        public HardwareTempProbe()
        {
            Status = "未初始化";
            CpuSensorName = "";
            GpuSensorName = "";
        }

        /// <summary>第一次真正需要温度时才加载驱动。失败结果会被记住，不反复折腾。</summary>
        public bool EnsureOpen()
        {
            if (_tried) return _open;
            _tried = true;

            try
            {
                _pc = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = false,   // 主板传感器对本浮窗没用，少开一路就少一轮 IO
                    IsControllerEnabled = false,
                    IsStorageEnabled = false,
                    IsMemoryEnabled = false,
                    IsNetworkEnabled = false
                };
                _pc.Open();
                _open = true;

                PickSensors();

                if (_cpuSensor == null && _gpuSensor == null)
                {
                    Status = "硬件监控已打开，但没找到温度传感器";
                }
                else
                {
                    Status = string.Format("温度就绪：CPU={0}，GPU={1}",
                        _cpuSensor != null ? CpuSensorName : "不可用",
                        _gpuSensor != null ? GpuSensorName : "不可用");
                }
                return _open;
            }
            catch (Exception ex)
            {
                Status = "温度读取失败：" + ex.GetType().Name + " " + ex.Message;
                Close();
                return false;
            }
        }

        /// <summary>挑传感器：AMD 认 Tctl/Tdie，Intel 认 CPU Package，GPU 认 GPU Core。</summary>
        private void PickSensors()
        {
            var cpuFallback = (ISensor)null;
            var gpuFallback = (ISensor)null;

            foreach (var hw in _pc.Hardware)
            {
                try { hw.Update(); } catch (Exception) { }

                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Temperature) continue;
                    string n = s.Name ?? "";

                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        if (n.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("CPU Package", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("Core (Tdie)", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (_cpuSensor == null) { _cpuSensor = s; CpuSensorName = n; }
                        }
                        else if (cpuFallback == null && n.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            cpuFallback = s;
                        }
                    }
                    else if (IsGpu(hw.HardwareType))
                    {
                        if (n.IndexOf("GPU Core", StringComparison.OrdinalIgnoreCase) >= 0
                            || string.Equals(n, "GPU", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_gpuSensor == null) { _gpuSensor = s; GpuSensorName = n; }
                        }
                        else if (gpuFallback == null && n.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            gpuFallback = s;
                        }
                    }
                }
            }

            if (_cpuSensor == null && cpuFallback != null) { _cpuSensor = cpuFallback; CpuSensorName = cpuFallback.Name; }
            if (_gpuSensor == null && gpuFallback != null) { _gpuSensor = gpuFallback; GpuSensorName = gpuFallback.Name; }
        }

        private static bool IsGpu(HardwareType t)
        {
            return t == HardwareType.GpuNvidia || t == HardwareType.GpuAmd
                || t == HardwareType.GpuIntel;
        }

        /// <summary>
        /// 读一次（内部按 2 秒节流，节流期间返回上次的值）。
        /// 返回值只表示「探针可用」，cpuOk / gpuOk 才是这两项各自有没有数。
        /// </summary>
        public bool TrySample(out double cpu, out double gpu, out bool cpuOk, out bool gpuOk)
        {
            cpu = _cpu;
            gpu = _gpu;
            cpuOk = _cpuOk;
            gpuOk = _gpuOk;

            if (!EnsureOpen()) return false;

            if ((DateTime.UtcNow - _lastSampleUtc).TotalSeconds < SampleIntervalSeconds) return true;
            _lastSampleUtc = DateTime.UtcNow;

            try
            {
                foreach (var hw in _pc.Hardware)
                {
                    try { hw.Update(); } catch (Exception) { }
                }

                if (_cpuSensor != null && _cpuSensor.Value.HasValue)
                {
                    _cpu = _cpuSensor.Value.Value;
                    _cpuOk = _cpu > 0.1;
                }

                if (_gpuSensor != null && _gpuSensor.Value.HasValue)
                {
                    _gpu = _gpuSensor.Value.Value;
                    _gpuOk = _gpu > 0.1;
                }
            }
            catch (Exception)
            {
                // 驱动被外部卸掉 / 显卡掉线：标记不可用，下次重新 Open
                Close();
                _tried = false;
                return false;
            }

            cpu = _cpu;
            gpu = _gpu;
            cpuOk = _cpuOk;
            gpuOk = _gpuOk;
            return true;
        }

        private void Close()
        {
            try { if (_pc != null) _pc.Close(); } catch (Exception) { }
            _pc = null;
            _open = false;
            _cpuSensor = null;
            _gpuSensor = null;
        }

        /// <summary>关掉「温度」开关时整体卸载：驱动和 dll 一起退掉。</summary>
        public void Dispose()
        {
            Close();
            _tried = false;
            Status = "已释放";
        }
    }
}
