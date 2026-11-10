using System;
using System.IO;
using System.Threading;

namespace PerfOverlay.Services
{
    public sealed class MetricsSnapshot
    {
        public double CpuPercent;
        public bool CpuOk;

        public double MemoryPercent;
        public ulong MemoryUsed;
        public ulong MemoryTotal;

        public double Fps;
        public double LowFps;
        public bool FpsOk;
        public int FrameCount;
        public string FpsMessage = "";
        public string TargetProcess = "";

        public double VramPercent;
        public ulong VramUsed;
        public ulong VramBudget;
        public bool VramOk;

        // ---- 0.9.4 新增（默认关闭）----
        /// <summary>时:分。空串表示不显示时间。</summary>
        public string ClockText = "";
        public double GpuPercent;
        public bool GpuOk;
        public string GpuDetail = "";

        /// <summary>CT = CPU 温度（ACPI 热区优先；读不到时走 PawnIO：AMD 读 SMN、Intel 读 MSR）。</summary>
        public double TempCpuC;
        public bool TempCpuOk;
        public string TempCpuDetail = "";

        /// <summary>
        /// CT 的取数状态，只给界面判断要不要弹「安装温度组件」用：
        /// not_installed / no_access / unsupported / read_failed / 空=正常或未开。
        /// </summary>
        public string CpuTempState = "";

        /// <summary>GT = GPU 温度（优先 NVML，读不到再用 LibreHardwareMonitor 的 GPU Core）。</summary>
        public double TempGpuC;
        public bool TempGpuOk;
        public string TempGpuDetail = "";
    }

    /// <summary>
    /// 采集调度：单条后台线程串行采样，避免定时器并发与 COM 归属漂移。
    /// UI 线程只消费快照，不做任何测量。
    /// </summary>
    public sealed class MonitorHost : IDisposable
    {
        private Thread _thread;
        private volatile bool _stop;
        private AppSettings _settings;
        private readonly object _settingsGate = new object();

        public readonly SystemMetricsProvider Sys = new SystemMetricsProvider();
        public readonly GpuMemoryProbe Gpu = new GpuMemoryProbe();
        public readonly PresentMonEngine Frames;

        // GPU 利用率：PDH「GPU Engine」计数器，A/N/I 通用，与任务管理器同源。
        private PdhGpuUsage _pdhUsage;
        // GPU 温度：WDDM 适配器性能数据，免驱动免权限，A/N 通用。
        private WddmGpuTemp _wddmTemp;
        // NVML 降为兜底：只有上面两条路都失败（或非 Windows 常见显卡）时才去加载 nvml.dll。
        private NvmlProbe _nvml;
        private bool _nvmlTried;
        // CPU 温度：第一级读 ACPI 热区（系统自带 WMI，零驱动零安装）。
        private AcpiTempProbe _temp;
        /// <summary>ACPI 兜底路径的「读数是否长时间不动」监控（见 FreezeWatch）。</summary>
        private readonly FreezeWatch _acpiFreeze = new FreezeWatch();
        // CPU 温度：第二级 PawnIO + AMDFamily17 读 SMN。只有第一级失败、且用户真的
        // 点过「安装温度组件」之后才会被创建 —— 没装的机器上这个对象连影子都没有。
        private PawnIoCpuTemp _pawnTemp;
        /// <summary>保护 _pawnTemp 的创建/替换/释放：采样线程与 UI 线程都会碰它。</summary>
        private readonly object _tempGate = new object();

        private bool _framesStarted;
        private string _framesWhy = "";
        private DateTime _starvedSince = DateTime.MinValue;
        private readonly int _selfPid = System.Diagnostics.Process.GetCurrentProcess().Id;

        /// <summary>
        /// 打开帧采集现场日志（每 5 秒一行）。默认关闭：
        /// 常态运行没必要每 5 秒开一次文件，也没必要把「前台在用什么程序」写进 %TEMP%。
        /// 排障时用 --diag 启动即可。
        /// </summary>
        public bool Diagnostic { get; set; }

        private string _frameDiagPath;

        /// <summary>在后台线程触发。</summary>
        public event EventHandler<MetricsSnapshot> Updated;

        public MonitorHost(string presentMonPath)
        {
            Frames = new PresentMonEngine(presentMonPath);
        }

