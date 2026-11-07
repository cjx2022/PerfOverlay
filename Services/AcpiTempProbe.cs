using System;
using System.Collections.Generic;
using System.Management;

namespace PerfOverlay.Services
{
    /// <summary>
    /// CPU 温度的零安装路径：读 ACPI 热区（root\WMI 的 MSAcpi_ThermalZoneTemperature，
    /// PDH 里的 "\Thermal Zone Information(*)\Temperature" 是同一份数据的另一种取法）。
    ///
    /// 它的价值在于：纯用户态、不需要管理员、不加载任何驱动，因此**不会被内存完整性（HVCI）
    /// 或「易受攻击驱动阻止列表」拦截**。代价是普适性取决于 BIOS 有没有实现 _TZ 热区 ——
    /// 笔记本通常有，台式 DIY 板子（本机 MSI B550M 实测）往往直接返回「不支持」。
    ///
    /// 所以它是「能用就白赚」的第一级：读到就走它，读不到再退到内核驱动那一路。
    /// </summary>
    public sealed class AcpiTempProbe : IDisposable
    {
        private bool _tried;
        private bool _ok;
        private string _why = "未探测";
        private string _sensorName = "";

        /// <summary>热区数量（多个热区时我们取最高的那个，通常是 CPU）。</summary>
        public int ZoneCount { get; private set; }

        public string Status { get { return _why; } }
        public string SensorName { get { return _sensorName; } }
        public bool Available { get { return _ok; } }

        /// <summary>探测一次并缓存结论（ACPI 热区不会中途出现/消失，不必反复试）。</summary>
        public bool Probe()
        {
            if (_tried) return _ok;
            _tried = true;

            double t;
            string why;
            if (ReadOnce(out t, out why))
            {
                _ok = true;
                _why = "正常";
                return true;
            }

            _ok = false;
            _why = why;
            return false;
        }

        /// <summary>读当前 CPU 温度。失败时 why 说明原因（不支持 / 无实例 / 权限）。</summary>
        public bool TryRead(out double celsius, out string why)
        {
            if (Probe() == false)
            {
                celsius = 0;
                why = _why;
                return false;
            }
            return ReadOnce(out celsius, out why);
        }

        private bool ReadOnce(out double celsius, out string why)
        {
            celsius = 0;
            why = "";

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature"))
                using (ManagementObjectCollection col = searcher.Get())
                {
                    var zones = new List<double>();
                    string bestName = "";

                    foreach (ManagementObject mo in col)
                    {
                        try
                        {
                            object raw = mo["CurrentTemperature"];
                            if (raw == null) continue;

                            // WMI 给的是十分之一开尔文
                            double c = Convert.ToDouble(raw) / 10.0 - 273.15;
                            if (c > -40 && c < 150)
                            {
                                zones.Add(c);
                                if (zones.Count == 1) bestName = Convert.ToString(mo["InstanceName"]);
                            }
                        }
                        catch (Exception) { }
                        finally { mo.Dispose(); }
                    }

                    ZoneCount = zones.Count;
                    if (zones.Count == 0)
                    {
                        why = "固件没有暴露 ACPI 温度热区";
                        return false;
                    }

                    celsius = zones[0];
                    for (int i = 1; i < zones.Count; i++) if (zones[i] > celsius) celsius = zones[i];
                    _sensorName = bestName;

                    // 明显是主板/环境温度的读数（< 20℃）对 CPU 没参考价值
                    if (celsius < 20)
                    {
                        why = string.Format("ACPI 热区读数异常（{0:0.0} ℃），不像 CPU 温度", celsius);
                        return false;
                    }
                    return true;
                }
            }
            catch (ManagementException ex)
            {
                // 典型：Not supported（BIOS 没实现 _TZ）
                why = "ACPI 热区不可用：" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                why = "ACPI 热区读取失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>无持久资源（每次查询即建即弃 WMI 对象），接口对齐用。</summary>
        public void Dispose() { }
    }
}
