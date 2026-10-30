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
                    "n={7} msg=[{8}] err=[{9}] q={10}",
                    DateTime.Now, Frames.TargetPid, snap.TargetProcess, Frames.IsRunning,
                    snap.FpsOk, snap.Fps, snap.LowFps, snap.FrameCount,
                    snap.FpsMessage, Frames.LastError, Frames.QueueStat);

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