        public string FramesDiagnostic
        {
            get
            {
                if (_framesWhy.Length > 0) return _framesWhy;
                if (!Frames.PresentMonFound) return "缺少 PresentMon.exe（程序目录 tools 下）";
                if (!PrivilegeHelper.CanCaptureFrames) return "帧数采集需要管理员权限，或把自己加入「Performance Log Users」组";
                if (!Frames.IsRunning) return "PresentMon 未运行" + (Frames.LastError.Length > 0 ? "：" + Frames.LastError : "");
                return "正常";
            }
        }

        public bool FramesRunning { get { return Frames.IsRunning; } }

        /// <summary>
        /// CT 当前的取数状态，给设置面板判断要不要显示感叹号。
        /// 只读，不会触发安装 —— 驱动不在就是 not_installed，仅此而已。
        ///
        /// 先看「上一帧到底有没有读到温度」：能读到就报 ready。感叹号的语义是
        /// **「CT 读不出来」**，不是「你还没装我的驱动」—— 用户的 BIOS 已经有热区、
        /// 温度一直在跳的机器上弹一个「去装组件」，纯属骚扰。
        /// </summary>
        public string CpuTempState
        {
            get
            {
                if (_cpuTempLive) return "ready";
                if (_pawnTemp != null) return _pawnTemp.DriverState;
                if (_temp != null && _temp.Probe()) return "ready";
                return PawnIoCpuTemp.IsSupportedCpu ? "not_installed" : "unsupported";
            }
        }

        /// <summary>上一帧 CT 是否读到了有效值（任一来源）。CpuTempState 用它压掉误报的感叹号。</summary>
        private volatile bool _cpuTempLive;

        /// <summary>装完驱动后调用：让温度探针重新走一遍（包括重新打开设备）。</summary>
        public void ResetCpuTempProbe()
        {
            // 采样线程正在用 _pawnTemp 时不能把它换掉：句柄释放到一半会被读到，结果是
            // ObjectDisposedException（会被吞掉，但那一轮 CT 就变 -- 了）。
            lock (_tempGate)
            {
                if (_pawnTemp != null)
                {
                    _pawnTemp.Reset();
                }
                else if (PawnIoCpuTemp.IsSupportedCpu)
                {
                    _pawnTemp = new PawnIoCpuTemp();
                }
                if (_pawnTemp != null) _pawnTemp.Probe();
            }
        }

        public void Start(AppSettings settings)
        {
            lock (_settingsGate) { _settings = settings.Clone(); }

            // 显存 probe 建立一次对象，之后只轮询
            Gpu.Initialize();

            // 帧诊断日志超过 512 KB 就重开，避免长期运行无限增长
            _frameDiagPath = Path.Combine(Path.GetTempPath(), "perfoverlay-frames.log");
            if (Diagnostic)
            {
                try
                {
                    if (File.Exists(_frameDiagPath) && new FileInfo(_frameDiagPath).Length > 512 * 1024)
                        File.Delete(_frameDiagPath);
                }
                catch (Exception) { }
            }

            // 权限够才启动 PresentMon：否则子进程会立刻报 access denied 并刷屏
            if (Frames.PresentMonFound && PrivilegeHelper.CanCaptureFrames)
            {
                _framesStarted = Frames.Start();
                if (!_framesStarted && Frames.LastError.Length > 0) _framesWhy = Frames.LastError;
            }

            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "PerfOverlay.Sampler" };
            _thread.Start();
        }

        public void ApplySettings(AppSettings settings)
        {
            lock (_settingsGate) { _settings = settings.Clone(); }
        }

