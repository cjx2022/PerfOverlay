using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 全屏守护：前台出现全屏窗口时，把浮窗重新钉回窗口栈顶。
    ///
    /// 为什么需要它：WPF 的 <c>Topmost="True"</c> 只是在窗口创建时打上 WS_EX_TOPMOST，
    /// 它保证的只是「在普通窗口之上」。全屏应用（无边框全屏 / 全屏优化）切进来时，
    /// 系统会把全屏窗口提升到 topmost 组里更靠前的位置，于是浮窗被压到全屏画面之下 ——
    /// 表现就是「全屏了，浮窗没了」，退全屏又自己回来。
    ///
    /// 所以这里做两件事：
    /// 1. 每 <see cref="IntervalMs"/> 毫秒看一眼前台窗口，判断它是否铺满整块显示器；
    /// 2. 是的话用 SetWindowPos(HWND_TOPMOST) 重申一次层级（带 SWP_NOACTIVATE，
    ///    绝不抢焦点，否则全屏游戏会被 Alt+Tab 出去）。
    ///
    /// 边界（写清楚，别让人以为这是万能药）：
    /// - 独占全屏（exclusive fullscreen，D3D 直接接管输出）下 DWM 不合成任何窗口，
    ///   WPF 浮窗物理上不可能出现。这种情况只能让用户在游戏里选「无边框窗口」。
    /// - 如果全屏程序自己也每隔几帧抢一次 topmost，会互相盖，视觉上是闪。
    ///   这里不做更激进的 hook，避免把正常的窗口层级搞乱。
    /// </summary>
    internal static class TopmostGuard
    {
        // 250ms：任务栏在鼠标移上去时会把自己提到 TOPMOST 队首，
        // 间隔太长就会出现「鼠标扫过任务栏 → 浮窗消失一下 → 又被抬回来」的闪烁。
        // 采集成本只是一次 EnumWindows，窗口数量级上百时不到 0.2ms。
        private const int IntervalMs = 250;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint GW_OWNER = 4;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_NOSENDCHANGING = 0x0400;

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        // SHQueryUserNotificationState 的返回值（QUERY_USER_NOTIFICATION_STATE）
        private const int QUNS_BUSY = 2;
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        private const int QUNS_PRESENTATION_MODE = 4;

        private static DispatcherTimer _timer;
        private static IntPtr _hwnd = IntPtr.Zero;

        /// <summary>重申次数。探针读它来证明「真的动过手」，不是只看配置位。</summary>
        public static int ReassertCount { get; private set; }

        /// <summary>定时器跑了多少轮。用来证明守护真的在转，而不是只有配置位对。</summary>
        public static int TickCount { get; private set; }

        /// <summary>最近一次判定：前台是否是全屏窗口。探针可读。</summary>
        public static bool LastForegroundFullscreen { get; private set; }

        /// <summary>SetWindowBand 是否可用（Win8.1+ 的未文档化接口，某些版本可能没有）。</summary>
        public static bool BandApiAvailable { get { ProbeBandApi(); return _setBand != null; } }

        /// <summary>把浮窗挂进守护。窗口句柄必须已经创建（OnSourceInitialized 之后）。</summary>
        public static void Attach(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            _hwnd = hwnd;
            // 换了一个窗口句柄，band 状态归零（band 跟着窗口走，不跟进程走）
            _bandRaised = false;

            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(IntervalMs),
                };
                _timer.Tick += (s, e) => Tick();
            }
            _timer.Start();

            // 挂上就先钉一次：启动时若已经在全屏应用里（开机自启常见），
            // 不能等 400ms 后第一次 tick 才生效。
            EnsureTopmost(hwnd);
        }

        public static void Detach()
        {
            if (_timer != null) _timer.Stop();
            _hwnd = IntPtr.Zero;
        }

        /// <summary>
        /// 立刻重申一次层级，不等定时器。自检用它造「被压下去 → 抢回来」的对照，
        /// 免得为了验一个位还要在探针里睡几百毫秒。
        /// </summary>
        public static void ForceReassert()
        {
            var hwnd = _hwnd;
            if (hwnd == IntPtr.Zero) return;
            EnsureTopmost(hwnd);
        }

        private static void Tick()
        {
            var hwnd = _hwnd;
            if (hwnd == IntPtr.Zero) return;
            if (!IsVisible(hwnd)) return;          // 用户手动隐藏时不必折腾层级

            TickCount++;

            RECT my;
            if (!GetWindowRect(hwnd, out my)) return;

            // 判据从「前台窗口是不是全屏」换成「**有没有窗口真的盖在我身上**」。
            //
            // 换的原因：全屏只是遮挡的一种特例。浮窗拖到任务栏区域时，盖住它的是
            // Shell_TrayWnd（它自己是 TOPMOST）；被别的置顶小工具压住时同理。
            // 按「是不是全屏」判，这些场景一个都覆盖不到 —— 所以 1.0.1 在这两种情况下
            // 定时器整轮都在空转，什么都没做。
            //
            // 现在的判据是枚举真实 Z 序（EnumWindows 从上到下）：
            // 只要在我之前（更高）出现一个可见窗口、且矩形和我有正面积相交，就算被遮挡。
            // 这是量出来的事实，不是猜的。
            bool occluded = AnyWindowAboveCovers(hwnd, my);

            var fg = GetForegroundWindow();
            bool full = fg != IntPtr.Zero && fg != hwnd && (IsFullscreen(fg) || IsSystemFullscreen());
            LastForegroundFullscreen = full;
            LastOccluded = occluded;

            if (occluded || full)
            {
                EnsureTopmost(hwnd);
                // 置顶和「盖住全屏/任务栏」不完全等价：这两类窗口在 DWM 的 band 里排得更高。
                // 往 SYSTEM_TOOLS band 抬是覆盖层的标准做法，但它需要 UIAccess 才真的生效，
                // 所以结果必须回读验证（见 BandStatus），不能调完就假设成功。
                SetBand(hwnd, BandSystemTools);
            }
            else if (_bandRaised)
            {
                SetBand(hwnd, BandDesktop);
            }
        }

        /// <summary>最近一轮判定：浮窗是否被 Z 序更高的窗口盖住。探针可读。</summary>
        public static bool LastOccluded { get; private set; }

        /// <summary>
        /// 暂停 / 恢复定时器。只停轮询、不摘句柄 —— 自检要构造一个「被盖住」的瞬间
        /// 再断言检测结果，如果定时器还在跑，它会在断言之前就把浮窗抢回最上面，
        /// 探针读到的永远是没有遮挡（第一次就是这么误判成「检测失灵」的）。
        /// </summary>
        public static void Suspend() { if (_timer != null) _timer.Stop(); }

        public static void Resume() { if (_timer != null) _timer.Start(); }

        /// <summary>
        /// 立刻按当前状态重算一次遮挡判定，不依赖定时器。
        /// 自检用它制造「任务栏被提到我头上」的真实场景再断言 —— 而不是只看配置位。
        /// </summary>
        public static bool CheckOcclusionNow()
        {
            var hwnd = _hwnd;
            if (hwnd == IntPtr.Zero) return false;
            RECT my;
            if (!GetWindowRect(hwnd, out my)) return false;
            bool hit = AnyWindowAboveCovers(hwnd, my);
            LastOccluded = hit;
            return hit;
        }

        /// <summary>
        /// 让探针把某扇窗口显式提到浮窗之上，用于构造遮挡场景。
        /// 必须用和 EnsureTopmost 相同的两步序列：同 band 内光 HWND_TOPMOST 插不到另一个
        /// topmost 窗口前面（实测只会紧随其后），自检就造不出「真的被盖住」的瞬间。
        /// </summary>
        public static bool RaiseAbove(IntPtr other)
        {
            if (other == IntPtr.Zero) return false;
            bool a = SetWindowPos(other, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            bool b = SetWindowPos(other, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            return a && b;
        }

        /// <summary>最近一次判定时的遮挡者句柄/类名。诊断用，能直接看出是谁压在头上。</summary>
        public static string LastOccluder { get; private set; } = "";

        /// <summary>
        /// 枚举顶层窗口（EnumWindows 的顺序就是 Z 序，从最上层往下），
        /// 在碰到自己之前，如果有一扇可见窗口和我的矩形有正面积相交，就算被遮挡。
        ///
        /// 排除三类，否则会自己触发自己、每 250ms 空转重申一次：
        /// - 我自己的菜单 / ToolTip：它们是**带 owner 的工具窗口**，天然在我上面。
        ///   注意不能「同进程一律排除」—— 那样连「同进程的全屏窗口」也被排掉，
        ///   自检就永远造不出遮挡场景，只能测个寂寞（1.0.1 就栽在这）。
        /// - 桌面（Progman / WorkerW）：铺满全屏但在我下面；
        /// - 最小化窗口：不可见。
        /// </summary>
        private static bool AnyWindowAboveCovers(IntPtr self, RECT my)
        {
            _scanSelf = self;
            _scanRect = my;
            _scanPid = 0;
            GetWindowThreadProcessId(self, out _scanPid);
            _scanHit = false;
            _scanOccluder = "";

            EnumWindows(EnumScanProc, IntPtr.Zero);
            LastOccluder = _scanOccluder;
            return _scanHit;
        }

        /// <summary>
        /// 窗口在真实 Z 序里的序号（0 = 最上层）。找不到返回 -1。
        /// 自检用它做「浮窗到底在任务栏上面还是下面」的硬断言 ——
        /// 比读扩展样式可靠得多：TOPMOST 位两者都有，谁在上只有枚举顺序说得清。
        /// </summary>
        public static int ZOrderIndex(IntPtr target)
        {
            if (target == IntPtr.Zero) return -1;
            _zTarget = target;
            _zIndex = -1;
            _zCursor = 0;
            EnumWindows(EnumZProc, IntPtr.Zero);
            return _zIndex;
        }

        private static IntPtr _zTarget;
        private static int _zIndex;
        private static int _zCursor;

        private static bool EnumZProc(IntPtr h, IntPtr lParam)
        {
            if (h == _zTarget) { _zIndex = _zCursor; return false; }
            _zCursor++;
            return true;
        }

        private static IntPtr _scanSelf;
        private static RECT _scanRect;
        private static uint _scanPid;
        private static bool _scanHit;
        private static string _scanOccluder = "";

        private static bool EnumScanProc(IntPtr h, IntPtr lParam)
        {
            if (h == _scanSelf) return false;              // 已经到我了，上面的窗口全看完了
            if (!IsWindowVisible(h)) return true;
            if (IsIconic(h)) return true;

            // 自己的弹出层（右键菜单 / ToolTip）：有 owner 或带 TOOLWINDOW 的一律跳过。
            // 这些窗口本来就该在我上面；把它们当「遮挡」会变成自己和自己打架，
            // 菜单每 250ms 被顶一次。
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == _scanPid)
            {
                if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            }

            string cls = ClassNameOf(h);
            if (cls == "Progman" || cls == "WorkerW") return true;   // 桌面在最底下

            RECT r;
            if (!GetWindowRect(h, out r)) return true;

            if (r.Right <= _scanRect.Left || r.Left >= _scanRect.Right) return true;
            if (r.Bottom <= _scanRect.Top || r.Top >= _scanRect.Bottom) return true;

            _scanHit = true;
            _scanOccluder = cls + " (0x" + h.ToInt64().ToString("X") + ")";
            return false;
        }

        // 未文档化但在 Win8.1+ 稳定存在：SetWindowBand(HWND, HWND insertAfter, DWORD band)。
        // 用 GetProcAddress 手工绑定而不是 DllImport —— 老系统上缺这个导出时 DllImport 会抛
        // TypeLoadException，整个类型都废掉；手工绑定拿不到就安静跳过。
        private delegate bool SetWindowBandFn(IntPtr hwnd, IntPtr hwndInsertAfter, uint band);

        private const uint BandDesktop = 1;        // ZBID_DESKTOP
        private const uint BandSystemTools = 14;   // ZBID_SYSTEM_TOOLS

        /// <summary>
        /// band 提升的真实结果，只有「回读到的 band 值 == 14」才算成功。
        /// 1.0.1 的自检只断言了「SetWindowBand 这个导出解析到了」，那是一条假绿 ——
        /// 普通权限进程调用它通常直接失败（需要 UIAccess），导出存在但一次没成功过。
        /// </summary>
        public static string BandStatus { get; private set; } = "未尝试";

        private static SetWindowBandFn _setBand;
        private delegate bool GetWindowBandFn(IntPtr hwnd, out uint band);
        private static GetWindowBandFn _getBand;
        private static bool _bandProbed;
        private static bool _bandRaised;

        private static void ProbeBandApi()
        {
            if (_bandProbed) return;
            _bandProbed = true;
            try
            {
                var lib = LoadLibrary("user32.dll");
                if (lib == IntPtr.Zero) return;
                var p = GetProcAddress(lib, "SetWindowBand");
                if (p != IntPtr.Zero)
                    _setBand = (SetWindowBandFn)Marshal.GetDelegateForFunctionPointer(p, typeof(SetWindowBandFn));
                var g = GetProcAddress(lib, "GetWindowBand");
                if (g != IntPtr.Zero)
                    _getBand = (GetWindowBandFn)Marshal.GetDelegateForFunctionPointer(g, typeof(GetWindowBandFn));
            }
            catch (Exception) { _setBand = null; }
        }

        private static void SetBand(IntPtr hwnd, uint band)
        {
            ProbeBandApi();
            if (_setBand == null) { BandStatus = "系统无此接口"; return; }
            if (band == BandSystemTools && _bandRaised) return;   // 已经抬上去了，别每轮重设
            try
            {
                bool ok = _setBand(hwnd, hwnd, band);
                uint back = 0;
                bool readOk = _getBand != null && _getBand(hwnd, out back);

                if (ok && readOk) BandStatus = "回读 band=" + back + (back == band ? "（生效）" : "（未生效）");
                else if (ok) BandStatus = "调用返回成功（无法回读）";
                else BandStatus = "调用失败 err=" + Marshal.GetLastWin32Error();

                // 只有「回读到的值就是目标 band」才算真的抬上去了
                _bandRaised = ok && readOk && back == band;
            }
            catch (Exception ex) { BandStatus = "异常 " + ex.GetType().Name; }
        }

        /// <summary>
        /// 系统级「是否全屏」判定。Windows 用同一套状态决定要不要弹通知：
        /// QUNS_BUSY(2) = 有全屏应用；QUNS_RUNNING_D3D_FULL_SCREEN(3) = D3D 独占全屏；
        /// QUNS_PRESENTATION_MODE(4) = 演示模式。
        /// 这条比量矩形可靠：宿主窗口尺寸不标准的全屏（低于桌面分辨率、居中缩放）也能命中。
        ///
        /// 状态 3 有个特殊含义：D3D 独占全屏绕开 DWM 合成，任何窗口都盖不上去。
        /// 诊断日志用它区分「该修没修好」和「系统不允许」。
        /// </summary>
        public static bool IsSystemFullscreen()
        {
            int st = SystemFullscreenState();
            return st == QUNS_BUSY || st == QUNS_RUNNING_D3D_FULL_SCREEN || st == QUNS_PRESENTATION_MODE;
        }

        /// <summary>原始 QUNS 状态码（0 无法读取）。0=正常 2=全屏 3=D3D独占 4=演示。</summary>
        public static int SystemFullscreenState()
        {
            try
            {
                int st;
                if (SHQueryUserNotificationState(out st) != 0) return 0;
                return st;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// 重申层级。对已经处在 TOPMOST 队首的窗口，这次调用是空操作：
        /// 不重绘、不闪、不抢焦点，所以每 400ms 调一次没有代价。
        /// </summary>
        private static void EnsureTopmost(IntPtr hwnd)
        {
            // 上一次 ClickThrough 切换或别的代码可能把 TOPMOST 位清掉，补回来
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOPMOST) == 0)
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOPMOST);

            // 关键：单靠 HWND_TOPMOST 是空转的。
            //
            // HWND_TOPMOST 的语义是「放到所有**非** topmost 窗口之上」。当浮窗、任务栏、
            // 无边框全屏游戏都已经是 topmost 时，这个调用**不改变组内顺序**，
            // 等于什么都没做 —— 1.0.0 / 1.0.1 的「每 400ms 重申一次」全程空转，
            // 这才是「全屏盖住浮窗」「任务栏压住浮窗」一直没修好的真正原因。
            //
            // 所以分两步走，必须在同一个同步调用里连续做完（不经过消息泵，
            // DWM 不会合成中间态，用户看不到闪）：
            //   1) HWND_TOP 真的把它提到 Z 序最前（副作用：会退出 topmost 组）；
            //   2) HWND_TOPMOST 立刻重新入组 —— 从非 topmost 进入时插入组首，
            //      于是「位」和「顺序」同时到位。
            bool ok = SetWindowPos(hwnd, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);

            ok &= SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);

            // 记结果而不是想当然：如果全屏应用是提权进程（很多游戏带反作弊、以管理员启动），
            // 低完整性级别的浮窗可能被 UIPI 挡住、SetWindowPos 直接返回 FALSE。
            // 这种情况重申多少次都没用，得让浮窗本身也提权 —— 探针要能看出这一条。
            if (!ok) LastReassertError = Marshal.GetLastWin32Error();
            ReassertCount++;
        }

        /// <summary>最近一次 SetWindowPos 失败的错误码（0 = 没失败过）。诊断用。</summary>
        public static int LastReassertError { get; private set; }

        // ==================================================================
        // 全屏判定
        // ==================================================================

        /// <summary>
        /// 窗口是否铺满它所在的那块显示器。
        ///
        /// 只判「覆盖」不判「精确相等」：有游戏会返回比屏幕略大的矩形做边框补偿
        /// （-8,-8 ~ +8,+8），卡死等式会漏判；而最大化的普通窗口拿到的是工作区矩形
        /// （底部被任务栏截掉一条），天然不满足。桌面（Progman / WorkerW）本身就是
        /// 全屏矩形，必须靠类名排除，否则浮窗会一直以为自己处在全屏环境里。
        /// </summary>
        public static bool IsFullscreen(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (!IsVisible(hwnd) || IsIconic(hwnd)) return false;

            string cls = ClassNameOf(hwnd);
            if (cls == "Progman" || cls == "WorkerW"
                || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                return false;

            RECT r;
            if (!GetWindowRect(hwnd, out r)) return false;

            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;

            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(mon, ref mi)) return false;

            const int tol = 2;
            return r.Left <= mi.rcMonitor.Left + tol
                && r.Top <= mi.rcMonitor.Top + tol
                && r.Right >= mi.rcMonitor.Right - tol
                && r.Bottom >= mi.rcMonitor.Bottom - tol;
        }

        private static string ClassNameOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            int n = GetClassName(hwnd, sb, sb.Capacity);
            return n > 0 ? sb.ToString(0, n) : "";
        }

        // ==================================================================
        // Win32
        // ==================================================================

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int pquns);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        // ExactSpelling + Ansi：不加的话运行时会去找 SetWindowBandA/W，导出名对不上就白拿
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        /// <summary>窗口是否还活着。守护定时器会跨窗口关闭，先问一句再动手。</summary>
        private static bool IsVisible(IntPtr hwnd)
        {
            return IsWindow(hwnd) && IsWindowVisible(hwnd);
        }
    }
}
