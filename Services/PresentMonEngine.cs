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

        private readonly object _gate = new object();
        private readonly string _exePath;

        private Process _proc;

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

        private string _lastError = "";

        public bool IsRunning
        {
            get { var p = _proc; return p != null && !p.HasExited; }
        }

        public string LastError { get { return _lastError; } }
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

        public PresentMonEngine(string exePath)
        {
            _exePath = exePath;
        }

        /// <summary>前台进程变化时调用。只统计该进程的 present，避免把浮窗自己也算进去。</summary>
        public void SetTarget(int pid, string name)
        {
            lock (_gate)
            {
                if (pid != _targetPid)
                {
                    _targetPid = pid;
                    _targetName = name ?? "";
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
                    Arguments = "--output_stdout --no_console_stats --v1_metrics "
                              + "--session_name " + _sessionName + " --exclude PerfOverlay.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(_exePath)
                };

                var p = new Process { StartInfo = psi };
                var sbErr = new StringBuilder();

                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (_gate) { if (sbErr.Length < 800) sbErr.AppendLine(e.Data); }
                };
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    HandleLine(e.Data);
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                _proc = p;

                // 1.6 秒后仍未存活，把 stderr 当失败原因记下来（access denied 就在这里）
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Thread.Sleep(1600);
                        if (p.HasExited)
                        {
                            lock (_gate)
                            {
                                _lastError = sbErr.Length > 0
                                    ? sbErr.ToString().Trim()
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

            int col = 0, start = 0, total = line.Length;
            for (int i = 0; i <= total; i++)
            {
                if (i < total && line[i] != ',') continue;

                if (col == _idxPid) { pidStart = start; pidLen = i - start; }
                else if (col == _idxBetween) { msStart = start; msLen = i - start; }
                else if (col == _idxTime) { tStart = start; tLen = i - start; }
                else if (col == _idxSwapChain) { chStart = start; chLen = i - start; }
                else if (col == _idxApp) { appStart = start; appLen = i - start; }

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
                q.Push(t, ms);
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

        // ------------------------------------------------------------------
        // 统计
        // ------------------------------------------------------------------

        public FrameSnapshot Snapshot()
        {
            var snap = new FrameSnapshot { Message = "" };

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
        private FrameQueue PickBestChain()
        {
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
            public void Push(double t, double ms)
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

                if (Times.Count > MaxPoints)
                {
                    int drop = Times.Count - MaxPoints;
                    Times.RemoveRange(0, drop);
                    FrameMs.RemoveRange(0, drop);
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
                }
            }
        }
    }
}