        private void Loop()
        {
            int tick = 0;
            while (!_stop)
            {
                AppSettings s;
                lock (_settingsGate) { s = _settings; }
                int interval = s != null ? s.RefreshMs : 500;
                if (interval < 100) interval = 100;

                try
                {
                    Sys.Sample();

                    int fgPid = ForegroundWatcher.CurrentPid;
                    if (fgPid == _selfPid)
                    {
                        // 自己在前台时保持上一个目标，避免点开浮窗后数字掉成 "—"
                        fgPid = 0;
                    }
                    if (fgPid > 0)
                    {
                        Frames.SetTarget(fgPid, ForegroundWatcher.ProcessNameOf(fgPid));
                    }

                    if (tick % 20 == 0) Frames.SweepQueues();

                    // 温度 / GPU 利用率：开关关着就什么都不做（连 dll 都不加载）
                    EnsureProbes(s);

                    // PresentMon 进程必须能自愈：它可能因为会话被顶掉、被外部杀掉、
                    // 或启动瞬间失败而消失。原来的实现只在 Start() 里拉一次，
                    // 之后进程没了就永远显示 N/A，而且没有任何线索。
                    if (tick % 10 == 0) EnsureFramesAlive();

                    var snap = Build(s);

                    // 每 ~5 秒把帧采集现场写一行日志：目标 PID、PresentMon 存活、
                    // 队列里各 PID 的帧数、失败原因。排障全靠这一行。
                    if (tick % 10 == 0) WriteFrameDiag(snap);

                    RecycleIfStarved(snap);

                    var h = Updated;
                    if (h != null)
                    {
                        try { h(this, snap); } catch (Exception) { }
                    }
                }
                catch (Exception) { }

                tick++;
                try { Thread.Sleep(interval); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 按开关管理探针生命周期。三个指标各有一条「普适」主路径，全部不依赖厂商 SDK：
        ///   GPU 利用率 → PDH GPU Engine（任务管理器同源）
        ///   GPU 温度   → WDDM 适配器性能数据（免驱动免权限）
        ///   CPU 温度   → ACPI 热区，读不到再上 LHM + PawnIO
        /// 开关全关时立刻 Dispose —— 「默认关闭不占内存」不是口号，
        /// 关掉后进程里不应该留下 nvml.dll、监控 dll 和它的驱动句柄。
        /// </summary>
        private void EnsureProbes(AppSettings s)
        {
            if (s == null) return;

            // ---- GPU 利用率 ----
            if (s.ShowGpu)
            {
                if (_pdhUsage == null)
                {
                    _pdhUsage = new PdhGpuUsage();
                    _pdhUsage.SetAdapter(Gpu.AdapterLuid);
                }
                _pdhUsage.EnsureOpen();
            }
            else if (_pdhUsage != null)
            {
                _pdhUsage.Dispose();
                _pdhUsage = null;
            }

            // ---- GPU 温度 ----
            if (s.ShowGpuTemp)
            {
                if (_wddmTemp == null)
                {
                    _wddmTemp = new WddmGpuTemp();
                    _wddmTemp.Attach(Gpu.AdapterLuid);
                }
            }
            else if (_wddmTemp != null)
            {
                _wddmTemp.Dispose();
                _wddmTemp = null;
            }

            // ---- CPU 温度 ----
            if (s.ShowCpuTemp)
            {
                if (_temp == null) _temp = new AcpiTempProbe();

                // 驱动这条线只要架构在支持范围内就建起来试一次 —— **不能等 ACPI 失败才建**。
                // 0.9.6 之前正是那样写的，后果很隐蔽：BIOS 一旦提供了热区（哪怕是恒定 28℃
                // 的占位值），_pawnTemp 就永远为 null，用户装了驱动也一次都不会被调用。
                //
                // 注意这里 **不会** 触发安装：驱动不存在时 PawnIoBridge 打开设备会立刻失败
                // （ERROR_FILE_NOT_FOUND），结论由 Probe() 缓存，之后每帧只是一次 bool 返回。
                bool pawnReady = false;
                if (PawnIoCpuTemp.IsSupportedCpu)
                {
                    lock (_tempGate)
                    {
                        if (_pawnTemp == null) _pawnTemp = new PawnIoCpuTemp();
                        pawnReady = _pawnTemp.Probe();
                    }
                }

                // ACPI 只在驱动不可用时兜底。这里仍先探一次：没装驱动的机器靠它才出得了温度。
                if (!pawnReady) _temp.Probe();
            }
            else
            {
                if (_temp != null)
                {
                    _temp.Dispose();
                    _temp = null;
                }
                lock (_tempGate)
                {
                    if (_pawnTemp != null)
                    {
                        _pawnTemp.Dispose();
                        _pawnTemp = null;
                    }
                }
            }

            // 两条普适路径都关掉时，NVML 缓存也一并退掉
            if (!s.ShowGpu && !s.ShowGpuTemp && _nvml != null)
            {
                _nvml.Dispose();
                _nvml = null;
                _nvmlTried = false;
            }
        }

        private MetricsSnapshot Build(AppSettings s)
        {
            var snap = new MetricsSnapshot
            {
                CpuPercent = Sys.CpuPercent,
                CpuOk = Sys.CpuAvailable,
                MemoryPercent = Sys.MemoryPercent,
                MemoryUsed = Sys.MemoryUsedBytes,
                MemoryTotal = Sys.MemoryTotalBytes
            };

            if (s != null && s.ShowClock) snap.ClockText = DateTime.Now.ToString("HH:mm");
            if (s != null && s.ShowGpu) FillGpuUsage(snap);
            if (s != null && s.ShowGpuTemp) FillGpuTemp(snap);
            if (s != null && s.ShowCpuTemp) FillCpuTemp(snap);

            ulong used, total;
            if (Gpu.TrySample(out used, out total))
            {
                snap.VramOk = total > 0;
                snap.VramUsed = used;
                snap.VramBudget = total;
                snap.VramPercent = total > 0 ? (used * 100.0 / total) : 0;
                if (snap.VramPercent > 100) snap.VramPercent = 100;
            }

            var f = Frames.Snapshot();
            snap.Fps = f.Fps;
            snap.LowFps = f.LowFps;
            snap.FrameCount = f.FrameCount;
            snap.FpsOk = f.Available && PrivilegeHelper.CanCaptureFrames && Frames.IsRunning;
            snap.TargetProcess = f.TargetProcess;
            snap.FpsMessage = f.Message;
            if (!snap.FpsOk && snap.FpsMessage.Length == 0) snap.FpsMessage = FramesDiagnostic;

            return snap;
        }

        /// <summary>
        /// GPU 利用率：主路 PDH（\GPU Engine，任务管理器同源，A/N/I 通用，取最忙引擎）；
        /// 只有 PDH 不可用时才退到 NVML（N 卡专有）。
        /// </summary>
        private void FillGpuUsage(MetricsSnapshot snap)
        {
            if (_pdhUsage != null)
            {
                double pct;
                if (_pdhUsage.TrySample(out pct))
                {
                    snap.GpuOk = true;
                    snap.GpuPercent = pct;
                    string card = Gpu.AdapterName;
                    snap.GpuDetail = (card.Length > 0 ? card + " " : "") + "整体利用率（"
                                     + (_pdhUsage.Detail.Length > 0 ? _pdhUsage.Detail : "最忙引擎") + "）";
                    return;
                }

                snap.GpuDetail = _pdhUsage.Status;
            }

            // 兜底：NVML
            double temp, util;
            bool tempOk, utilOk;
            if (TryNvml(out temp, out util, out tempOk, out utilOk) && utilOk)
            {
                snap.GpuOk = true;
                snap.GpuPercent = util;
                snap.GpuDetail = (_nvml.DeviceName.Length > 0 ? _nvml.DeviceName + " 整体利用率（NVML）" : "GPU 整体利用率（NVML）");
            }
            else if (snap.GpuDetail.Length == 0)
            {
                snap.GpuDetail = "GPU 利用率不可用";
            }
        }

        /// <summary>
        /// GPU 温度：主路 WDDM（KMTQAITYPE_ADAPTERPERFDATA，免驱动免权限，A/N 通用），
        /// 失败再退 NVML。虚拟机 / 无传感器显卡会两者都失败，原因写进 tooltip。
        /// </summary>
        private void FillGpuTemp(MetricsSnapshot snap)
        {
            if (_wddmTemp != null)
            {
                double t;
                if (_wddmTemp.TryRead(out t))
                {
                    snap.TempGpuOk = true;
                    snap.TempGpuC = t;
                    string card = Gpu.AdapterName;
                    snap.TempGpuDetail = (card.Length > 0 ? card + " " : "") + "核心温度（WDDM）";
                    return;
                }
                snap.TempGpuDetail = _wddmTemp.Status;
            }

            double temp, util;
            bool tempOk, utilOk;
            if (TryNvml(out temp, out util, out tempOk, out utilOk) && tempOk)
            {
                snap.TempGpuOk = true;
                snap.TempGpuC = temp;
                snap.TempGpuDetail = _nvml.DeviceName.Length > 0
                    ? _nvml.DeviceName + " 核心温度（NVML）"
                    : "GPU 核心温度（NVML）";
            }
            else if (snap.TempGpuDetail.Length == 0)
            {
                snap.TempGpuDetail = "GPU 温度不可用";
            }
        }

        /// <summary>
        /// CPU 温度：两级，**驱动优先、ACPI 兜底**。
        ///
        /// 顺序在 0.9.6 之前是反的（先 ACPI），代价很实在：ACPI 热区是固件转述的数，
        /// 大量台式板/笔记本 BIOS 直接回一个恒定占位值（用户实测恒为 28℃，装完驱动也不变）。
        /// 那种值能通过合理性闸门，于是链路在第一级就 return，**用户装的驱动一次都没被调用**，
        /// 表现出来就是「装了组件，温度还是一动不动」。既然用户装驱动的目的就是要真 die 读数，
        /// 那就该让 SMN 先答；ACPI 只在 SMN 不可用时兜底。
        ///
        /// 两条都失败时把结论写进快照，UI 据此决定要不要给感叹号：
        ///   not_installed / no_access / module_failed / read_failed —— 见 PawnIoCpuTemp.DriverState
        ///   unsupported —— 架构不在支持范围，且 ACPI 也没有
        /// </summary>
        private void FillCpuTemp(MetricsSnapshot snap)
        {
            double t;
            string why = "";
            string pawnWhy = "";
            string pawnState = "";

            // ---- 第一级：PawnIO（读 die 内温度传感器）----
            lock (_tempGate)
            {
                if (_pawnTemp != null)
                {
                    _pawnTemp.Probe();
                    if (_pawnTemp.TryRead(out t, out why))
                    {
                        snap.TempCpuOk = true;
                        snap.TempCpuC = t;
                        snap.TempCpuDetail = "CPU 温度（SMN 寄存器，PawnIO）";
                        snap.CpuTempState = "ready";
                        _cpuTempLive = true;
                        _acpiFreeze.Reset();   // 有真读数了，ACPI 那条线不再关注
                        return;
                    }

                    pawnWhy = _pawnTemp.Status;
                    pawnState = _pawnTemp.DriverState;
                }
            }

            // ---- 第二级：ACPI 热区（零依赖，但可能是固件固定值）----
            if (_temp != null && _temp.TryRead(out t, out why))
            {
                bool frozen = _acpiFreeze.Add(t);
                snap.TempCpuOk = true;
                snap.TempCpuC = t;
                snap.TempCpuDetail = "CPU 温度（ACPI 热区"
                    + (_temp.SensorName.Length > 0 ? "：" + _temp.SensorName : "")
                    + (frozen ? "；读数已 3 分钟未变化，多为固件固定值" : "")
                    + "）";
                snap.CpuTempState = "ready";
                _cpuTempLive = true;
                return;
            }

            string acpiWhy = why;

            // ---- 两级都没走通 ----
            _cpuTempLive = false;
            snap.CpuTempState = pawnState.Length > 0
                ? pawnState
                : (PawnIoCpuTemp.IsSupportedCpu ? "not_installed" : "unsupported");

            if (pawnWhy.Length > 0) snap.TempCpuDetail = pawnWhy;
            else if (acpiWhy.Length > 0) snap.TempCpuDetail = acpiWhy;
        }

        /// <summary>
        /// ACPI 读数冻结监控。ACPI 热区是固件给的数，恒定的占位值能骗过合理性闸门
        /// （只拦 &lt;20℃），于是「一直 28℃」这种假值会长期冒充 CPU 温度。
        /// 这里只做标注、不改数值：连续 3 分钟一字不差就在 tooltip 里点明，
        /// 让用户知道该怀疑固件而不是怀疑程序。
        /// </summary>
        private sealed class FreezeWatch
        {
            private const int WindowMs = 180000;
            private double _last = double.NaN;
            private int _since;

            /// <summary>记一次读数。返回 true 表示该值已经连续 WindowMs 没有变化。</summary>
            public bool Add(double v)
            {
                int now = Environment.TickCount;
                if (double.IsNaN(_last) || Math.Abs(v - _last) > 0.0001)
                {
                    _last = v;
                    _since = now;
                    return false;
                }
                // TickCount 约 49 天回绕一次，unchecked 让差值自然处理符号
                return unchecked(now - _since) > WindowMs;
            }

            public void Reset()
            {
                _last = double.NaN;
                _since = 0;
            }
        }

        /// <summary>
        /// NVML 懒加载：只在 PDH / WDDM 都失败时才碰 nvml.dll，
        /// 这样 N 卡用户走新路径时进程里根本不会出现这个 dll。
        /// </summary>
        private bool TryNvml(out double temp, out double util, out bool tempOk, out bool utilOk)
        {
            temp = util = 0;
            tempOk = utilOk = false;

            if (_nvml == null && !_nvmlTried)
            {
                _nvmlTried = true;
                _nvml = new NvmlProbe();
                _nvml.EnsureInit();
            }
            if (_nvml == null) return false;

            return _nvml.TryRead(out temp, out util, out tempOk, out utilOk);
        }

        /// <summary>PresentMon 掉了就拉起来，每 5 秒最多试一次（由调用点的 tick 控制）。</summary>
        private void EnsureFramesAlive()
        {
            try
            {
                if (!Frames.PresentMonFound) return;
                if (!PrivilegeHelper.CanCaptureFrames) return;
                if (Frames.IsRunning) return;
                Frames.Start();
            }
            catch (Exception) { }
        }

        private void WriteFrameDiag(MetricsSnapshot snap)
        {
            if (!Diagnostic) return;
            try
            {
                string line = string.Format(
                    "{0:HH:mm:ss} tgt={1}({2}) pmRun={3} avail={4} fps={5:0.0} low={6:0.0} " +
                    "n={7} lines={8} sess={9} msg=[{10}] err=[{11}] se=[{12}] q={13}",
                    DateTime.Now, Frames.TargetPid, snap.TargetProcess, Frames.IsRunning,
                    snap.FpsOk, snap.Fps, snap.LowFps, snap.FrameCount,
                    Frames.LinesSeen, Frames.SessionName,
                    snap.FpsMessage, Frames.LastError, Frames.StderrText, Frames.QueueStat);

                File.AppendAllText(_frameDiagPath, line + Environment.NewLine);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// PresentMon 活着但连续 60 秒一帧都没收到 —— 会话在建立时就被拦截了
        /// （例如游戏反作弊启动在先）。这种会话不会自己恢复，必须拆掉重建。
        /// 退出游戏后靠这条自动恢复，用户不用手动重启浮窗。
        /// </summary>
        private void RecycleIfStarved(MetricsSnapshot snap)
        {
            try
            {
                if (!Frames.IsRunning) { _starvedSince = DateTime.MinValue; return; }

                if (snap.FrameCount > 0)
                {
                    _starvedSince = DateTime.MinValue;
                    return;
                }

                if (_starvedSince == DateTime.MinValue)
                {
                    _starvedSince = DateTime.UtcNow;
                    return;
                }

                if ((DateTime.UtcNow - _starvedSince).TotalSeconds < 60) return;

                _starvedSince = DateTime.MinValue;
                Frames.Stop();
                Frames.Start();
            }
            catch (Exception) { }
        }

        public void Stop()
        {
            _stop = true;
            try { if (_thread != null) _thread.Join(1500); } catch (Exception) { }
            Frames.Stop();
        }

        public void Dispose()
        {
            Stop();
            Frames.Dispose();
            Gpu.Dispose();
            if (_pdhUsage != null) { _pdhUsage.Dispose(); _pdhUsage = null; }
            if (_wddmTemp != null) { _wddmTemp.Dispose(); _wddmTemp = null; }
            if (_nvml != null) { _nvml.Dispose(); _nvml = null; }
            if (_temp != null) { _temp.Dispose(); _temp = null; }
            if (_pawnTemp != null) { _pawnTemp.Dispose(); _pawnTemp = null; }
        }

        /// <summary>
        /// PresentMon 的位置：程序目录有 tools\PresentMon.exe 就用它（便于自行换版本），
        /// 否则用程序集里内嵌的那一份释放出来的副本（单 exe 发布形态）。
        /// </summary>
        public static string PresentMonPath()
        {
            return PresentMonProvider.Resolve();
        }
    }
}
