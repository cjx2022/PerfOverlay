using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PerfOverlay.Services
{
    public struct FrameSnapshot
    {
        public bool Available;
        public double Fps;
        public double LowFps;
        public int FrameCount;      // 统计窗口内的帧数
        public string TargetProcess;
        public string Message;

        // ---- 显示延迟：Present 调用 → 该帧被扫到屏幕上（毫秒）----
        // 不是「帧时间」，也不是「输入延迟」。取值来自 PresentMon 的 msUntilDisplayed，
        // 只有渲染路径允许追踪显示环节时才有数（见 HandleLine 里的说明）。
        public bool LatencyOk;
        public double LatencyP50Ms;
        public double LatencyP95Ms;
        public int LatencySamples;
        public string LatencyMessage;
    }

    /// <summary>
    /// 帧数 / 1% Low 采集引擎。
    ///
    /// ── 数据来源 ────────────────────────────────────────────────
    /// ETW 的 Microsoft-Windows-DXGI provider 里，每次 SwapChain::Present 都有一条记录。
    /// 自己解析 EVENT_RECORD 要处理布局与 provider 版本差异，容易写错且难验证；
    /// 所以调用 Intel 开源的 PresentMon（MIT），只做子进程 + CSV 解析。
    /// 需要「管理员」或「Performance Log Users」组成员权限，否则报
    /// "failed to start trace session: access denied"。无权限时不启动、显示 N/A，绝不编数据。
    ///
    /// ── 1% Low 的口径（重点）────────────────────────────────────
    /// 定义：取最近 10 秒内所有帧的**帧时间**，按从大到小排序，取最慢的 1%
    ///       （至少 1 帧），求这些帧的**平均帧时间**，再 1000 / 平均 = 1% Low FPS。
    /// 与 CapFrameX / MSI Afterburner 的 "1% Low" 一致（用的是平均值法）。
    ///
    /// 三个容易踩错的地方，这里都处理了：
    /// 1) 必须用「帧时间」而不是「帧率」排序。按 FPS 排序再取 1% 会严重高估，
    ///    因为 1000/ms 是非线性的。
    /// 2) 不能只取第 99 百分位那一个点（P99），单点会被一次尖刺带偏；
    ///    业内口径是对最差 1% 取平均。
    /// 3) 一帧记录必须只来自一个 swapchain。多 swapchain（帧生成、覆盖层）
    ///    会把同一时刻的多次 present 重复计数，FPS 直接翻倍。这里按 PID + swapchain
    ///    分组，只统计该进程记录数最多的那条链。
    ///
    /// 帧时间优先取 CSV 的 msBetweenPresents；缺失（0/空）时退回用相邻
    /// TimeInSeconds 的差值，避免整段数据被判为无效。
    /// </summary>
    public sealed class PresentMonEngine : IDisposable
    {
        private const double FpsWindowSeconds = 1.0;    // FPS 统计窗
        private const double LowWindowSeconds = 10.0;   // 1% Low 统计窗
        private const int MinFramesForFps = 4;
        private const int MinFramesForLow = 100;        // 少于 100 帧谈 1% 没有意义
        private const double Percentile = 0.01;         // 1%
        private const double StaleSeconds = 2.5;        // 超过这个时间没新帧就判为停止提交
        // 显示延迟用「够不够说明问题」当门槛：60fps 下 10 秒 600 帧，20 个有效样本
        // 已经能给出稳定的中位数；再少就说明这段画面根本追踪不到显示环节。
        private const int MinLatencySamples = 20;

        private readonly object _gate = new object();
        private readonly string _exePath;

        private Process _proc;

        // stdout / stderr 各一条专用读线程。早先用的是 Process.OutputDataReceived
        // 事件回调，那个回调跑在 .NET 线程池上、**每一行都要排一次队**：游戏 300fps
        // 时每秒几百次调度，一旦线程池被 UI 渲染或采样线程占住，回调就积压 → 管道
        // 64KB 写满 → PresentMon 阻塞在 write(stdout)，不再读 ETW → 内核判定
        // 「实时会话没有实时使用者」→ 缓冲区写满、帧事件全被丢弃 → 队列空、帧数 N/A。
        // 这条链在 Kernel-EventTracing 日志里留下了原话（事件 1，见 README 排障）。
        // 改成专用线程同步 ReadLine 之后，消费速度只取决于这一条线程，与线程池无关。
        private Thread _outThread;
        private Thread _errThread;

        // stderr 与 stdout 用两把锁：stderr 极少，不该跟每帧都要拿的 _gate 抢。
        private readonly object _errGate = new object();

        // stderr 现场。必须能**随时**取到当前内容，不能只在进程退出后读：
        // PresentMon 会话建不起来时进程并不退出，只往 stderr 抱怨一句，然后安安静静
        // 挂着 —— 旧实现只在 HasExited 时才赋值，于是诊断日志里 err=[] 一片干净，
        // 让人以为「什么都没错」。排障最怕这种假绿。
        private readonly StringBuilder _errBuf = new StringBuilder();

        // 自己的进程文件名（带 .exe）。--exclude 按 CSV 的 Application 列精确匹配，
        // 而那一列是带扩展名的。发布物叫 PerfOverlay-0.9.5.exe，写死 "PerfOverlay.exe"
        // 匹配不上，PresentMon 会把浮窗自己的 present 也记进来，白白多一份负载。
        private static readonly string SelfExeName =
            SafeSelfExeName() ?? "PerfOverlay.exe";

        // 本次 PresentMon 子进程使用的 ETW 会话名。必须每次启动都不同：
        // 进程被强杀（RecycleIfStarved / 应用退出）时内核会话不会被回收，
        // 固定名字下一次启动会撞 0xC0000035（对象名冲突），会话起不来、一帧都收不到，
        // 而 PresentMon 进程本身还活着，看起来「一切正常」却永远 N/A。
        // 会话名唯一 + Stop() 里主动清理，两头都不留残留。
        private string _sessionName = "";

        // PID → (swapchain 地址 → 帧队列)
        private readonly Dictionary<int, Dictionary<ulong, FrameQueue>> _queues
            = new Dictionary<int, Dictionary<ulong, FrameQueue>>();

        private int _targetPid;
        private string _targetName = "";
        private int _lastSeenPid;

        // PID → PresentMon 报的进程名（Application 列，如 "msedge.exe"）。
        // Chromium/Electron 系是多进程架构：持有前台窗口的进程不调用 Present，
        // 真正出帧的是同名的 GPU 进程。所以按 PID 找不到时必须能按名字回落。
        private readonly Dictionary<int, string> _appNameOf = new Dictionary<int, string>();

        // PID → 最后一次收到该进程帧行的「墙钟」秒数。
        // 清理队列必须用这个，绝不能用 TimeInSeconds：那是相对采集起点的秒数，
        // 跟 Environment.TickCount 不在同一时钟域，相减会把所有队列误判成过期。
        private readonly Dictionary<int, double> _lastWallOf = new Dictionary<int, double>();

        // 最后一次真正收到帧行的「墙钟」时刻。
        // 不能用 TimeInSeconds 跟系统时钟比：PresentMon 的 TimeInSeconds 是相对
        // 采集起点的秒数，量级与 Environment.TickCount 完全不同，直接相减会得出
        // 荒谬的过期判断。所以另外记一个本地时钟。
        private double _lastFrameWall;

        // header 索引（动态解析，不假设列序）。-1 表示该列不存在。
        private int _idxApp = -1;
        private int _idxPid = -1;
        private int _idxBetween = -1;
        private int _idxTime = -1;
        private int _idxSwapChain = -1;
        private int _idxUntilDisplayed = -1;

        // 是否采集显示延迟。默认关：v1_metrics 的 CSV 里这列一直都在，
        // 不采的时候连切列都不做（每帧少一次 Substring），也不占内存。
        private volatile bool _captureLatency;
        public bool CaptureLatency
        {
            get { return _captureLatency; }
            set
            {
                if (_captureLatency == value) return;
                _captureLatency = value;
                if (!value)
                {
                    // 关掉时把已攒的样本清掉，否则下次打开会带着上次的旧数据出数
                    lock (_gate)
                    {
                        foreach (var perChain in _queues.Values)
                            foreach (var q in perChain.Values) q.ClearDelay();
                    }
                }
            }
        }

        // 求分位数时的临时缓冲：复用一块，避免每 500ms 在 GC 上扔一个大数组。
        // 只在 _gate 内使用，所以不需要额外加锁。
        private readonly List<double> _delayScratch = new List<double>(4096);

        private string _lastError = "";

        // 从 PresentMon stdout 收到的 CSV 行总数（含表头）。这一个数字就能把
        // 「帧数 N/A」拆成三种完全不同的情况：
        //   lines=0            → PresentMon 一句都没吐：会话没建起来 / 被拦 / 被 exclude 光
        //   lines>0 且队列空    → 有输出但解析没命中（列序或编码变了）
        //   lines>0 且队列有帧  → 解析没问题，是目标进程判定错了（选中了不 present 的进程）
        // 以前只有 QueueStat，队列空的时候根本分不清是哪一种。
        private long _linesSeen;

        public bool IsRunning
        {
            get { var p = _proc; return p != null && !p.HasExited; }
        }

        public string LastError { get { return _lastError; } }

        /// <summary>PresentMon 的 stderr 现场（进程还活着时也能取到）。诊断用。</summary>
        public string StderrText
        {
            get
            {
                lock (_errGate)
                {
                    return _errBuf.ToString()
                        .Replace("\r", "")
                        .Replace("\n", " | ")
                        .Trim();
                }
            }
        }
        /// <summary>stdout 收到的 CSV 行总数（诊断用，见 _linesSeen 注释）。</summary>
        public long LinesSeen { get { return Interlocked.Read(ref _linesSeen); } }

        /// <summary>本次子进程使用的 ETW 会话名（诊断用，方便和内核日志对照）。</summary>
        public string SessionName { get { return _sessionName; } }

        public string ExePath { get { return _exePath; } }
        public bool PresentMonFound { get { return File.Exists(_exePath); } }

        /// <summary>最近一条帧行里出现的 PID（回放诊断用）。</summary>
        public int LastSeenPid { get { lock (_gate) { return _lastSeenPid; } } }

        /// <summary>当前队列概况：每个 PID 累计多少帧。诊断「PresentMon 到底有没有在出数据」用。</summary>
        public string QueueStat
        {
            get
            {
                lock (_gate)
                {
                    if (_queues.Count == 0) return "(空)";
                    var sb = new StringBuilder();
                    foreach (var kv in _queues)
                    {
                        int n = 0;
                        foreach (var c in kv.Value.Values) n += c.Count;
                        if (sb.Length > 0) sb.Append(' ');
                        string nm;
                        sb.Append(kv.Key);
                        if (_appNameOf.TryGetValue(kv.Key, out nm) && nm.Length > 0)
                            sb.Append('(').Append(NormalizeAppName(nm)).Append(')');
                        sb.Append(':').Append(n);
                    }
                    return sb.ToString();
                }
            }
        }

        /// <summary>当前目标 PID（诊断用）。</summary>
        public int TargetPid { get { lock (_gate) { return _targetPid; } } }

        public PresentMonEngine(string exePath, bool excludeSelf = true)
        {
            _exePath = exePath;
            ExcludeSelf = excludeSelf;
        }

        /// <summary>
        /// 是否把本进程排除在采集之外。默认开 —— 浮窗自己也在 Present，
        /// 不排除就会在「前台是别的程序」时把自己那几帧混进来。
        /// 只有 --latency-test 要采自己的动画窗口时才关掉。
        /// </summary>
        public bool ExcludeSelf { get; set; }

        /// <summary>前台进程变化时调用。只统计该进程的 present，避免把浮窗自己也算进去。</summary>
        public void SetTarget(int pid, string name)
        {
            lock (_gate)
            {
                string n = name ?? "";
                // 名字也要参与判断：只按 pid 判断会出现「pid 没变、名字从空变成可读」
                // 时永远不更新 —— 而空名字会让同名进程回落整条失效，帧数就永远 N/A。
                if (pid != _targetPid || n.Length > 0 && n != _targetName)
                {
                    _targetPid = pid;
                    _targetName = n;
                }
            }
        }

        public bool Start()
        {
            if (!PresentMonFound)
            {
                _lastError = "未找到 PresentMon.exe（应位于程序目录 tools\\PresentMon.exe）";
                return false;
            }
            if (IsRunning) return true;
            return StartProcess();
        }

        private bool StartProcess()
        {
            try
            {
                _sessionName = "PerfOverlay_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    // ── 不要加 --exclude_dropped ──────────────────────────────
                    // PresentMon 把「没追踪到真正显示到屏幕」的帧判为 Dropped=1。
                    // 而 Independent Flip / 覆盖层 / Electron 这类路径上，
                    // msUntilDisplayed 与 msBetweenDisplayChange 恒为 0，追踪不到显示环节，
                    // 于是**每一帧都是 Dropped=1**。带上 --exclude_dropped 就会把整段帧
                    // 全部剔除，stdout 只剩表头，队列永远是空的 —— 表现为「提权了也拿不到帧数」。
                    // 这里统计的是 present 率（与主流 FPS 计数器口径一致），所以不剔除。
                    //
                    // 会话名每次唯一：靠 --stop_existing_session 清残留并不可靠（实测清不掉的
                    // 僵死会话会让之后每次启动都报 0xC0000035，帧数永久 N/A）。
                    // 残留由 Stop() 里 PresentMon 自带的 --terminate_existing_session 收尾。
                    //
                    // --set_circular_buffer_size：PresentMon 自己的 present 事件环形缓冲
                    // 默认只有 2048 条（-h 里写明），300fps 下不到 7 秒就绕一圈。超过就丢，
                    // 丢掉的帧连 ETW 层都到不了。调到 32768（必须是 2 的幂），给消费端留余量。
                    // 2.6 以下没有这个开关，加了会直接启动失败，所以按版本探测后再加。
                    Arguments = "--output_stdout --no_console_stats --v1_metrics "
                              + "--session_name " + _sessionName
                              + (ExcludeSelf ? " --exclude " + SelfExeName : "")
                              + CircularBufferArg(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(_exePath)
                };

                var p = new Process { StartInfo = psi };
                lock (_errGate) { _errBuf.Length = 0; }
                Interlocked.Exchange(ref _linesSeen, 0);

                p.Start();
                _proc = p;

                // ── stdout：专用线程同步读 ────────────────────────────────
                // 不用 BeginOutputReadLine + OutputDataReceived。那条路每来一行就要
                // 往线程池排一次工作项，帧率高时会积压；积压 → 管道写满 → PresentMon
                // 阻塞 → ETW 实时会话没有消费者 → 内核丢事件（帧数 N/A 的真正来源）。
                // 一个阻塞在 ReadLine 上的专用线程没有调度延迟，来一行吃一行。
                var outThread = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null)
                        {
                            HandleLine(line);
                        }
                    }
                    catch (Exception) { }
                });
                outThread.IsBackground = true;
                outThread.Name = "PresentMon-stdout";
                outThread.Start();

                // stderr 同样走专用线程：量很小，但用 BeginErrorReadLine 一样吃线程池。
                var errThread = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardError.ReadLine()) != null)
                        {
                            lock (_errGate) { if (_errBuf.Length < 800) _errBuf.AppendLine(line); }
                        }
                    }
                    catch (Exception) { }
                });
                errThread.IsBackground = true;
                errThread.Name = "PresentMon-stderr";
                errThread.Start();

                _outThread = outThread;
                _errThread = errThread;

                // 1.6 秒后仍未存活，把 stderr 当失败原因记下来（access denied 就在这里）
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Thread.Sleep(1600);
                        if (p.HasExited)
                        {
                            string detail;
                            lock (_errGate)
                            {
                                detail = _errBuf.Length > 0 ? _errBuf.ToString().Trim() : "";
                            }
                            lock (_gate)
                            {
                                _lastError = detail.Length > 0
                                    ? detail
                                    : ("PresentMon 退出码 " + p.ExitCode);
                            }
                        }
                    }
                    catch (Exception) { }
                });

                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------
        // CSV 解析
        // ------------------------------------------------------------------

        /// <summary>
        /// 自己的进程文件名（带 .exe，与 CSV 的 Application 列同一口径）。
        /// 取不到时返回 null，由调用方兜底。
        /// </summary>
        private static string SafeSelfExeName()
        {
            try
            {
                string path = null;
                var asm = System.Reflection.Assembly.GetEntryAssembly();
                if (asm != null)
                {
                    try { path = asm.Location; } catch (Exception) { }
                }
                if (string.IsNullOrEmpty(path))
                {
                    var mm = Process.GetCurrentProcess().MainModule;
                    if (mm != null) path = mm.FileName;
                }
                string name = Path.GetFileName(path);
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// --set_circular_buffer_size 只在 PresentMon 2.6 及以上存在（2.6.0 的 -h 里列在
        /// Beta Options）。低版本传了会直接启动失败，表现为帧数永久 N/A，所以按文件版本
        /// 探测后再决定加不加。
        /// </summary>
        private string CircularBufferArg()
        {
            // ── 暂时关闭（2026-11-10 实测）──
            // PresentMon 2.6.0 的 --set_circular_buffer_size 列在 Beta Options。
            // 加上之后实测：进程活着、stderr 一句不说、stdout 连 CSV 表头都不吐
            // （诊断日志 lines=0），内核日志里也没有「会话未能启动」的记录 ——
            // 就是卡在建立会话那一步不返回。帧数因此永久 N/A。
            // 参数本身被解析器接受（传错参数会明确报 unrecognized option），
            // 所以问题在运行时而不是命令行。等能确认它的行为再打开。
            return "";

            /*
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(_exePath);
                int major = vi.FileMajorPart;
                int minor = vi.FileMinorPart;
                bool ok = major > 2 || (major == 2 && minor >= 6);
                return ok ? " --set_circular_buffer_size 32768" : "";
            }
            catch (Exception) { return ""; }
            */
        }

        /// <summary>
        /// 把一行 CSV 喂进解析管线。生产路径是 PresentMon 的 stdout，
        /// 诊断路径是回放录好的 CSV —— 两条路走同一份代码，回放才能证明解析没问题。
        /// </summary>
        public void FeedLine(string line)
        {
            HandleLine(line);
        }

        private void HandleLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            Interlocked.Increment(ref _linesSeen);

            if (line.StartsWith("Application,", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("ProcessID,", StringComparison.OrdinalIgnoreCase))
            {
                ParseHeader(line);
                return;
            }

            // ── 热路径：不做 string.Split ────────────────────────────────
            // v1_metrics 的 CSV 有二十多列，每帧 Split 一次会分配一个数组 + 二十多个子串。
            // 300fps 下就是每秒近七千次分配，是常驻进程里最大的一处 GC 来源。
            // 这里只扫描一遍逗号，把用得到的几列位置记下来，按需切片（每帧 2 个字符串）。
            int pidStart = -1, pidLen = 0;
            int msStart = -1, msLen = 0;
            int tStart = -1, tLen = 0;
            int chStart = -1, chLen = 0;
            int appStart = -1, appLen = 0;
            int dlStart = -1, dlLen = 0;

            int col = 0, start = 0, total = line.Length;
            for (int i = 0; i <= total; i++)
            {
                if (i < total && line[i] != ',') continue;

                if (col == _idxPid) { pidStart = start; pidLen = i - start; }
                else if (col == _idxBetween) { msStart = start; msLen = i - start; }
                else if (col == _idxTime) { tStart = start; tLen = i - start; }
                else if (col == _idxSwapChain) { chStart = start; chLen = i - start; }
                else if (col == _idxApp) { appStart = start; appLen = i - start; }
                else if (_captureLatency && col == _idxUntilDisplayed) { dlStart = start; dlLen = i - start; }

                col++;
                start = i + 1;
            }

            // 没有表头时退回「第二列是 PID」的约定
            if (pidStart < 0) { pidStart = 0; pidLen = 0; }
            int pid;
            if (pidLen > 0)
            {
                if (!TryParseInt(line, pidStart, pidLen, out pid)) return;
            }
            else
            {
                // 无表头兜底：手工找第二列
                int first = line.IndexOf(',');
                if (first < 0) return;
                int second = line.IndexOf(',', first + 1);
                int len = (second < 0 ? total : second) - first - 1;
                if (!TryParseInt(line, first + 1, len, out pid)) return;
            }
            if (pid <= 0) return;

            double ms = 0;
            if (msLen > 0)
                double.TryParse(line.Substring(msStart, msLen), NumberStyles.Float, CultureInfo.InvariantCulture, out ms);

            double t = 0;
            bool haveT = false;
            if (tLen > 0)
                haveT = double.TryParse(line.Substring(tStart, tLen), NumberStyles.Float, CultureInfo.InvariantCulture, out t);
            if (!haveT) t = NowSeconds();

            // swapchain 用 ulong 当 key：地址是十六进制整数，解析成数值就不必每帧切字符串
            ulong chain = 0;
            if (chLen > 0) TryParseHex(line, chStart, chLen, out chain);

            // 显示延迟：msUntilDisplayed。空/0 表示这一帧没被追踪到显示环节，
            // 不是「延迟为 0」—— 独立翻转、覆盖层、Electron 这类路径整段都是 0，
            // 所以这里保留 0 作为「无样本」占位，由统计端过滤，绝不当成有效值。
            double delay = 0;
            if (dlLen > 0)
                double.TryParse(line.Substring(dlStart, dlLen), NumberStyles.Float, CultureInfo.InvariantCulture, out delay);
            if (delay < 0 || delay > 10000) delay = 0;   // 明显异常的脏值直接丢

            lock (_gate)
            {
                _lastSeenPid = pid;
                _lastFrameWall = NowSeconds();
                // 应用名只在首次见到该 PID 时解析一次（每帧省一个子串 + Trim）
                if (appLen > 0 && !_appNameOf.ContainsKey(pid))
                {
                    string app = TrimQuotes(line, appStart, appLen);
                    if (app.Length > 0) _appNameOf[pid] = app;
                }
                // 目标进程名自带兜底：系统接口读不到名字时（进程刚起来、权限瞬态不足），
                // 用 CSV 的 Application 列补上 —— 这是唯一可靠的来源，缺了它
                // 「同名进程回落」整条失效，Chromium 系应用的帧数会一直是 N/A。
                if (pid == _targetPid && _targetName.Length == 0 && appLen > 0)
                {
                    string app2 = TrimQuotes(line, appStart, appLen);
                    if (app2.Length > 0) _targetName = app2;
                }
                _lastWallOf[pid] = _lastFrameWall;

                Dictionary<ulong, FrameQueue> perChain;
                if (!_queues.TryGetValue(pid, out perChain))
                {
                    perChain = new Dictionary<ulong, FrameQueue>(2);
                    _queues[pid] = perChain;
                }

                FrameQueue q;
                if (!perChain.TryGetValue(chain, out q))
                {
                    q = new FrameQueue();
                    perChain[chain] = q;
                }
                q.Push(t, ms, delay);
            }
        }

        /// <summary>手写整数解析：热路径上避免 Substring 分配。</summary>
        private static bool TryParseInt(string s, int start, int len, out int value)
        {
            value = 0;
            int i = start, end = start + len;
            while (i < end && (s[i] == ' ' || s[i] == '"')) i++;
            while (end > i && (s[end - 1] == ' ' || s[end - 1] == '"')) end--;
            if (i >= end) return false;

            bool neg = false;
            if (s[i] == '-' || s[i] == '+') { neg = s[i] == '-'; i++; }
            if (i >= end) return false;

            int acc = 0;
            for (; i < end; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return false;
                acc = acc * 10 + (c - '0');
                if (acc > 100000000) return false;
            }
            value = neg ? -acc : acc;
            return true;
        }

        /// <summary>解析 "0x00000000A1B2C3D4" 这类十六进制地址。</summary>
        private static bool TryParseHex(string s, int start, int len, out ulong value)
        {
            value = 0;
            int i = start, end = start + len;
            while (i < end && s[i] == ' ') i++;
            if (i + 1 < end && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X')) i += 2;
            if (i >= end) return false;

            ulong acc = 0;
            for (; i < end; i++)
            {
                char c = s[i];
                int d;
                if (c >= '0' && c <= '9') d = c - '0';
                else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
                else return false;
                acc = (acc << 4) | (uint)d;
            }
            value = acc;
            return true;
        }

        /// <summary>取一段并去掉首尾空白与引号（CSV 里带逗号的路径会被引号包起来）。</summary>
        private static string TrimQuotes(string s, int start, int len)
        {
            int i = start, end = start + len;
            while (i < end && (s[i] == ' ' || s[i] == '"')) i++;
            while (end > i && (s[end - 1] == ' ' || s[end - 1] == '"')) end--;
            return end > i ? s.Substring(i, end - i) : "";
        }

        private void ParseHeader(string line)
        {
            var cols = line.Split(',');
            lock (_gate)
            {
                // 没有 Application 列时保持 -1（不能回落成 0，那样会把 PID 列当进程名）
                _idxApp = IndexOf(cols, "Application");
                _idxPid = IndexOf(cols, "ProcessID");
                _idxBetween = IndexOf(cols, "msBetweenPresents");
                _idxTime = IndexOf(cols, "TimeInSeconds");
                _idxSwapChain = IndexOf(cols, "SwapChainAddress");
                // 显示延迟列。v1_metrics（PresentMon 1.x 口径）里就是这个名字；
                // 换 --v2_metrics 的话叫 DisplayLatency，届时这里补一条即可。
                _idxUntilDisplayed = IndexOf(cols, "msUntilDisplayed");
            }
        }

        private static int IndexOf(string[] cols, string name)
        {
            for (int i = 0; i < cols.Length; i++)
            {
                if (string.Equals(cols[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static double NowSeconds()
        {
            // 必须用单调时钟：Environment.TickCount 是 32 位毫秒计数，
            // 49.7 天就回绕成负数。这程序是开机自启的常驻进程，跑满两个月必然翻车 ——
            // 回绕后「距上次出帧多久」会算成一个巨大的正数或负数，帧数永远显示"无新帧"。
            return Clock.Elapsed.TotalSeconds;
        }

        /// <summary>进程级单调秒表，替代会回绕的 Environment.TickCount。</summary>
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>取已排序样本的 p 分位（线性插值）。p 用 0.5 / 0.95 这种小数。</summary>
        private static double PercentileOf(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            if (sorted.Count == 1) return sorted[0];
            double pos = (sorted.Count - 1) * p;
            int lo = (int)Math.Floor(pos);
            int hi = (int)Math.Ceiling(pos);
            if (lo < 0) lo = 0;
            if (hi >= sorted.Count) hi = sorted.Count - 1;
            if (lo == hi) return sorted[lo];
            double frac = pos - lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
        }

        // ------------------------------------------------------------------
        // 统计
        // ------------------------------------------------------------------

        public FrameSnapshot Snapshot()
        {
            // 两个 string 字段都必须给初值：struct 的默认值是 null，
            // 上层到处在写 `.Length > 0`，漏一个就是空引用（本机实测崩过）。
            var snap = new FrameSnapshot { Message = "", LatencyMessage = "" };

            lock (_gate)
            {
                if (_targetPid <= 0)
                {
                    snap.Message = "等待前台窗口";
                    return snap;
                }

                // 队列里的旧帧不能一直当作实时值显示（切窗口、画面静止、进程退出）
                if (_lastFrameWall > 0 && NowSeconds() - _lastFrameWall > StaleSeconds)
                {
                    snap.Message = "无新帧（画面静止或已切换）";
                    return snap;
                }

                FrameQueue q = PickBestChain();
                if (q == null || q.Count == 0)
                {
                    string blocked = BlockedHint();
                    snap.Message = blocked.Length > 0
                        ? blocked
                        : (_targetName.Length > 0 ? (_targetName + " 无画面提交") : "无画面提交");
                    return snap;
                }

                double now = q.LastTime;
                q.Trim(now - LowWindowSeconds);

                int count = q.Count;
                if (count < MinFramesForFps)
                {
                    snap.Message = "采样中";
                    return snap;
                }

                // ---- FPS：最近 1 秒的帧数 / 实际跨度 ----
                double winStart = now - FpsWindowSeconds;
                int inWin = 0;
                double firstTs = double.MaxValue;
                double lastTs = 0;

                var times = q.Times;
                for (int i = times.Count - 1; i >= 0; i--)
                {
                    var item = times[i];
                    if (item.Key < winStart) break;
                    inWin++;
                    if (item.Key < firstTs) firstTs = item.Key;
                    if (item.Key > lastTs) lastTs = item.Key;
                }

                if (inWin >= MinFramesForFps)
                {
                    double span = lastTs - firstTs;
                    double denom = span > 0.15 ? span : FpsWindowSeconds;
                    double fps = (inWin - 1) / denom;
                    if (fps < 0) fps = 0;
                    snap.Fps = fps;
                    snap.Available = true;
                }

                // ---- 1% Low：最差 1% 帧的「平均帧时间」取倒数（算法见 FrameStats）----
                if (count >= MinFramesForLow)
                {
                    double low = FrameStats.LowFps(q.FrameMs, Percentile);
                    if (low > 0)
                    {
                        snap.LowFps = low;
                        snap.Available = true;
                    }
                }

                // ---- 显示延迟：窗口内有效样本的 P50 / P95 ----
                // 用中位数而不是平均值：延迟分布是长尾的（一次卡顿能甩出几百毫秒），
                // 平均值会被单帧拉飞，中位数才代表「平常看到多少」。
                if (_captureLatency)
                {
                    if (_idxUntilDisplayed < 0)
                    {
                        snap.LatencyMessage = "采集器未提供延迟列";
                    }
                    else
                    {
                        _delayScratch.Clear();
                        var ds = q.DelayMs;
                        for (int i = 0; i < ds.Count; i++)
                            if (ds[i] > 0) _delayScratch.Add(ds[i]);

                        snap.LatencySamples = _delayScratch.Count;
                        if (_delayScratch.Count >= MinLatencySamples)
                        {
                            _delayScratch.Sort();
                            snap.LatencyP50Ms = PercentileOf(_delayScratch, 0.50);
                            snap.LatencyP95Ms = PercentileOf(_delayScratch, 0.95);
                            snap.LatencyOk = true;
                        }
                        else
                        {
                            snap.LatencyMessage = _delayScratch.Count == 0
                                ? "这段画面追踪不到显示环节（该渲染路径不上报出屏时刻）"
                                : "采样中";
                        }
                    }
                }

                snap.FrameCount = count;
                snap.TargetProcess = _targetName;
                return snap;
            }
        }

        /// <summary>
        /// 选出代表「当前前台应用」的帧队列，两级匹配：
        ///   1) PID 精确命中 —— 传统单进程游戏（大多数 exe）走这条；
        ///   2) 按进程名回落 —— Electron / Chromium（WorkBuddy、Edge、Chrome、Discord…）
        ///      是多进程架构，持有前台窗口的主进程**不调用 Present**，真正出帧的是
        ///      同名的 GPU 进程，PID 完全不同。按 PID 精确匹配会让这些应用永远拿不到帧。
        /// 多链时只取记录数最多的一条，避免帧生成/覆盖层把帧数算两遍。
        /// </summary>
        private FrameQueue PickBestChain()        {
            // 1) PID 精确命中
            Dictionary<ulong, FrameQueue> perChain;
            if (_targetPid > 0 && _queues.TryGetValue(_targetPid, out perChain))
            {
                var exact = Densest(perChain);
                if (exact != null && exact.Count > 0) return exact;
            }

            // 2) 同名进程回落（PresentMon 的 "msedge.exe" vs 系统的 "msedge"）
            string want = NormalizeAppName(_targetName);
            if (want.Length == 0) return null;

            FrameQueue best = null;
            foreach (var kv in _queues)
            {
                string app;
                if (!_appNameOf.TryGetValue(kv.Key, out app)) continue;
                if (!string.Equals(NormalizeAppName(app), want, StringComparison.OrdinalIgnoreCase)) continue;

                var cand = Densest(kv.Value);
                if (cand != null && cand.Count > 0 && (best == null || cand.Count > best.Count)) best = cand;
            }
            return best;
        }

        private static FrameQueue Densest(Dictionary<ulong, FrameQueue> perChain)
        {
            FrameQueue best = null;
            foreach (var kv in perChain)
            {
                if (best == null || kv.Value.Count > best.Count) best = kv.Value;
            }
            return best;
        }

        /// <summary>"msedge.exe" 与 "msedge" 视为同一个应用。</summary>
        private static string NormalizeAppName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string t = name.Trim();
            if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
            return t;
        }

        /// <summary>
        /// PresentMon 起来了、也没报错，却一个帧都收不到 —— 最常见的原因是反作弊/安全软件
        /// （如腾讯 ACE 的 SGuard64）拦截了 ETW 的 DXGI present 事件。
        /// 这种情况下任何采集器都拿不到数据，只能如实告知，不能编数。
        /// </summary>
        private string BlockedHint()
        {
            try
            {
                var p = _proc;
                if (p == null || p.HasExited) return "";
                double up = (DateTime.Now - p.StartTime).TotalSeconds;
                if (up < 8) return "";          // 刚启动，给会话一点建立时间
                lock (_gate) { if (_queues.Count > 0) return ""; }
                return "采集被拦截（反作弊/安全软件，退出游戏后恢复）";
            }
            catch (Exception) { return ""; }
        }

        /// <summary>清理超过 30 秒没有新帧的进程队列。</summary>
        public void SweepQueues()
        {
            lock (_gate)
            {
                if (_queues.Count <= 2) return;
                double now = NowSeconds();
                string want = NormalizeAppName(_targetName);
                var dead = new List<int>();
                foreach (var kv in _queues)
                {
                    if (kv.Key == _targetPid) continue;

                    // 同名的兄弟进程（Chromium 的 GPU 进程）就是当前目标，不能清
                    string app;
                    if (want.Length > 0 && _appNameOf.TryGetValue(kv.Key, out app)
                        && string.Equals(NormalizeAppName(app), want, StringComparison.OrdinalIgnoreCase))
                        continue;

                    double last;
                    if (!_lastWallOf.TryGetValue(kv.Key, out last) || last <= 0 || now - last > 30.0)
                        dead.Add(kv.Key);
                }
                for (int i = 0; i < dead.Count; i++)
                {
                    _queues.Remove(dead[i]);
                    _appNameOf.Remove(dead[i]);
                    _lastWallOf.Remove(dead[i]);
                }
            }
        }

        public void Stop()
        {
            var p = _proc;
            _proc = null;
            var ot = _outThread;
            var et = _errThread;
            _outThread = null;
            _errThread = null;
            string session = _sessionName;
            _sessionName = "";

            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                }
                catch (Exception) { }

                // 读线程必须死在 Dispose 之前：Dispose 关流后，还在 ReadLine 里的线程
                // 会抛 ObjectDisposedException（外面有 catch，但那一瞬可能在处理半行）。
                // Kill 之后管道就关了，ReadLine 立刻返回 null，实际几乎不用等。
                try { if (ot != null) ot.Join(600); } catch (Exception) { }
                try { if (et != null) et.Join(300); } catch (Exception) { }

                try { p.Dispose(); } catch (Exception) { }
            }

            // 强杀不会通知内核回收 ETW 会话。用 PresentMon 自带的开关把本次会话名
            // 显式停掉（实测能把 logman 里那条残留会话清成「找不到数据收集器集」）。
            // 不清的话，同名会话残留 + 固定名字 = 下次启动必然 0xC0000035。
            if (session.Length == 0 || !File.Exists(_exePath)) return;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = "--session_name " + session + " --terminate_existing_session --no_csv",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var cleaner = Process.Start(psi))
                {
                    if (cleaner != null)
                    {
                        cleaner.WaitForExit(1500);
                        if (!cleaner.HasExited)
                        {
                            try { cleaner.Kill(); } catch (Exception) { }
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            Stop();
        }

        // ------------------------------------------------------------------

        private sealed class FrameQueue
        {
            public readonly List<KeyValuePair<double, double>> Times
                = new List<KeyValuePair<double, double>>(2048);
            public readonly List<double> FrameMs = new List<double>(2048);
            /// <summary>与 FrameMs 一一对应的显示延迟；0 表示这一帧没有有效值。</summary>
            public readonly List<double> DelayMs = new List<double>(2048);
            private const int MaxPoints = 3000;   // ≈ 300fps × 10s

            // 最后见过的帧时间戳，与「是否入队」解耦：
            // 若整列 msBetweenPresents 都是空，首帧因无参考被丢弃，
            // 入队队列就一直是空的，后面的帧也就永远拿不到参考 → 整段归零。
            private double _lastSeenT;

            public int Count { get { return Times.Count; } }

            public double LastTime
            {
                get { return Times.Count == 0 ? 0 : Times[Times.Count - 1].Key; }
            }

            /// <summary>ms 为 0（CSV 未给或首帧）时，用与上一帧的时间差补出来。</summary>
            public void Push(double t, double ms, double delay)
            {
                if (ms <= 0 && _lastSeenT > 0)
                {
                    double dt = t - _lastSeenT;
                    if (dt > 0 && dt < 1.0) ms = dt * 1000.0;
                }
                _lastSeenT = t;

                if (ms <= 0) return;   // 没有任何参考，丢掉这一帧，不要塞 0 污染统计

                Times.Add(new KeyValuePair<double, double>(t, ms));
                FrameMs.Add(ms);
                DelayMs.Add(delay);

                if (Times.Count > MaxPoints)
                {
                    int drop = Times.Count - MaxPoints;
                    Times.RemoveRange(0, drop);
                    FrameMs.RemoveRange(0, drop);
                    DelayMs.RemoveRange(0, drop);
                }
            }

            public void Trim(double minTime)
            {
                int drop = 0;
                while (drop < Times.Count && Times[drop].Key < minTime) drop++;
                if (drop > 0)
                {
                    Times.RemoveRange(0, drop);
                    if (drop <= FrameMs.Count) FrameMs.RemoveRange(0, drop);
                    if (drop <= DelayMs.Count) DelayMs.RemoveRange(0, drop);
                }
            }

            /// <summary>清掉延迟样本（关掉这个指标时用），帧数统计不受影响。</summary>
            public void ClearDelay()
            {
                DelayMs.Clear();
                for (int i = 0; i < FrameMs.Count; i++) DelayMs.Add(0);
            }
        }
    }
}
