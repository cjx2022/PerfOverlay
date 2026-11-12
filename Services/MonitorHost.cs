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

        /// <summary>CT = CPU 温度（驱动优先；驱动不可用或读数成死值时回退 ACPI 热区）。</summary>
        public double TempCpuC;
        public bool TempCpuOk;
        public string TempCpuDetail = "";

        /// <summary>上一帧 CT 实际用的来源："pawn"（SMN/MSR）或 "acpi"（热区）。空 = 没读到。</summary>
        public string TempCpuSource = "";
        /// <summary>该来源的读数是否已被判定为「长时间不变」。UI 不用它，留给 --selftest 与排障。</summary>
        public bool TempCpuSuspect;

        /// <summary>
        /// CT 的取数状态，只给界面判断要不要弹「安装温度组件」用：
        /// not_installed / no_access / unsupported / read_failed / 空=正常或未开。
        /// </summary>
        public string CpuTempState = "";

        /// <summary>GT = GPU 温度（优先 NVML，读不到再用 LibreHardwareMonitor 的 GPU Core）。</summary>
        public double TempGpuC;
        public bool TempGpuOk;
        public string TempGpuDetail = "";

        // ---- 显示延迟（默认关闭，用户自己在面板里勾）----
        /// <summary>中位显示延迟（毫秒）：Present 调用 → 该帧被扫到屏幕上。</summary>
        public double LatencyMs;
        /// <summary>P95，给 tooltip 用；浮窗上只显示中位数。</summary>
        public double LatencyP95Ms;
        public bool LatencyOk;
        public int LatencySamples;
        public string LatencyDetail = "";
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
        /// <summary>驱动路径的同一套监视：驱动也可能坏成「每次都回同一个值」。</summary>
        private readonly FreezeWatch _pawnFreeze = new FreezeWatch();
        /// <summary>上一帧选中的取数来源（"pawn" / "acpi"），用于切换后的保持期。</summary>
        private string _tempChoice = "";
        private int _tempChoiceAt;
        /// <summary>ACPI 每 2 秒才真读一次：WMI 查询比一次 SMN 读慢两个数量级，没必要每帧都做。</summary>
        private int _acpiLastRead;
        private double _acpiCachedC;
        private bool _acpiCachedOk;
        private bool _acpiFrozenState;

        // CPU 温度：第二级 PawnIO + AMDFamily17 读 SMN。只要架构在支持范围内就建起来试一次
        // （不能等 ACPI 失败才建，否则 BIOS 给个恒定占位值时驱动永远轮不到）。
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
        /// 语义是**「CT 读不到可信温度」**，不是「你还没装我的驱动」：
        ///   · 读到可信值 → ready（BIOS 热区正常工作的机器上不该弹「去装组件」）；
        ///   · 只有一路能读、但那一路是恒定死值（典型：固件 28℃ 占位）→ 报装驱动能解决的状态，
        ///     因为这时候用户看到的数就是假的，提示才有意义。
        /// </summary>
        public string CpuTempState
        {
            get
            {
                if (_cpuTempState != null) return _cpuTempState;
                if (_temp != null && _temp.Probe()) return "ready";
                return PawnIoCpuTemp.IsSupportedCpu ? "not_installed" : "unsupported";
            }
        }

        /// <summary>上一帧的 CT 取数状态（由采样线程写入）。null = 还没采过。</summary>
        private volatile string _cpuTempState;

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

            // 刚装完驱动，之前的「恒定值」结论全部作废：不重置的话，新装的驱动要等
            // 监视窗口（4 分钟）走完才可能被选中，用户会以为「装了没用」。
            _pawnFreeze.Reset();
            _acpiFreeze.Reset();
            _tempChoice = "";
            _tempChoiceAt = 0;
            _acpiLastRead = 0;
            _cpuTempState = null;
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
                Frames.CaptureLatency = _settings != null && _settings.ShowDisplayLatency;
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
            // 延迟这一列要不要解析，直接告诉引擎：关掉时每帧少切一次列。
            // 只切解析开关，不重启 PresentMon —— v1_metrics 本来每行就带着这一列。
            Frames.CaptureLatency = settings != null && settings.ShowDisplayLatency;
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
                if (PawnIoCpuTemp.IsSupportedCpu)
                {
                    lock (_tempGate)
                    {
                        if (_pawnTemp == null) _pawnTemp = new PawnIoCpuTemp();
                        _pawnTemp.Probe();
                    }
                }

                // ACPI 这一路现在**始终探一次**（不只是驱动失败时）：它是判定「驱动读数是不是
                // 死值」的对照样本 —— 没有第二路的值，就无从知道某一路的恒定是异常还是空闲。
                // 代价只有每 2 秒一次 WMI 查询（见 FillCpuTemp 里的采样节流）。
                _temp.Probe();
            }
            else
            {
                if (_temp != null)
                {
                    _temp.Dispose();
                    _temp = null;
                }
                // 关掉后把「死值」结论一起清掉：下次再打开时重新观察，
                // 不能拿上次会话的冻结状态去判定新会话（时间戳对不上）。
                _acpiFreeze.Reset();
                _pawnFreeze.Reset();
                _acpiLastRead = 0;
                _acpiCachedOk = false;
                _acpiFrozenState = false;
                _tempChoice = "";
                _tempChoiceAt = 0;
                _cpuTempState = null;
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

            // 显示延迟：只有开了才有内容。拿不到就把原因给出去 —— 这条链路
            // 在独立翻转 / 覆盖层 / Electron 上本来就追不到显示环节，不是故障。
            if (s != null && s.ShowDisplayLatency)
            {
                snap.LatencyOk = f.LatencyOk && PrivilegeHelper.CanCaptureFrames && Frames.IsRunning;
                snap.LatencyMs = f.LatencyP50Ms;
                snap.LatencyP95Ms = f.LatencyP95Ms;
                snap.LatencySamples = f.LatencySamples;
                if (snap.LatencyOk)
                {
                    snap.LatencyDetail = string.Format(
                        "显示延迟：Present 到出屏的中位数 {0:0.0} ms（P95 {1:0.0} ms，{2} 帧样本）&#10;"
                        + "体感参考：8 ms 以内几乎无感 · 15 ms 顺畅 · 25 ms 轻微发钝 · 40 ms 以上明显拖手",
                        f.LatencyP50Ms, f.LatencyP95Ms, f.LatencySamples);
                }
                else
                {
                    snap.LatencyDetail = f.LatencyMessage.Length > 0
                        ? f.LatencyMessage
                        : (f.Message.Length > 0 ? f.Message : FramesDiagnostic);
                }
            }

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
        /// CPU 温度：两路取数（PawnIO 读 SMN/MSR、ACPI 读热区），**驱动优先、ACPI 兜底**，
        /// 并按「读数是否成了死值」自动切换来源。
        ///
        /// 三条设计约束，都是踩出来的：
        ///
        /// 1) 顺序必须是驱动优先。ACPI 热区是固件转述的数，大量台式板/笔记本 BIOS 直接回
        ///    一个恒定占位值（用户实测恒为 28℃，装完驱动也一次没被调用过 —— 因为老实现
        ///    只要 ACPI 有值就 return）。用户装驱动的目的就是要真 die 读数，那就该 SMN 先答。
        ///
        /// 2) 恒定值本身是异常信号。两路都读得到时，如果一路连续 4 分钟一字不差、
        ///    而另一路确实会变（历史读数里出现过 ≥2 个不同值），就把死值那一路降级 ——
        ///    这正是「装了驱动仍显示 28℃」与「驱动坏成固定值」的共同特征。
        ///    两路都死时留在驱动：真 die 传感器即使空闲也不该比固件占位值更可疑。
        ///
        /// 3) 切换要有保持期（HoldMs）。否则一路在临界点抖动时读数会每 0.5 秒跳一次，
        ///    比不切换还难读。
        ///
        /// ACPI 每 2 秒才真读一次（WMI 查询比一次 SMN 读慢两个数量级）；驱动每帧都读。
        /// 两条都失败时把结论写进快照，UI 据此决定要不要给感叹号：
        ///   not_installed / no_access / module_failed / read_failed —— 见 PawnIoCpuTemp.DriverState
        ///   unsupported —— 架构不在支持范围，且 ACPI 也没有
        /// </summary>
        private void FillCpuTemp(MetricsSnapshot snap)
        {
            int now = Environment.TickCount;

            // ---- 第一路：PawnIO（读 die 内温度传感器）----
            double pawnC = 0;
            bool pawnOk = false;
            string pawnWhy = "";
            string pawnState = "";

            lock (_tempGate)
            {
                if (_pawnTemp != null)
                {
                    _pawnTemp.Probe();
                    pawnOk = _pawnTemp.TryRead(out pawnC, out pawnWhy);
                    if (!pawnOk)
                    {
                        pawnWhy = _pawnTemp.Status;
                        pawnState = _pawnTemp.DriverState;
                    }
                }
            }

            // ---- 第二路：ACPI 热区（零依赖，但可能是固件固定值）----
            double acpiC = 0;
            bool acpiOk = false;
            if (_temp != null)
            {
                if (_acpiLastRead == 0 || unchecked(now - _acpiLastRead) >= 2000)
                {
                    string why;
                    _acpiLastRead = now;
                    _acpiCachedOk = _temp.TryRead(out _acpiCachedC, out why);
                    // 只有真读到新值才喂监视器：把缓存值反复喂进去会把「没读到」算成「没变化」，
                    // 那样的冻结判定是假的。
                    if (_acpiCachedOk) _acpiFrozenState = _acpiFreeze.Add(_acpiCachedC);
                }
                acpiOk = _acpiCachedOk;
                acpiC = _acpiCachedC;
            }

            // ---- 冻结判定 ----
            bool pawnFrozen = pawnOk && _pawnFreeze.Add(pawnC);
            bool acpiFrozen = acpiOk && _acpiFrozenState;

            // 只有「另一路确实会变」时，才敢说这一路的恒定是异常
            bool pawnSuspect = pawnFrozen && _acpiFreeze.Distinct >= 2;
            bool acpiSuspect = acpiFrozen && _pawnFreeze.Distinct >= 2;

            string pick = DecideTempSource(pawnOk, pawnSuspect, acpiOk, acpiSuspect);

            // 保持期：刚切过去的那一路，只要还能读就再待一会儿
            if (pick.Length > 0 && _tempChoice.Length > 0 && unchecked(now - _tempChoiceAt) < HoldMs)
            {
                if (_tempChoice == "pawn" && pawnOk) pick = "pawn";
                else if (_tempChoice == "acpi" && acpiOk) pick = "acpi";
            }

            if (pick != _tempChoice)
            {
                _tempChoice = pick;
                _tempChoiceAt = now;
                if (pick.Length > 0)
                {
                    Diag("CpuTemp", string.Format(
                        "来源切换为 {0}（pawn {1:0.00}℃ ok={2} 冻结={3} ／ acpi {4:0.00}℃ ok={5} 冻结={6}）",
                        pick, pawnC, pawnOk, pawnFrozen, acpiC, acpiOk, acpiFrozen));
                }
            }

            // ---- 出数 ----
            if (pick == "pawn")
            {
                snap.TempCpuOk = true;
                snap.TempCpuC = pawnC;
                snap.TempCpuSource = "pawn";
                snap.TempCpuSuspect = pawnSuspect;
                snap.TempCpuDetail = "CPU 温度（SMN 寄存器，PawnIO"
                    + (acpiSuspect ? "；ACPI 热区读数长时间未变，已改用驱动直读" : "")
                    + (pawnSuspect ? "；此读数已 " + (FreezeWindowMs / 60000) + " 分钟未变化，另一路在变" : "")
                    + "）";
                snap.CpuTempState = "ready";
                _cpuTempState = "ready";
                return;
            }

            if (pick == "acpi")
            {
                snap.TempCpuOk = true;
                snap.TempCpuC = acpiC;
                snap.TempCpuSource = "acpi";
                snap.TempCpuSuspect = acpiSuspect;
                snap.TempCpuDetail = "CPU 温度（ACPI 热区"
                    + (_temp.SensorName.Length > 0 ? "：" + _temp.SensorName : "")
                    + (acpiSuspect ? "；驱动读数已 " + (FreezeWindowMs / 60000) + " 分钟未变化，已临时回退" : "")
                    + (acpiFrozen && !acpiSuspect ? "；读数已长时间未变化，多为固件固定值" : "")
                    + "）";
                snap.CpuTempState = acpiSuspect
                    ? (PawnIoCpuTemp.IsSupportedCpu ? "not_installed" : "unsupported")
                    : "ready";
                _cpuTempState = snap.CpuTempState;
                if (acpiSuspect) snap.TempCpuDetail += "；建议装一次温度组件读真实值";
                return;
            }

            // ---- 两路都没走通 ----
            string acpiWhy = "";
            if (_temp != null && !acpiOk)
            {
                double ignoredT;
                _temp.TryRead(out ignoredT, out acpiWhy);
            }

            string state = pawnState.Length > 0
                ? pawnState
                : (PawnIoCpuTemp.IsSupportedCpu ? "not_installed" : "unsupported");

            _cpuTempState = state;
            snap.CpuTempState = state;
            snap.TempCpuSource = "";

            if (pawnWhy.Length > 0) snap.TempCpuDetail = pawnWhy;
            else if (acpiWhy.Length > 0) snap.TempCpuDetail = acpiWhy;
        }

        /// <summary>
        /// 取数来源决策（纯函数，好测）。
        /// pawnSuspect / acpiSuspect 的语义是「这一路读数已经是死值，而另一路确实会变」，
        /// 判定在调用方做（要喂历史读数），这里只负责选择。
        ///
        /// 两路都可疑、或都正常时选驱动：真 die 传感器即使空闲也不该比固件占位值更可疑。
        /// </summary>
        internal static string DecideTempSource(bool pawnOk, bool pawnSuspect, bool acpiOk, bool acpiSuspect)
        {
            if (pawnOk && acpiOk)
            {
                if (acpiSuspect && !pawnSuspect) return "pawn";
                if (pawnSuspect && !acpiSuspect) return "acpi";
                return "pawn";
            }
            if (pawnOk) return "pawn";
            if (acpiOk) return "acpi";
            return "";
        }

        /// <summary>切源后的保持时长：够久到读数稳定，又不至于把用户锁在错的来源上。</summary>
        private const int HoldMs = 60000;

        /// <summary>冻结判定窗口：连续 4 分钟一字不差才算死值。</summary>
        private const int FreezeWindowMs = 240000;

        /// <summary>给自检/探针报告用的秒数（规则只有一处定义，报告不能再抄一份数字）。</summary>
        public static int FreezeWindowSeconds { get { return FreezeWindowMs / 1000; } }
        public static int HoldSeconds { get { return HoldMs / 1000; } }

        /// <summary>温度来源切换留痕（只在这命 --diag 时写盘）。</summary>
        private void Diag(string stage, string message)
        {
            if (!Diagnostic) return;
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "perfoverlay-error.log");
                var fi = new System.IO.FileInfo(path);
                if (fi.Exists && fi.Length > 256 * 1024) { try { System.IO.File.Delete(path); } catch (Exception) { } }
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [INF][" + stage + "] " + message
                    + Environment.NewLine);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 读数冻结监控（每一路一个）。
        ///
        /// 判定「死值」看两件事：
        ///   · Frozen  —— 连续 FreezeWindowMs 一字不差（用 TickCount，天然处理 49 天回绕）；
        ///   · Distinct —— 自程序启动以来见过几个不同读数。Distinct ≥ 2 说明这一路**会变**，
        ///     于是另一路的「不变」才是可疑的；两路都不变就没有信息量，不能拿来判罪。
        ///
        /// 窗口取 4 分钟而不是 3 分钟：die 传感器分辨率 0.125℃，空闲时确实可能长时间同值，
        /// 窗口太短会把正常空闲误判成死值。
        /// </summary>
        private sealed class FreezeWatch
        {
            private double _last = double.NaN;
            private int _since;
            private int _distinct;
            private bool _frozen;

            /// <summary>见过的不同读数个数（≥2 表示这一路会变）。</summary>
            public int Distinct { get { return _distinct; } }

            /// <summary>上一次 Add 的结论：该值是否已连续一个窗口没有变化。</summary>
            public bool Frozen { get { return _frozen; } }

            public bool Add(double v)
            {
                int now = Environment.TickCount;
                if (double.IsNaN(_last) || Math.Abs(v - _last) > 0.0001)
                {
                    _last = v;
                    _since = now;
                    _distinct++;
                    _frozen = false;
                    return false;
                }
                // TickCount 约 49 天回绕一次，unchecked 让差值自然处理符号
                _frozen = unchecked(now - _since) > FreezeWindowMs;
                return _frozen;
            }

            public void Reset()
            {
                _last = double.NaN;
                _since = 0;
                _distinct = 0;
                _frozen = false;
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
