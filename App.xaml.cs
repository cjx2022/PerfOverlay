using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PerfOverlay.Services;
using PerfOverlay.Views;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;
using Drawing2D = System.Drawing.Drawing2D;

namespace PerfOverlay
{
    public partial class App : Application
    {
        private Mutex _single;
        private AppSettings _settings;
        private MonitorHost _host;
        private OverlayWindow _overlay;
        private SettingsWindow _settingsWin;

        private Forms.NotifyIcon _tray;
        private Forms.ContextMenuStrip _trayMenu;
        private Forms.ToolStripMenuItem _miShow;
        private Forms.ToolStripMenuItem _miStartup;
        private Icon _trayIcon;
        private bool _trayIconOwned;

        private DispatcherTimer _saveTimer;
        private DispatcherTimer _hideTrimTimer;
        private DateTime _lastTrimUtc = DateTime.MinValue;
        private int _uiPending;
        // 最近一帧数据。浮窗藏着时不碰 UI，重新显示要立刻回填，否则会空一秒。
        private MetricsSnapshot _lastSnap;

        /// <summary>
        /// 温度采集要用 LibreHardwareMonitor，它的 dll 与 HidSharp / System.CodeDom
        /// 都作为资源装在 exe 里（发布物保持单文件）。CLR 找不到程序集时从这里加载 ——
        /// 静态构造函数的时机早于任何被 JIT 的方法体，所以不会出现「类型解析先于挂接」。
        /// </summary>
        static App()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedDependency;
        }

        private static System.Reflection.Assembly ResolveEmbeddedDependency(object sender, ResolveEventArgs e)
        {
            try
            {
                string file = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
                string logical = "PerfOverlay.Deps." + file;
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var st = asm.GetManifestResourceStream(logical))
                {
                    if (st == null) return null;
                    var buf = new byte[st.Length];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = st.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return System.Reflection.Assembly.Load(buf);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => LogError("AppDomain", ex.ExceptionObject as Exception);
            try
            {
                StartupCore(e);
            }
            catch (Exception ex)
            {
                LogError("OnStartup", ex);
                try
                {
                    MessageBox.Show("性能浮窗启动失败：\n\n" + ex.Message +
                        "\n\n详细日志：" + Path.Combine(Path.GetTempPath(), "perfoverlay-error.log"),
                        "性能浮窗", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception) { }
                Shutdown(1);
            }
        }

        private static void LogError(string stage, Exception ex)
        {
            WriteLog("ERR", stage, ex == null ? "(null)" : ex.ToString());
        }

        /// <summary>
        /// 常规运行记录。默认**不落盘**，只有带 --diag 启动时才写，
        /// 避免把「谁在什么时候用过什么程序」这类痕迹无谓地留在 %TEMP% 里。
        /// </summary>
        private static void LogInfo(string stage, string message)
        {
            if (!_diagnostic) return;
            WriteLog("INF", stage, message);
        }

        private static bool _diagnostic;

        private const long LogMaxBytes = 256 * 1024;

        private static void WriteLog(string level, string stage, string body)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "perfoverlay-error.log");
                // 日志必须封顶：一个常驻进程反复报错会把磁盘写满
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > LogMaxBytes)
                {
                    try { File.Delete(path); } catch (Exception) { }
                }
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "][" + stage + "] "
                              + body + Environment.NewLine;
                File.AppendAllText(path, line);
            }
            catch (Exception) { }
        }

        private void StartupCore(StartupEventArgs e)
        {
            // --diag：打开常规运行日志（默认关闭，见 LogInfo）
            _diagnostic = HasArg(e.Args, "--diag");

            // --sw：强制软件渲染（必须在建任何 UI 之前设）。用来对比「WPF 走 D3D 时
            // 会把显卡驱动整包映射进来」到底吃掉多少工作集。
            if (HasArg(e.Args, "--sw"))
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode =
                    System.Windows.Interop.RenderMode.SoftwareOnly;
                LogInfo("Render", "软件渲染（--sw）");
            }

            // ---- 自检模式：只采样一次并写报告，不开任何窗口 ----
            if (HasArg(e.Args, "--selftest"))
            {
                SelfTest.Run();
                Shutdown(0);
                return;
            }

            // ---- 布局测量：建一次浮窗并输出真实尺寸，用于排查自适应宽度问题 ----
            if (HasArg(e.Args, "--measure"))
            {
                RunMeasure();
                return;
            }

            // ---- 设置面板体检：一屏装得下、无滚动条、卡片不越界 ----
            if (HasArg(e.Args, "--measure-settings"))
            {
                RunMeasureSettings();
                return;
            }

            // ---- 热键自检：写法解析 + 真实注册 + 占用检测，不开窗口 ----
            if (HasArg(e.Args, "--hotkey-test"))
            {
                RunHotkeyProbe();
                return;
            }

            // ---- 检查更新自检：不开窗口，把 GitHub 返回与版本比较写进报告 ----
            if (HasArg(e.Args, "--check-update"))
            {
                RunUpdateProbe();
                return;
            }

            // ---- 版本窗口探针：开在屏幕外，自动查一次更新并量几何（截图与断言共用）----
            if (HasArg(e.Args, "--about"))
            {
                // --about-hold N：窗口多留 N 秒，方便从外部 PrintWindow 抓图核对
                int hold = 11;
                if (!int.TryParse(ArgValue(e.Args, "--about-hold") ?? "", out hold) || hold < 3) hold = 11;
                RunAboutProbe(hold);
                return;
            }

            // ---- README 配图：离屏渲染控制面板 + 深/浅背景浮窗并合成 ----
            if (HasArg(e.Args, "--shot"))
            {
                string dir = ArgValue(e.Args, "--shot") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs");
                int code = 0;
                try { ReadmeShot.Compose(dir); }
                catch (Exception ex) { LogError("Shot", ex); code = 2; }
                Shutdown(code);
                return;
            }

            // ---- CSV 回放：验证解析 / 多链去重 / 1% Low 聚合（不需要管理员权限）----
            if (HasArg(e.Args, "--replay"))
            {
                string dir = ArgValue(e.Args, "--replay");
                if (dir == null) dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_test", "csv");
                string report = CsvReplay.Run(dir);
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "perfoverlay-replay.txt"), report, Encoding.UTF8);
                }
                catch (Exception) { }
                Shutdown(report.IndexOf("SUMMARY: ALL PASS", StringComparison.Ordinal) >= 0 ? 0 : 2);
                return;
            }

            // ---- GPU 探针：PDH 占用率 + WDDM 温度（两条路都不需要管理员权限）----
            if (HasArg(e.Args, "--gpu-probe"))
            {
                RunGpuProbe();
                Shutdown(0);
                return;
            }

            // ---- 温度组件探针：PawnIO 装没装、能不能读 SMN（不装、不改系统）----
            if (HasArg(e.Args, "--pawnio-status"))
            {
                RunPawnIoProbe();
                Shutdown(0);
                return;
            }

            // ---- 温度组件安装：下载 + 校验 + 静默安装（等价于点面板上的感叹号）----
            if (HasArg(e.Args, "--pawnio-install"))
            {
                RunPawnIoInstall();
                Shutdown(0);
                return;
            }

            // ---- 自动提权 ----
            // 帧采集（FPS / 1% Low）要靠 PresentMon 开 ETW 会话，必须管理员权限。
            // 双击 exe 时如果当前不是管理员，就地拉起一个提权实例再退出自己，
            // 用户不需要记任何启动脚本。这台机器 ConsentPromptBehaviorAdmin=0，
            // 提权是静默的，不会弹 UAC 框。
            //
            // --elevated-attempt 是防循环的保险丝：万一提权成功但进程仍非管理员，
            // 也只允许重试一次。提权失败（被拒绝/策略禁止）时继续以普通权限运行，
            // CPU / 内存 / 显存照常工作，只有 FPS 显示 N/A。
            bool uiProbe = HasArg(e.Args, "--open-settings") || HasArg(e.Args, "--open-menu")
                           || HasArg(e.Args, "--pawnio-status") || HasArg(e.Args, "--pawnio-install");
            if (!PrivilegeHelper.IsElevated
                && !uiProbe
                && !HasArg(e.Args, "--noelevate")
                && !HasArg(e.Args, "--elevated-attempt"))
            {
                // 重复双击时不要去做「提权 + 等 12 秒接管」那套：已经有实例在跑就静默退出。
                // --takeover 是显式接管请求（来自「以管理员身份重启」），必须放行。
                if (!HasArg(e.Args, "--takeover") && InstanceAlreadyRunning())
                {
                    LogInfo("AlreadyRunning", "已有实例在运行，本次启动直接退出");
                    Shutdown(0);
                    return;
                }

                string elevArgs = "--startup --takeover --elevated-attempt"
                                  + (_diagnostic ? " --diag" : "")
                                  + (HasArg(e.Args, "--mem") ? " --mem" : "")
                                  + (HasArg(e.Args, "--mem-hide") ? " --mem-hide" : "")
                                  + ((HasArg(e.Args, "--temp-probe") || HasArg(e.Args, "--lhm-probe")) ? " --temp-probe" : "")
                                  + (HasArg(e.Args, "--sw") ? " --sw" : "");
                string elevErr = PrivilegeHelper.RestartElevated(elevArgs);
                if (elevErr == null)
                {
                    LogInfo("Elevate", "普通权限启动，已转交给提权实例");
                    Shutdown(0);
                    return;
                }
                LogInfo("Elevate", "提权未成功（" + elevErr + "），继续以普通权限运行（FPS 将显示 N/A）");
            }

            // 单实例互斥。--takeover 是「以管理员身份重启」专用：旧实例此刻可能还没退干净
            // （UAC 是同进程内启动的新进程先跑起来，旧进程随后才 Shutdown），
            // 这里必须等它释放互斥体，否则新进程会误判「已有实例」把自己杀掉，
            // 表现就是点了 UAC 的「是」然后什么都没发生。
            bool takeover = HasArg(e.Args, "--takeover");
            bool created;
            _single = new Mutex(true, "Local\\PerfOverlay.Instance", out created);

            if (!created && takeover)
            {
                try
                {
                    created = _single.WaitOne(TimeSpan.FromSeconds(12));
                }
                catch (AbandonedMutexException)
                {
                    // 旧进程被强杀 / 崩溃，互斥体被遗弃；此时所有权已经归我们
                    created = true;
                }
            }

            if (!created)
            {
                LogInfo("Mutex", takeover
                    ? "接管失败：旧实例 12 秒内未退出，本次启动放弃"
                    : "已有实例在运行，本次启动直接退出");
                Shutdown(0);
                return;
            }

            // 权限实况只在 --diag 下记录：判断「提权到底成没成」看设置面板的状态点即可
            LogInfo("Startup", string.Format(
                "pid={0} elevated={1} perfLogUsers={2} args=[{3}]",
                System.Diagnostics.Process.GetCurrentProcess().Id,
                PrivilegeHelper.IsElevated,
                PrivilegeHelper.IsInPerformanceLogUsers,
                string.Join(" ", e.Args)));

            _settings = SettingsStore.Load();
            if (!_settings.FirstRunDone) _settings.FirstRunDone = true;

            // ---- 温度探针：列出 LibreHardwareMonitor 能读到的所有温度传感器 ----
            if (HasArg(e.Args, "--temp-probe") || HasArg(e.Args, "--lhm-probe"))
            {
                RunTempProbe();
                Shutdown(0);
                return;
            }

            // ---- 托盘菜单探针：只把菜单弹出来备截图，不建浮窗、不启采集 ----
            if (HasArg(e.Args, "--tray-menu"))
            {
                RunTrayMenuProbe();
                return;
            }

            _host = new MonitorHost(MonitorHost.PresentMonPath());
            _host.Diagnostic = _diagnostic;
            _host.Updated += OnMetricsUpdated;

            _overlay = new OverlayWindow();
            _overlay.SettingsRequested += (s, a) => ShowSettings();
            _overlay.HideRequested += (s, a) => SetOverlayVisible(false);
            _overlay.ExitRequested += (s, a) => ExitApp();
            _overlay.PositionChanged += (s, cfg) => ApplySettings(cfg, false);

            _overlay.ApplySettings(_settings, true);

            if (_settings.OverlayVisible)
            {
                _overlay.Show();
            }
            else
            {
                // 上次退出时是「隐藏」：这条启动路径根本不经过 SetOverlayVisible，
                // 于是回收永远不会被排上 —— 开机自启常驻、浮窗从没露过面，
                // 工作集就一路涨着不下来（实测 20 → 50 MB）。
                // 这里补一次，延迟放宽到 5 秒：WPF 与采集引擎这时还在爬。
                ScheduleHideTrim(5000);
            }

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += (s, a) => { _saveTimer.Stop(); SettingsStore.Save(_settings); };

            InitTray();
            InitHotkey();
            _host.Start(_settings);

            // ---- UI 探针：仅供截图核对，正常启动不走这里（静默：窗口放屏幕外，不抢焦点）----
            if (HasArg(e.Args, "--open-settings")) ShowSettings(true);
            if (HasArg(e.Args, "--open-menu")) _overlay.OpenMenuAt(320, 160);

            // ---- 内存体检：在真实常驻态下分三次采样，跑完自己退出 ----
            if (HasArg(e.Args, "--mem")) RunMemProbe();

            // ---- 位置探针：三档落点是否贴边/居中 ----
            if (HasArg(e.Args, "--align")) RunAlignProbe(ArgValue(e.Args, "--align"));

            bool isStartupLaunch = HasArg(e.Args, "--startup") || HasArg(e.Args, "--tray");
            if (!isStartupLaunch)
            {
                // 首次运行给个引导；之后再启动就静默待在右上角
                if (!File.Exists(SettingsStore.FilePath)) ShowSettings();
            }

            DispatcherUnhandledException += (s, ex) =>
            {
                ex.Handled = true;
                LogError("Dispatcher", ex.Exception);
            };
        }

        /// <summary>
        /// 内存体检：常驻态下在 10s / 30s / 60s 各采一次（前两次不打扰进程，
        /// 最后一次才做强制回收与工作集修剪），结果写 %TEMP%\perfoverlay-mem.txt 后退出。
        /// </summary>
        /// <summary>
        /// 位置探针：把浮窗贴到 0=左上 / 1=中上 / 2=右上，量出窗口实际落点。
        /// 只证明「算出来的位置对、没跑到屏幕外」，不证明界面按钮点得动。
        /// </summary>
        private void RunAlignProbe(string arg)
        {
            int mode;
            if (!int.TryParse(arg ?? "", out mode) || mode < 0 || mode > 2) mode = 2;
            string path = Path.Combine(Path.GetTempPath(), "perfoverlay-align.txt");
            try { File.Delete(path); } catch (Exception) { }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (s, a) =>
            {
                timer.Stop();
                var sb = new StringBuilder();
                try
                {
                    _overlay.Show();
                    _overlay.AlignHorizontal(mode);
                    _overlay.UpdateLayout();

                    var work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                    double dpi = 1.0;
                    try { dpi = Media.VisualTreeHelper.GetDpi(_overlay).DpiScaleX; } catch (Exception) { }
                    if (dpi <= 0) dpi = 1.0;

                    double wl = work.Left / dpi, wr = work.Right / dpi;
                    double left = _overlay.Left, right = left + _overlay.ActualWidth;
                    double lGap = left - wl, rGap = wr - right;

                    bool inside = left >= wl - 1 && right <= wr + 1;
                    bool placed = inside;
                    if (mode == 0) placed = placed && Math.Abs(lGap - Views.OverlayWindow.EdgeRight) <= 2;
                    if (mode == 2) placed = placed && Math.Abs(rGap - Views.OverlayWindow.EdgeRight) <= 2;
                    if (mode == 1) placed = placed && Math.Abs(lGap - rGap) <= 2;

                    sb.AppendLine("mode       = " + mode);
                    sb.AppendLine(string.Format("work       = {0:0.0}..{1:0.0} DIP（DPI {2:0.00}）", wl, wr, dpi));
                    sb.AppendLine(string.Format("window     = {0:0.0}..{1:0.0}（宽 {2:0.0}）", left, right, _overlay.ActualWidth));
                    sb.AppendLine(string.Format("gaps       = 左 {0:0.0} / 右 {1:0.0}", lGap, rGap));
                    sb.AppendLine("in-screen  = " + (inside ? "OK" : "FAIL（跑出工作区）"));
                    sb.AppendLine("SUMMARY: " + (placed ? "OK" : "FAIL"));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("EXCEPTION: " + ex.Message);
                    sb.AppendLine("SUMMARY: FAIL");
                }

                try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch (Exception) { }
                Shutdown(0);
            };
            timer.Start();
        }

        private void RunMemProbe()
        {
            string path = Path.Combine(Path.GetTempPath(), "perfoverlay-mem.txt");
            try { File.Delete(path); } catch (Exception) { }

            // --mem-hide：第一次采样后把浮窗藏起来，用来验证「隐藏即回收」真的跑到
            bool hideAfterFirst = HasArg(Environment.GetCommandLineArgs(), "--mem-hide");
            bool wasVisible = _settings == null || _settings.OverlayVisible;
            var marks = new[] { 10, 30, 60 };
            int i = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(marks[0]) };
            timer.Tick += (s, a) =>
            {
                timer.Stop();
                try
                {
                    File.AppendAllText(path, MemProbe.Capture(i == marks.Length - 1), Encoding.UTF8);
                }
                catch (Exception) { }
                i++;
                if (i == 1 && hideAfterFirst) SetOverlayVisible(false);
                if (i < marks.Length)
                {
                    timer.Interval = TimeSpan.FromSeconds(marks[i] - marks[i - 1]);
                    timer.Start();
                    return;
                }
                // 探针不能改用户配置：把 OverlayVisible 原样写回去
                if (hideAfterFirst && _settings != null)
                {
                    _settings.OverlayVisible = wasVisible;
                    SettingsStore.Save(_settings);
                }
                Shutdown(0);
            };
            timer.Start();
        }

        /// <summary>
        /// 托盘菜单探针：真把菜单 Show 出来（NotifyIcon 右键走的就是同一个对象），
        /// 供截图核对皮肤。15 秒后自动退出，避免留下孤儿进程。
        /// </summary>
        private void RunTrayMenuProbe()
        {
            InitTray();
            if (_trayMenu == null) { Shutdown(2); return; }

            var wa = Forms.Screen.PrimaryScreen.WorkingArea;
            // 静默：弹到屏幕外，靠 PrintWindow 抓图，用户看不到闪一下
            _trayMenu.Show(new System.Drawing.Point(-4200, wa.Top + 120));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += (s, a) => { timer.Stop(); Shutdown(0); };
            timer.Start();
        }

        /// <summary>
        /// 设置面板体检：三个硬指标，全部数值化，不靠看截图。
        ///   1) 一屏装得下 —— 窗口高度 ≤ 工作区高度
        ///   2) 不滚动 —— 视觉树里 ScrollViewer 数量必须为 0
        ///   3) 不裁切 —— SizeToContent 下 ActualHeight 应等于内容所需高度
        /// </summary>
        private void RunMeasureSettings()
        {
            var sb = new System.Text.StringBuilder();
            int fails = 0;

            var w = new SettingsWindow();
            w.Bind(SettingsStore.Load());
            // 排版验证必须离屏：Opacity=0 仍会完整跑布局与渲染，量出来的尺寸是真的，
            // 但屏幕上什么都不会出现 —— 不能为了验个边距把窗口弹到用户眼前。
            w.Opacity = 0;
            w.MoveOffscreen();
            w.Show();
            w.UpdateLayout();

            double winW = w.ActualWidth;
            double winH = w.ActualHeight;
            var need = w.MeasureContentHeight();
            int scrollers = CountVisuals<System.Windows.Controls.ScrollViewer>(w);

            double dpi;
            try { dpi = Media.VisualTreeHelper.GetDpi(w).DpiScaleY; } catch (Exception) { dpi = 1.0; }
            if (dpi <= 0) dpi = 1.0;

            var work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            double workH = work.Height / dpi;
            double workW = work.Width / dpi;

            bool fits = winH <= workH + 0.5;
            bool noScroll = scrollers == 0;
            bool noClip = Math.Abs(winH - need.Height) <= 1.0;
            if (!fits || !noScroll || !noClip) fails++;

            // 卡片矩形：同行必须等高、左右必须等宽、左右外边距必须相等
            var cards = w.Cards();
            var rects = new System.Windows.Rect[cards.Length];
            for (int i = 0; i < cards.Length; i++)
            {
                var p = cards[i].TranslatePoint(new System.Windows.Point(0, 0), w);
                rects[i] = new System.Windows.Rect(p.X, p.Y, cards[i].ActualWidth, cards[i].ActualHeight);
            }

            bool row1Same = Math.Abs(rects[0].Height - rects[1].Height) <= 0.5;
            // 第 2 行故意不等高：右下「帧数采集」按内容自适应高度，下边缘上移，
            // 「恢复默认 / 完成」被放到卡片外面。所以这里只要求"不比左卡高" + 顶对齐。
            bool row2Lower = rects[3].Height <= rects[2].Height + 0.5;
            bool row1Top = Math.Abs(rects[0].Top - rects[1].Top) <= 0.5;
            bool row2Top = Math.Abs(rects[2].Top - rects[3].Top) <= 0.5;
            // 容差取 1 DIP：1.25 DPI 下 UseLayoutRounding 的量化步长是 0.8 DIP（一个设备像素），
            // 两列各占 352.8 / 352.0 属正常，肉眼看不出，也不该为了凑整关掉像素对齐。
            bool colsSame = Math.Abs(rects[0].Width - rects[1].Width) <= 1.0;
            bool leftEdge = Math.Abs(rects[0].Left - rects[2].Left) <= 0.5;
            bool rightEdge = Math.Abs((winW - rects[1].Right) - rects[0].Left) <= 0.5;
            bool align = row1Same && row2Lower && row1Top && row2Top && colsSame && leftEdge && rightEdge;
            if (!align) fails++;

            // 位置三档：同一行、等宽、文字放得下
            var posBtns = w.PositionButtons();
            double posTop = posBtns[0].TranslatePoint(new System.Windows.Point(0, 0), w).Y;
            bool posSameRow = true, posSameW = true, posFits = true;
            double posRight = 0;
            for (int i = 0; i < posBtns.Length; i++)
            {
                var b = posBtns[i];
                var p = b.TranslatePoint(new System.Windows.Point(0, 0), w);
                if (Math.Abs(p.Y - posTop) > 0.5) posSameRow = false;
                if (Math.Abs(b.ActualWidth - posBtns[0].ActualWidth) > 1.0) posSameW = false;
                double needW = b.DesiredSize.Width - b.Margin.Left - b.Margin.Right;
                if (b.ActualWidth + 0.5 < needW) posFits = false;
                if (p.X + b.ActualWidth > posRight) posRight = p.X + b.ActualWidth;
            }
            if (!posSameRow || !posSameW || !posFits) fails++;

            // 热键框：与位置按钮组右端对齐、够放长组合、不越卡片右界
            var hk = w.HotkeyBoxElement;
            var hkPos = hk.TranslatePoint(new System.Windows.Point(0, 0), w);
            double behaviorRight = rects[2].Right - 15.0;   // 卡片左右内边距 15
            bool hkInside = hkPos.X + hk.ActualWidth <= behaviorRight + 0.5;
            bool hkRightAligned = Math.Abs((hkPos.X + hk.ActualWidth) - posRight) <= 1.0;
            bool hkWide = hk.ActualWidth >= 120.0;
            if (!hkInside || !hkRightAligned || !hkWide) fails++;

            // 通用检查（不只盯复位按钮）：任何一个按钮都不许被压窄，也不许越出内容区右边界。
            // 底栏并进右下卡片后，一行里并排两个按钮，最容易在这里悄悄压掉内边距。
            // 容差 1 DIP：1.25 DPI 下 UseLayoutRounding 的量化步长是 0.8 DIP，
            // 且 Measure(∞) 与父级约束下测出的期望宽度本来就会差零点几。
            var content = w.ContentArea;
            var contentRight = content.TranslatePoint(new System.Windows.Point(0, 0), w).X + content.ActualWidth;
            int squeezed = 0, overflow = 0, btnChecked = 0;
            var badBtns = new StringBuilder();
            var btnList = FindVisuals<System.Windows.Controls.Button>(w);
            foreach (var b in btnList)
            {
                if (!b.IsVisible || b.ActualWidth <= 0) continue;
                // 标题栏里的关闭按钮不属于内容区，别拿内容区边界去卡它
                if (!IsUnder(b, content)) continue;
                btnChecked++;
                var bp = b.TranslatePoint(new System.Windows.Point(0, 0), w);
                // 注意：这里绝不能重新 Measure(∞) 再比 —— 重测会把 DesiredSize 换成
                // "不限宽时的理想值"，而真正要问的是"布局阶段它自己想多宽、实际分到多宽"。
                // 另外 FrameworkElement.DesiredSize 是**含 Margin** 的（MeasureCore 末尾加的），
                // 而 ActualWidth 不含，所以必须把水平 Margin 扣掉再比，
                // 否则任何带右边距的按钮都会被误判成"被压缩 8 DIP"。
                double needW = b.DesiredSize.Width - b.Margin.Left - b.Margin.Right;
                if (bp.X + b.ActualWidth > contentRight + 1.0)
                {
                    overflow++;
                    badBtns.Append("越界:").Append(TextOf(b)).Append(' ');
                }
                if (b.ActualWidth + 1.0 < needW)
                {
                    squeezed++;
                    badBtns.Append(string.Format("压缩:{0}({1:0.0}<{2:0.0}) ", TextOf(b), b.ActualWidth, needW));
                }
            }
            bool btnsOk = squeezed == 0 && overflow == 0;
            if (!btnsOk) fails++;

            // 「显示项目」两列布局：列内左对齐、左右同一行顶对齐、两列不重叠、
            // 每个开关的文字都放得下（Switch 模板固定占 40 DIP 轨道 + 12 DIP 内边距）。
            // 只看截图容易漏掉「文字被压窄一点点」这种问题，这里按 rect 与实测字宽断言。
            var metricBoxes = w.MetricSwitches();
            var mbRect = new System.Windows.Rect[metricBoxes.Length];
            for (int i = 0; i < metricBoxes.Length; i++)
            {
                var p = metricBoxes[i].TranslatePoint(new System.Windows.Point(0, 0), w);
                mbRect[i] = new System.Windows.Rect(p.X, p.Y, metricBoxes[i].ActualWidth, metricBoxes[i].ActualHeight);
            }

            bool colA = true, colB = true, rowsTied = true, textFits = true;
            var tight = new StringBuilder();
            for (int i = 0; i < 4 && i + 4 < mbRect.Length; i++)
            {
                if (Math.Abs(mbRect[i].Left - mbRect[0].Left) > 0.5) colA = false;
                if (Math.Abs(mbRect[i + 4].Left - mbRect[4].Left) > 0.5) colB = false;
                if (Math.Abs(mbRect[i].Top - mbRect[i + 4].Top) > 0.5) rowsTied = false;
            }
            for (int i = 0; i < metricBoxes.Length; i++)
            {
                var cb = metricBoxes[i] as System.Windows.Controls.CheckBox;
                string label = (cb == null || cb.Content == null) ? "" : cb.Content.ToString();
                double avail = mbRect[i].Width - 40 - 12;
                double needW = TextWidthDip(label, 13, FontWeights.Normal);
                if (needW > avail + 0.5)
                {
                    textFits = false;
                    tight.Append(string.Format("{0}({1:0.0}>{2:0.0}) ", label, needW, avail));
                }
                if (mbRect[i].Left < rects[0].Left - 0.5 || mbRect[i].Right > rects[0].Right + 0.5) textFits = false;
            }
            // 列间要有实际空隙：贴在一起就等于没有分栏
            double colGapDip = mbRect[4].Left - mbRect[0].Right;
            bool colsApart = colGapDip >= 8.0;
            bool metricsOk = colA && colB && rowsTied && colsApart && textFits;
            if (!metricsOk) fails++;

            sb.AppendLine("== 设置面板 ==");
            sb.AppendLine(string.Format("   窗口      = {0:0.0} x {1:0.0} DIP", winW, winH));
            sb.AppendLine(string.Format("   内容需要  = {0:0.0} x {1:0.0} DIP", need.Width, need.Height));
            sb.AppendLine(string.Format("   工作区    = {0:0.0} x {1:0.0} DIP（DPI {2:0.00}）", workW, workH, dpi));
            sb.AppendLine(string.Format("   一屏装下  = {0}", fits ? "OK" : "FAIL（比屏幕还高）"));
            sb.AppendLine(string.Format("   无滚动条  = {0}（ScrollViewer 数量 {1}）", noScroll ? "OK" : "FAIL", scrollers));
            sb.AppendLine(string.Format("   无垂直裁切= {0}（差值 {1:0.0} DIP）", noClip ? "OK" : "FAIL", winH - need.Height));
            sb.AppendLine();
            sb.AppendLine("   卡片矩形：");
            string[] names = { "显示项目", "外观", "行为", "帧数采集" };
            for (int i = 0; i < rects.Length; i++)
            {
                sb.AppendLine(string.Format("     {0,-6} x={1,6:0.0} y={2,6:0.0} w={3,6:0.0} h={4,6:0.0}",
                    names[i], rects[i].X, rects[i].Y, rects[i].Width, rects[i].Height));
            }
            sb.AppendLine(string.Format("   同行等高  = {0} / 右下更矮 {1}", row1Same ? "OK" : "FAIL", row2Lower ? "OK" : "FAIL"));
            sb.AppendLine(string.Format("   同行顶对齐= {0} / {1}", row1Top ? "OK" : "FAIL", row2Top ? "OK" : "FAIL"));
            sb.AppendLine(string.Format("   左右等宽  = {0}", colsSame ? "OK" : "FAIL"));
            sb.AppendLine(string.Format("   左右外边距相等 = {0}", rightEdge ? "OK" : "FAIL"));
            sb.AppendLine(string.Format("   卡片对齐  = {0}", align ? "OK" : "FAIL"));
            sb.AppendLine(string.Format("   位置三档  = 同行 {0} / 等宽 {1} / 文字够 {2}（各宽 {3:0.0}）",
                posSameRow ? "OK" : "FAIL", posSameW ? "OK" : "FAIL", posFits ? "OK" : "FAIL", posBtns[0].ActualWidth));
            sb.AppendLine(string.Format("   热键框    = x {0:0.0}..{1:0.0} / 按钮组右端 {2:0.0} / 卡右界 {3:0.0} / 宽 {4:0.0} => {5}",
                hkPos.X, hkPos.X + hk.ActualWidth, posRight, behaviorRight, hk.ActualWidth,
                (hkInside && hkRightAligned && hkWide) ? "OK" : "FAIL（不对齐或越界）"));
            sb.AppendLine(string.Format("   全部按钮  = {0} 个，压缩 {1} / 越界 {2} => {3}",
                btnChecked, squeezed, overflow, btnsOk ? "OK" : "FAIL"));
            if (!btnsOk && badBtns.Length > 0) sb.AppendLine("     明细：" + badBtns.ToString().Trim());
            sb.AppendLine(string.Format("   显示项目两列 = 左列对齐 {0} / 右列对齐 {1} / 同行顶对齐 {2} / 列间 {3:0.0} DIP / 文字够 {4} => {5}",
                colA ? "OK" : "FAIL", colB ? "OK" : "FAIL", rowsTied ? "OK" : "FAIL",
                colGapDip, textFits ? "OK" : "FAIL", metricsOk ? "OK" : "FAIL"));
            if (!textFits && tight.Length > 0) sb.AppendLine("     放不下：" + tight.ToString().Trim());

            // ---- 感叹号点亮时的 CT 行 ----
            // 它平时是 Collapsed，光看默认布局等于没测。这里临时点亮再量三件事：
            //   1) 不压在开关上；2) 不越出卡片内容区；3) 开关本身没被挤窄（否则文字会被裁）。
            string warnLine;
            bool warnOk = true;
            {
                var row = w.TempRow();
                var chk = row[0];
                var btn = row[1];
                double chkBefore = chk.ActualWidth;

                btn.Visibility = System.Windows.Visibility.Visible;
                w.UpdateLayout();

                var chkT = chk.TransformToAncestor(w).Transform(new System.Windows.Point(0, 0));
                var btnT = btn.TransformToAncestor(w).Transform(new System.Windows.Point(0, 0));
                var cardT = w.Cards()[0].TransformToAncestor(w).Transform(new System.Windows.Point(0, 0));

                double chkRight = chkT.X + chk.ActualWidth;
                double btnLeft = btnT.X;
                double btnRight = btnLeft + btn.ActualWidth;
                double limitRight = cardT.X + w.Cards()[0].ActualWidth - 14;   // 卡片内边距 15，留 1 DIP 余量

                bool noOverlap = btnLeft >= chkRight - 0.5;
                bool inCard = btnRight <= limitRight + 0.5;
                bool chkKept = Math.Abs(chk.ActualWidth - chkBefore) < 0.5;
                warnOk = noOverlap && inCard && chkKept;

                warnLine = string.Format(
                    "   温度感叹号 = 不压开关 {0} / 不越界 {1}（右端 {2:0.0} ≤ {3:0.0}）/ 开关未被挤窄 {4} => {5}",
                    noOverlap ? "OK" : "FAIL", inCard ? "OK" : "FAIL", btnRight, limitRight,
                    chkKept ? "OK" : "FAIL", warnOk ? "OK" : "FAIL");

                btn.Visibility = System.Windows.Visibility.Collapsed;
                w.UpdateLayout();
            }
            if (!warnOk) fails++;
            sb.AppendLine(warnLine);
            sb.AppendLine();

            w.CloseForShutdown();

            sb.AppendLine(fails == 0 ? "SUMMARY: ALL PASS" : ("SUMMARY: FAIL " + fails));
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "perfoverlay-settings.txt"), sb.ToString());
            }
            catch (Exception) { }

            Shutdown(fails == 0 ? 0 : 2);
        }

        /// <summary>
        /// 温度探针：把 LibreHardwareMonitor 能读到的每个温度传感器都列出来。
        /// 需要管理员（它要加载内核驱动读 MSR / SMN），所以这条命令会先自提权再执行。
        /// </summary>
        /// <summary>
        /// CPU 温度探针：只走 ACPI 热区。它不需要管理员、不加载驱动、不需要装任何软件，
        /// 所以输出里不再有「提权后才读得到」这类前提；读不到就是固件没实现 _TZ。
        /// </summary>
        private void RunTempProbe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== CPU 温度探针（ACPI 热区，零依赖） ==");
            sb.AppendLine("管理员: " + PrivilegeHelper.IsElevated);
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("OS: " + Environment.OSVersion.Version);
            sb.AppendLine();

            var at = new PerfOverlay.Services.AcpiTempProbe();
            try
            {
                bool ok = at.Probe();
                sb.AppendLine("探测结果: " + ok);
                sb.AppendLine("热区数量: " + at.ZoneCount);
                sb.AppendLine("状态: " + at.Status);
                sb.AppendLine("热区名: " + (at.SensorName.Length > 0 ? at.SensorName : "(无)"));

                if (ok)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        double t;
                        string why;
                        if (at.TryRead(out t, out why))
                            sb.AppendLine(string.Format("  读数{0}: {1:0.0} ℃", i + 1, t));
                        else
                            sb.AppendLine(string.Format("  读数{0}: 失败 {1}", i + 1, why));
                        System.Threading.Thread.Sleep(700);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.GetType().Name + " " + ex.Message);
            }
            finally { at.Dispose(); }

            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "perfoverlay-temp.txt"),
                                  sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// GPU 探针：验证 PDH 占用率（任务管理器同源）与 WDDM 温度（免驱动免权限）。
        /// 两条路都不需要管理员，所以这个分支放在提权判断之前。
        /// </summary>
        private void RunGpuProbe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== GPU 探针 ==");
            sb.AppendLine("管理员: " + PrivilegeHelper.IsElevated);
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(string.Format("OS: {0}", Environment.OSVersion.Version));
            sb.AppendLine(string.Format("PDH 结构体布局: FMT_COUNTERVALUE={0}B  ITEM_W={1}B（期望 16 / 24）",
                PerfOverlay.Services.PdhGpuUsage.StructSizeValue,
                PerfOverlay.Services.PdhGpuUsage.StructSizeItem));
            sb.AppendLine();

            long luid = 0;
            string adapter = "";
            try
            {
                var mem = new GpuMemoryProbe();
                if (mem.Initialize())
                {
                    luid = mem.AdapterLuid;
                    adapter = mem.AdapterName;
                }
                sb.AppendLine(string.Format("主显卡: {0}  LUID=0x{1:X16}", adapter, luid));
                foreach (string line in mem.Trace) sb.AppendLine("  " + line);
                mem.Dispose();
            }
            catch (Exception ex)
            {
                sb.AppendLine("显存探针异常: " + ex.Message);
            }
            sb.AppendLine();

            // ---- PDH 占用率：连采三次，看数值是否随负载变化 ----
            sb.AppendLine("-- PDH GPU 引擎占用率 --");
            var pdh = new PerfOverlay.Services.PdhGpuUsage();
            try
            {
                pdh.SetAdapter(luid);
                if (pdh.EnsureOpen())
                {
                    sb.AppendLine("打开 = OK");
                    for (int i = 0; i < 3; i++)
                    {
                        double pct;
                        if (pdh.TrySample(out pct))
                            sb.AppendLine(string.Format("  采样{0}: {1:0.0}%   {2}", i + 1, pct, pdh.Detail));
                        else
                            sb.AppendLine(string.Format("  采样{0}: 失败  {1}", i + 1, pdh.Status));
                        System.Threading.Thread.Sleep(1000);
                    }
                }
                else
                {
                    sb.AppendLine("打开失败: " + pdh.Status);
                }
                sb.AppendLine("引擎数: " + pdh.EngineCount);
            }
            catch (Exception ex) { sb.AppendLine("PDH 异常: " + ex.GetType().Name + " " + ex.Message); }
            finally { pdh.Dispose(); }
            sb.AppendLine();

            // ---- WDDM 温度 ----
            sb.AppendLine("-- WDDM 适配器温度（KMTQAITYPE_ADAPTERPERFDATA=62）--");
            var wddm = new PerfOverlay.Services.WddmGpuTemp();
            try
            {
                if (wddm.Attach(luid))
                {
                    double t;
                    for (int i = 0; i < 2; i++)
                    {
                        if (wddm.TryRead(out t)) sb.AppendLine(string.Format("  读数{0}: {1:0.0} ℃   {2}", i + 1, t, wddm.Detail));
                        else sb.AppendLine(string.Format("  读数{0}: 失败  {1}", i + 1, wddm.Status));
                        System.Threading.Thread.Sleep(500);
                    }
                }
                else
                {
                    sb.AppendLine("不可用: " + wddm.Status);
                }
            }
            catch (Exception ex) { sb.AppendLine("WDDM 异常: " + ex.GetType().Name + " " + ex.Message); }
            sb.AppendLine();

            // ---- 对照：NVML ----
            sb.AppendLine("-- 对照 NVML（N 卡驱动自带，已是兜底路径）--");
            var nv = new NvmlProbe();
            try
            {
                if (nv.EnsureInit())
                {
                    double t, u;
                    bool tOk, uOk;
                    if (nv.TryRead(out t, out u, out tOk, out uOk))
                        sb.AppendLine(string.Format("  {0}: 温度={1}  利用率={2}",
                            nv.DeviceName,
                            tOk ? t.ToString("0.0") + " ℃" : "不可读",
                            uOk ? u.ToString("0") + "%" : "不可读"));
                    else
                        sb.AppendLine("  读取失败: " + nv.Status);
                }
                else
                {
                    sb.AppendLine("  不可用: " + nv.Status);
                }
            }
            catch (Exception ex) { sb.AppendLine("  NVML 异常: " + ex.Message); }
            finally { nv.Dispose(); }

            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "perfoverlay-gpu.txt"),
                                  sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 温度组件安装（等价于点设置面板上的感叹号 → 下载并安装）。
        /// 保留成命令行是为了两件事：自动化验证，以及用户机器上出问题时能一条命令重装。
        /// </summary>
        private void RunPawnIoInstall()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== CPU 温度组件安装 ==");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("管理员: " + PrivilegeHelper.IsElevated);
            sb.AppendLine();

            string why;

            sb.AppendLine("-- 1. 获取安装包 --");
            string path = null;
            try { path = PerfOverlay.Services.PawnIoInstaller.EnsureSetup(out why); }
            catch (Exception ex) { why = ex.Message; }

            if (path == null)
            {
                sb.AppendLine("  失败: " + why);
                WritePawnIoInstallLog(sb);
                return;
            }
            sb.AppendLine("  路径: " + path);
            sb.AppendLine("  大小: " + new FileInfo(path).Length.ToString("N0") + " 字节");

            sb.AppendLine();
            sb.AppendLine("-- 2. 校验签名与体积 --");
            string vwhy;
            bool ok = PerfOverlay.Services.PawnIoInstaller.Validate(path, out vwhy);
            sb.AppendLine("  " + (ok ? "通过" : "失败: " + vwhy));
            if (!ok) { WritePawnIoInstallLog(sb); return; }

            sb.AppendLine();
            sb.AppendLine("-- 3. 静默安装（-install -silent，UAC 由安装器自己请求）--");
            string err = PerfOverlay.Services.PawnIoInstaller.Install(path);
            sb.AppendLine("  " + (err == null ? "成功" : "失败: " + err));

            sb.AppendLine();
            sb.AppendLine("-- 4. 安装后验证 --");
            sb.AppendLine("  服务已注册: " + (PerfOverlay.Services.PawnIoBridge.IsServiceRegistered() ? "是" : "否"));

            var pawn = new PerfOverlay.Services.PawnIoCpuTemp();
            try
            {
                bool usable = pawn.Probe();
                sb.AppendLine("  设备可读: " + (usable ? "是" : "否"));
                sb.AppendLine("  驱动状态: " + pawn.DriverState);
                sb.AppendLine("  说明: " + pawn.Status);

                if (usable)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        double t;
                        string rwhy;
                        if (pawn.TryRead(out t, out rwhy))
                            sb.AppendLine(string.Format("  CT 采样{0}: {1:0.0} ℃", i + 1, t));
                        else
                            sb.AppendLine(string.Format("  CT 采样{0}: 失败 —— {1}", i + 1, rwhy));
                        System.Threading.Thread.Sleep(600);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  异常: " + ex.GetType().Name + " " + ex.Message);
            }
            finally { pawn.Dispose(); }

            WritePawnIoInstallLog(sb);
        }

        private static void WritePawnIoInstallLog(System.Text.StringBuilder sb)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "perfoverlay-pawnio-install.txt"),
                                  sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 温度组件探针：只读不写 —— 不安装、不改注册表、不留文件（除了这份报告）。
        /// 用来回答三个问题：这台机器该不该走 PawnIO、驱动在不在、装完之后读数是多少。
        /// </summary>
        private void RunPawnIoProbe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== CPU 温度组件探针 ==");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("管理员: " + PrivilegeHelper.IsElevated);
            sb.AppendLine(string.Format("OS: {0}  ({1})", Environment.OSVersion.Version,
                                        Environment.Is64BitProcess ? "x64 进程" : "x86 进程"));
            sb.AppendLine();
            sb.AppendLine("-- 处理器 --");
            sb.AppendLine("  " + PerfOverlay.Services.PawnIoCpuTemp.CpuSummary);
            sb.AppendLine("  模块白名单 family 17h~1Ah: "
                          + (PerfOverlay.Services.PawnIoCpuTemp.IsSupportedCpu ? "命中" : "不命中"));
            sb.AppendLine();

            sb.AppendLine("-- 第一级：ACPI 热区（零依赖）--");
            var acpi = new PerfOverlay.Services.AcpiTempProbe();
            try
            {
                bool ok = acpi.Probe();
                sb.AppendLine("  可用: " + (ok ? "是" : "否"));
                sb.AppendLine("  状态: " + acpi.Status);
                if (ok)
                {
                    double t;
                    string why;
                    sb.AppendLine("  热区数: " + acpi.ZoneCount + "  传感器: " + acpi.SensorName);
                    if (acpi.TryRead(out t, out why))
                        sb.AppendLine(string.Format("  读数: {0:0.0} ℃", t));
                    else
                        sb.AppendLine("  读取失败: " + why);
                }
            }
            catch (Exception ex) { sb.AppendLine("  异常: " + ex.Message); }
            finally { acpi.Dispose(); }
            sb.AppendLine();

            sb.AppendLine("-- 第二级：PawnIO + AMDFamily17（需安装）--");
            sb.AppendLine("  服务已注册: " + (PerfOverlay.Services.PawnIoInstaller.IsServiceRegistered() ? "是" : "否"));
            // 只查有没有，不触发下载：探针必须保持「只读、不联网」的承诺
            string cache = PerfOverlay.Services.PawnIoInstaller.CachePath;
            string sidecar = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                          PerfOverlay.Services.PawnIoInstaller.SetupFileName);
            sb.AppendLine("  安装包缓存: " + cache + (File.Exists(cache) ? "（已有）" : "（无）"));
            sb.AppendLine("  程序旁安装包: " + sidecar + (File.Exists(sidecar) ? "（已有）" : "（无）"));
            sb.AppendLine();

            var pawn = new PerfOverlay.Services.PawnIoCpuTemp();
            try
            {
                bool ok = pawn.Probe();
                sb.AppendLine("  可用: " + (ok ? "是" : "否"));
                sb.AppendLine("  驱动状态: " + pawn.DriverState);
                sb.AppendLine("  说明: " + pawn.Status);

                if (ok)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        double t;
                        string why;
                        if (pawn.TryRead(out t, out why))
                            sb.AppendLine(string.Format("  采样{0}: {1:0.0} ℃", i + 1, t));
                        else
                            sb.AppendLine(string.Format("  采样{0}: 失败 —— {1}", i + 1, why));
                        System.Threading.Thread.Sleep(500);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  异常: " + ex.GetType().Name + " " + ex.Message);
            }
            finally { pawn.Dispose(); }

            sb.AppendLine();
            sb.AppendLine("-- 结论 --");
            string state = PerfOverlay.Services.PawnIoInstaller.DescribeState();
            sb.AppendLine("  UI 状态: " + state);
            switch (state)
            {
                case "ready":
                    sb.AppendLine("  → 温度可用，设置面板不会出现感叹号");
                    break;
                case "no_access":
                    sb.AppendLine("  → 驱动已装，但当前进程不是管理员，CT 会是 --");
                    break;
                case "not_installed":
                    sb.AppendLine("  → 设置面板会显示感叹号，点击进入安装引导");
                    break;
                default:
                    sb.AppendLine("  → 非 AMD 17h~1Ah，这条路用不上；CT 只能靠 ACPI");
                    break;
            }

            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "perfoverlay-pawnio.txt"),
                                  sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
        }

        /// <summary>诊断用：按 WPF 的排版结果量一段文字的真实宽度（DIP）。</summary>
        private static double TextWidthDip(string text, double fontSize, FontWeight weight)
        {
            try
            {
                var tf = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
                                      FontStyles.Normal, weight, FontStretches.Normal);
                var ft = new System.Windows.Media.FormattedText(text ?? "", System.Globalization.CultureInfo.CurrentUICulture,
                                           FlowDirection.LeftToRight, tf, fontSize,
                                           System.Windows.Media.Brushes.Black, 1.0);
                return ft.WidthIncludingTrailingWhitespace;
            }
            catch (Exception)
            {
                return (text == null ? 0 : text.Length) * fontSize * 0.62;
            }
        }

        /// <summary>
        /// 热键自检：写法能不能解析、该拒的有没有拒、系统能不能注册、
        /// 被别人占住时能不能判出冲突（1409）。全程不开窗口。
        /// </summary>
        private void RunHotkeyProbe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== 热键自检 ==");
            int fails = 0;

            string[] good = { "Ctrl+Alt+P", "Ctrl+Shift+F5", "Alt+1", "Ctrl+Alt+Shift+Win+F12" };
            foreach (var s in good)
            {
                uint mods, vk;
                bool ok = HotkeyService.TryParse(s, out mods, out vk) && vk != 0;
                if (!ok) fails++;
                sb.AppendLine("parse  [" + s + "] => " + (ok ? "OK" : "FAIL") + " mods=0x" + mods.ToString("X") + " vk=0x" + vk.ToString("X"));
            }

            // 没修饰键会抢走全局打字，只有修饰键等于没按键，都必须拒
            string[] bad = { "P", "Ctrl", "Ctrl+Alt", "Ctrl+NonsenseKey" };            foreach (var s in bad)
            {
                uint mods, vk;
                bool ok = HotkeyService.TryParse(s, out mods, out vk);
                if (ok) fails++;
                sb.AppendLine("reject [" + s + "] => " + (ok ? "FAIL（误接受）" : "OK"));
            }

            // 数字键必须落到 VK '1'(0x31)：Enum.TryParse 对纯数字串是按数值解析，
            // "1" 会变成 Cancel(0x03)，注册上去就按不动了
            uint nm, nvk;
            bool digitOk = HotkeyService.TryParse("Alt+1", out nm, out nvk) && nvk == 0x31;
            if (!digitOk) fails++;
            sb.AppendLine("digit  [Alt+1] => vk=0x" + nvk.ToString("X") + (digitOk ? " OK" : " FAIL（应为 0x31）"));

            const string testSpec = "Ctrl+Alt+Shift+F9";   // 挑个没人用的，避免被环境干扰

            int code;
            bool avail = HotkeyService.ProbeRegister(testSpec, 0x6001, out code);
            if (!avail) fails++;
            sb.AppendLine("register [" + testSpec + "] => " + (avail ? "OK" : "FAIL code=" + code));

            // 自己先占住，再换个 id 抢同一个组合：系统必须拒绝并给出 1409
            bool hold = HotkeyService.ProbeHold(testSpec, 0x6002, out code);
            if (!hold) fails++;
            sb.AppendLine("hold     [" + testSpec + "] => " + (hold ? "OK" : "FAIL code=" + code));
            if (hold)
            {
                bool steal = HotkeyService.ProbeHold(testSpec, 0x6003, out code);
                bool detected = !steal && code == 1409;
                if (!detected) fails++;
                sb.AppendLine("conflict [" + testSpec + "] => " + (detected ? "OK（1409 已被占用）" : "FAIL steal=" + steal + " code=" + code));
                HotkeyService.ProbeRelease(0x6002);
                HotkeyService.ProbeRelease(0x6003);
            }

            string err;
            bool applied = HotkeyService.Apply(testSpec, out err);
            if (!applied) fails++;
            sb.AppendLine("apply  => " + (applied ? "OK current=" + HotkeyService.Current : "FAIL " + err));

            bool cleared = HotkeyService.Apply("", out err);
            if (!cleared) fails++;
            sb.AppendLine("clear  => " + (cleared ? "OK current=[" + HotkeyService.Current + "]" : "FAIL " + err));

            sb.AppendLine(fails == 0 ? "SUMMARY: ALL PASS" : "SUMMARY: FAIL " + fails);
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "perfoverlay-hotkey.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
            Shutdown(fails == 0 ? 0 : 2);
        }

        /// <summary>
        /// 检查更新自检：不开窗口，把 GitHub 的返回和版本比较写进报告。
        /// 网络不通时这里应该给出可读原因（超时 / 403 / 连不上），而不是异常堆栈。
        /// </summary>
        private void RunUpdateProbe()
        {
            var r = UpdateChecker.Check(8000);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("current   = " + r.Current);
            sb.AppendLine("latest    = " + (r.Latest == "" ? "(none)" : r.Latest));
            sb.AppendLine("url       = " + r.Url);
            sb.AppendLine("failed    = " + r.Failed);
            sb.AppendLine("error     = " + (r.Error == "" ? "(none)" : r.Error));
            sb.AppendLine("hasUpdate = " + r.HasUpdate);
            sb.AppendLine("compare v0.9.3 / 0.9.2  = " + UpdateChecker.Compare("v0.9.3", "0.9.2") + "  (期望 1)");
            sb.AppendLine("compare v0.9   / 0.9.0  = " + UpdateChecker.Compare("v0.9", "0.9.0") + "  (期望 0)");
            sb.AppendLine("compare 0.9.2  / 0.9.10 = " + UpdateChecker.Compare("0.9.2", "0.9.10") + "  (期望 -1)");
            sb.AppendLine("compare 0.9.2  / 0.9.2  = " + UpdateChecker.Compare("0.9.2", "0.9.2") + "  (期望 0)");
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "perfoverlay-update.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
            Shutdown(r.Failed ? 2 : 0);
        }

        /// <summary>
        /// 版本窗口探针：开在屏幕外（不抢焦点、不闪屏），等自动检查跑完后把
        /// 几何实测（按钮间距 / 是否压宽 / 文字是否越界）与状态写文件。
        /// 检查本身最多 8 秒，给 11 秒余量。
        /// </summary>
        private void RunAboutProbe(int holdSeconds)
        {
            var w = new AboutWindow { ShowActivated = false, Opacity = 0 };
            w.MoveOffscreen();          // 先移出屏幕再显示，避免中间闪一帧
            w.Show();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(holdSeconds) };
            timer.Tick += (s, a) =>
            {
                timer.Stop();
                string report;
                try { report = w.GeometryReport(); }
                catch (Exception ex) { report = "GEOMETRY FAILED: " + ex.Message; }

                bool bad = report.IndexOf("OVERLAP", StringComparison.Ordinal) >= 0
                        || report.IndexOf("SQUEEZED", StringComparison.Ordinal) >= 0
                        || report.IndexOf("OVERFLOW", StringComparison.Ordinal) >= 0
                        || report.IndexOf("FAILED", StringComparison.Ordinal) >= 0;
                report += bad ? "SUMMARY: FAIL" : "SUMMARY: ALL PASS";

                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "perfoverlay-about.txt"), report, Encoding.UTF8);
                }
                catch (Exception) { }

                w.Close();
                Shutdown(bad ? 2 : 0);
            };
            timer.Start();
        }

        private static int CountVisuals<T>(DependencyObject root) where T : DependencyObject
        {
            int n = (root is T) ? 1 : 0;
            int c = Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < c; i++) n += CountVisuals<T>(Media.VisualTreeHelper.GetChild(root, i));
            return n;
        }

        private static System.Collections.Generic.List<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
        {
            var list = new System.Collections.Generic.List<T>();
            FillVisuals(root, list);
            return list;
        }

        private static void FillVisuals<T>(DependencyObject root, System.Collections.Generic.List<T> list) where T : DependencyObject
        {
            if (root is T) list.Add((T)(object)root);
            int c = Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < c; i++) FillVisuals<T>(Media.VisualTreeHelper.GetChild(root, i), list);
        }

        private static string TextOf(System.Windows.Controls.Button b)
        {
            var t = b.Content as string;
            return t ?? "?";
        }

        private static bool IsUnder(DependencyObject node, DependencyObject ancestor)
        {
            var cur = node;
            while (cur != null)
            {
                if (ReferenceEquals(cur, ancestor)) return true;
                cur = Media.VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        /// <summary>
        /// 布局体检：字号 S/M/L，五项指标全开，逐个量真实渲染矩形。
        /// 两件事必须靠数值定案，不能靠看截图：
        ///   1) 最后一个分组有没有越过窗口右边界（裁切）
        ///   2) 窗口右边距 / 上边距是否符合 EdgeRight / EdgeTop（贴角）
        /// </summary>
        private void RunMeasure()
        {
            var sb = new System.Text.StringBuilder();
            int fails = 0;

            string[] sizes = { "S", "M", "L" };

            foreach (var size in sizes)
            {
                var s = SettingsStore.Load();
                s.ShowCpu = s.ShowMemory = s.ShowFps = s.ShowLowFps = s.ShowVram = true;
                s.ShowClock = s.ShowGpu = s.ShowTemp = true;   // 全开才量得到最宽的极端情况
                s.SizeMode = size;
                s.OffsetX = s.OffsetY = 0;

                var ov = new OverlayWindow();
                ov.ApplySettings(s, true);
                ov.Opacity = 0;         // 离屏验证：布局照跑，屏幕上不出现
                ov.Show();
                ov.UpdateLayout();

                double winW = ov.ActualWidth;
                double rightmost = 0;
                var lines = new System.Text.StringBuilder();

                foreach (var child in ov.RowsHost.Children)
                {
                    var fe = child as FrameworkElement;
                    if (fe == null) continue;
                    var p = fe.TranslatePoint(new System.Windows.Point(0, 0), ov);
                    double right = p.X + fe.ActualWidth;
                    if (right > rightmost) rightmost = right;
                    lines.AppendLine(string.Format("      x={0,7:0.0}  w={1,6:0.0}  right={2,7:0.0}",
                        p.X, fe.ActualWidth, right));
                }

                // 允许 0.5 DIP 的亚像素误差；再大的差值就是真裁切
                bool clipOk = rightmost <= winW + 0.5;
                bool posOk = Math.Abs(ov.RightMargin - OverlayWindow.EdgeRight) <= 1.0
                          && Math.Abs(ov.TopMargin - OverlayWindow.EdgeTop) <= 1.0;
                if (!clipOk || !posOk) fails++;

                sb.AppendLine("== Size=" + size + " ==");
                sb.AppendLine(string.Format("   FontSize={0}  Actual={1:0.0} x {2:0.0}  groups={3}",
                    s.FontSizeMain, winW, ov.ActualHeight, ov.RowsHost.Children.Count));
                sb.AppendLine(string.Format("   裁切: rightmost={0:0.0}  windowRight={1:0.0}  => {2}",
                    rightmost, winW, clipOk ? "OK" : "CLIPPED"));
                sb.AppendLine(string.Format("   贴角: 右边距={0:0.0} (期望 {1:0.0})  上边距={2:0.0} (期望 {3:0.0})  => {4}",
                    ov.RightMargin, OverlayWindow.EdgeRight,
                    ov.TopMargin, OverlayWindow.EdgeTop, posOk ? "OK" : "OFF"));
                sb.AppendLine(string.Format("   DPI={0:0.00}  工作区={1}", ov.GetDpiScale(), ov.GetWorkAreaText()));
                sb.Append(lines.ToString());
                sb.AppendLine();

                ov.Close();
            }

            sb.AppendLine(fails == 0 ? "SUMMARY: ALL PASS" : ("SUMMARY: FAIL " + fails));
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "perfoverlay-measure.txt"), sb.ToString());
            }
            catch (Exception) { }

            Shutdown(fails == 0 ? 0 : 2);
        }

        /// <summary>
        /// 探测是否已有实例（互斥体是否被占用）。只用于「双击重复启动」的快速返回，
        /// 不参与真正的单实例仲裁——那一步仍然由 StartupCore 里的 Mutex 完成。
        /// </summary>
        private static bool InstanceAlreadyRunning()
        {
            try
            {
                using (var probe = Mutex.OpenExisting("Local\\PerfOverlay.Instance"))
                {
                    return probe != null;
                }
            }
            catch (Exception) { return false; }
        }

        private static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>取 `--name value` 后面那个值；没给值返回 null。</summary>
        private static string ArgValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    string v = args[i + 1];
                    return v.StartsWith("--", StringComparison.Ordinal) ? null : v;
                }
            }
            return null;
        }

        private void OnMetricsUpdated(object sender, MetricsSnapshot snap)
        {
            // 浮窗不可见时连 UI 都不该碰：每秒一次格式化 + 改 Text 会持续制造垃圾，
            // 又没有一帧会被渲染出去。只留最新快照，显示时回填。
            _lastSnap = snap;
            if (_settings == null || !_settings.OverlayVisible) return;

            // UI 线程跟不上时直接丢弃这一帧，避免 BeginInvoke 排队堆积
            if (Interlocked.Exchange(ref _uiPending, 1) == 1) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _uiPending, 0);
                try { _overlay.UpdateMetrics(snap); } catch (Exception) { }
            }), DispatcherPriority.Render);
        }

        // ==================================================================
        // 设置
        // ==================================================================

        private void ApplySettings(AppSettings cfg, bool rebuild)
        {
            if (cfg == null) return;
            // 热键注册有系统级副作用，只有值真的变了才去动它
            string oldHotkey = _settings != null ? (_settings.Hotkey ?? "") : "";
            // OffsetX/OffsetY 是浮窗的拖动/贴角状态，面板 Bind 时的克隆里不会更新它；
            // 不带回的话，第二次开关会用 Bind 时的旧偏移覆盖浮窗当前位置，
            // 既让复位判错档位，又会在存盘时把旧偏移写进 settings.json。
            if (_settings != null)
            {
                cfg.OffsetX = _settings.OffsetX;
                cfg.OffsetY = _settings.OffsetY;
            }
            bool rowsChanged = rebuild
                || cfg.ShowCpu != _settings.ShowCpu || cfg.ShowMemory != _settings.ShowMemory
                || cfg.ShowFps != _settings.ShowFps || cfg.ShowLowFps != _settings.ShowLowFps
                || cfg.ShowVram != _settings.ShowVram
                || cfg.ShowClock != _settings.ShowClock || cfg.ShowGpu != _settings.ShowGpu
                || cfg.ShowTemp != _settings.ShowTemp
                || cfg.SizeMode != _settings.SizeMode
                || cfg.TextColor != _settings.TextColor
                || cfg.ShowOutline != _settings.ShowOutline;

            _settings = cfg;
            _overlay.ApplySettings(cfg, rowsChanged);
            if (_host != null) _host.ApplySettings(cfg);

            if (!string.Equals(oldHotkey, cfg.Hotkey ?? "", StringComparison.OrdinalIgnoreCase))
                ApplyHotkeyFromSettings();

            if (_tray != null) _tray.Visible = cfg.ShowTrayIcon;

            if (_saveTimer != null) { _saveTimer.Stop(); _saveTimer.Start(); }
        }

        private void ShowSettings()
        {
            ShowSettings(false);
        }

        private void ShowSettings(bool silent)
        {
            try
            {
                ShowSettingsCore(silent);
            }
            catch (Exception ex)
            {
                // 面板出问题不能把浮窗一起拖下水
                LogError("ShowSettings", ex);
            }
        }

        private void ShowSettingsCore(bool silent)
        {
            if (_settingsWin == null)
            {
                _settingsWin = new SettingsWindow();
                _settingsWin.SettingsChanged += (s, cfg) => ApplySettings(cfg, true);
                _settingsWin.PositionPicked += (s, mode) =>
                {
                    _overlay.AlignHorizontal(mode);
                    SettingsStore.Save(_settings);
                };
                _settingsWin.ExitRequested += (s, a) => ExitApp();
            }

            _settingsWin.Bind(_settings);
            _settingsWin.SetPositionMode(_overlay.CurrentAlignMode());
            _settingsWin.UpdateFrameStatus(_host);

            // 静默模式（探针）：不激活、直接放到屏幕外，只给 PrintWindow 抓图用
            if (silent)
            {
                _settingsWin.ShowActivated = false;
                _settingsWin.Show();
                _settingsWin.MoveOffscreen();
                return;
            }

            _settingsWin.Show();

            if (_settingsWin.WindowState == WindowState.Minimized)
                _settingsWin.WindowState = WindowState.Normal;

            _settingsWin.Activate();
        }

        // ==================================================================
        // 显示 / 隐藏热键
        // ==================================================================

        /// <summary>
        /// 把配置里的热键注册上。注册失败不拦启动 —— 组合键可能被后来装的软件占了，
        /// 那时候浮窗照常能用，只是这个键要用户自己换一个。
        /// </summary>
        private void InitHotkey()
        {
            HotkeyService.Pressed += ToggleOverlayByHotkey;

            string spec = _settings != null ? _settings.Hotkey : null;
            if (string.IsNullOrEmpty(spec)) return;

            string err;
            if (!HotkeyService.Apply(spec, out err))
                LogInfo("Hotkey", "注册失败（" + spec + "）：" + err);
        }

        private void ToggleOverlayByHotkey()
        {
            SetOverlayVisible(_settings == null || !_settings.OverlayVisible);
        }

        private void ApplyHotkeyFromSettings()
        {
            string spec = _settings != null ? _settings.Hotkey : null;
            string err;
            if (!HotkeyService.Apply(spec, out err))
                LogInfo("Hotkey", "注册失败（" + (spec ?? "") + "）：" + err);
        }

        private void SetOverlayVisible(bool visible)
        {
            _settings.OverlayVisible = visible;
            if (visible)
            {
                if (_hideTrimTimer != null) _hideTrimTimer.Stop();
                _overlay.Show();
                // 刚才藏起来时跳过了所有 UI 更新，这里补上最后一帧，别让用户看到一秒空白
                if (_lastSnap != null)
                {
                    try { _overlay.UpdateMetrics(_lastSnap); } catch (Exception) { }
                }
            }
            else
            {
                _overlay.Hide();
                ScheduleHideTrim(1500);
            }
            if (_tray != null) _miShow.Text = visible ? "隐藏浮窗" : "显示浮窗";
            SettingsStore.Save(_settings);
        }

        // ==================================================================
        // 浮窗隐藏后回收一次
        // ==================================================================

        /// <summary>
        /// 藏起来之后等一会儿再动手：用户常是「点一下看看、马上又点回来」，
        /// 立刻回收反而要重新换页。20 秒内的重复隐藏只做一次。
        /// delayMs 给启动路径留出余量 —— 开机时 WPF 与采集引擎还在爬起来，
        /// 过早回收等于把马上就要用的页面换出去。
        /// </summary>
        private void ScheduleHideTrim(int delayMs)
        {
            if (_hideTrimTimer == null)
            {
                _hideTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
                _hideTrimTimer.Tick += (s, a) => { _hideTrimTimer.Stop(); RunHideTrim(); };
            }
            _hideTrimTimer.Stop();
            _hideTrimTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            _hideTrimTimer.Start();
        }

        /// <summary>
        /// 先 GC 再修剪工作集，顺序不能反：不先回收就修剪，等于把还活着的对象一起换出去，
        /// 下一帧又全读回来。两件事都只在浮窗不可见时做 —— 可见时每秒重绘，
        /// 修剪会让系统反复换页，越修越卡。
        /// 实测：WS 90 → 9 MB，Private 只掉那部分真垃圾（81 → 76），不虚报效果。
        /// </summary>
        private void RunHideTrim()
        {
            if (_settings == null || _settings.OverlayVisible) return;       // 期间又点回来了
            if ((DateTime.UtcNow - _lastTrimUtc).TotalSeconds < 20) return;  // 反复开关只做一次
            _lastTrimUtc = DateTime.UtcNow;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // 阻塞式 Gen2，压缩关掉：LOH 压缩在这点垃圾量上不划算，还慢
                    GC.Collect(2, GCCollectionMode.Forced, true, false);
                    GC.WaitForPendingFinalizers();
                }
                catch (Exception) { }
                MemProbe.TrimWorkingSet();
                LogInfo("Trim", "浮窗已隐藏，完成一次回收 + 工作集修剪");
            });
        }

        // ==================================================================
        // 托盘
        // ==================================================================

        private void InitTray()
        {
            _trayIcon = LoadIcon(out _trayIconOwned);
            _tray = new Forms.NotifyIcon
            {
                // 托盘提示里也带版本号：装了两份 exe 时靠这个分清在跑哪一个
                Text = "PerfOverlay v" + UpdateChecker.CurrentVersion,
                Icon = _trayIcon,
                Visible = _settings.ShowTrayIcon
            };

            var menu = new Forms.ContextMenuStrip
            {
                ShowImageMargin = false,
                // 关掉勾选栏：勾会占掉左侧一块，文字再怎么设居中也会整体偏右。
                // 当前状态改用「动作文案」表达（隐藏浮窗 / 取消开机启动），一眼就能看出点下去会怎样。
                ShowCheckMargin = false,
                Font = new Font("Microsoft YaHei UI", 9f),
                Renderer = new LightMenuRenderer(),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(0x18, 0x2A, 0x40),
                Padding = new Forms.Padding(4),
                DropShadowEnabled = true,
                AutoSize = true
            };

            _miShow = new Forms.ToolStripMenuItem(_settings.OverlayVisible ? "隐藏浮窗" : "显示浮窗");
            _miShow.Click += (s, e) => SetOverlayVisible(!_settings.OverlayVisible);

            var miSettings = new Forms.ToolStripMenuItem("控制面板");
            miSettings.Click += (s, e) => ShowSettings();

            _miStartup = new Forms.ToolStripMenuItem(StartupHelper.IsEnabled ? "取消开机启动" : "开机启动");
            _miStartup.Click += (s, e) =>
            {
                bool want = !StartupHelper.IsEnabled;
                if (StartupHelper.Apply(want))
                {
                    _miStartup.Text = want ? "取消开机启动" : "开机启动";
                    _settings.StartWithWindows = want;
                    SettingsStore.Save(_settings);
                    if (_settingsWin != null) _settingsWin.Bind(_settings);
                }
            };

            var miExit = new Forms.ToolStripMenuItem("退出");
            miExit.Click += (s, e) => ExitApp();

            menu.Items.Add(_miShow);
            menu.Items.Add(miSettings);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_miStartup);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(miExit);

            // 等分 + 居中：所有项同宽（按最长文案算），文字水平垂直都居中。
            // WinForms 的 ToolStripMenuItem 默认 AutoSize，宽度随文案变，"退出"只有两个字，
            // 整张菜单就会左窄右宽，看着很散。
            var items = new System.Collections.Generic.List<Forms.ToolStripMenuItem>();
            foreach (Forms.ToolStripItem it in menu.Items)
            {
                var mi = it as Forms.ToolStripMenuItem;
                if (mi != null) items.Add(mi);
                else it.Margin = new Forms.Padding(0, 3, 0, 3);
            }

            int textW = 0;
            // 文案会随状态变长，按最长可能文案定宽，避免切换时菜单宽度跳动
            foreach (string t in new[] { "隐藏浮窗", "显示浮窗", "控制面板", "取消开机启动", "开机启动", "退出" })
                textW = Math.Max(textW, Forms.TextRenderer.MeasureText(t, menu.Font).Width);
            int itemW = textW + 28;
            int itemH = Forms.TextRenderer.MeasureText("国", menu.Font).Height + 12;

            foreach (var mi in items)
            {
                // 宽度/高度固定 = 等分；文字居中由 LightMenuRenderer 自绘（TextAlign 对菜单项无效）
                mi.AutoSize = false;
                mi.Width = itemW;
                mi.Height = itemH;
            }

            menu.Opening += (s, e) =>
            {
                _miShow.Text = _settings.OverlayVisible ? "隐藏浮窗" : "显示浮窗";
                _miStartup.Text = StartupHelper.IsEnabled ? "取消开机启动" : "开机启动";
            };
            // 圆角靠 Region 裁：WinForms 没有 CornerRadius，只能裁形状 + 自绘 1px 浅蓝边
            menu.Opened += (s, e) => MenuSkin.ApplyRoundedRegion(menu);

            _tray.ContextMenuStrip = menu;
            _trayMenu = menu;
            _tray.DoubleClick += (s, e) => ShowSettings();
        }

        /// <summary>
        /// 托盘图标直接用 exe 自身内嵌的图标，不额外打包 .ico 文件。
        /// owned=true 表示这个 Icon 是本次新建的、用完必须 Dispose；
        /// 回落到 SystemIcons.Application 时那是共享静态实例，绝不能再释放。
        /// </summary>
        private static Icon LoadIcon(out bool owned)
        {
            owned = false;
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) { owned = true; return ico; }
            }
            catch (Exception) { }
            return SystemIcons.Application;
        }

        // ==================================================================

        private void ExitApp()
        {
            try { if (_saveTimer != null) _saveTimer.Stop(); } catch (Exception) { }
            SettingsStore.Save(_settings);

            // 释放顺序：先摘托盘（否则图标会在任务栏残留一个「幽灵」），
            // 再释放菜单与图标句柄，最后停采集线程。
            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.ContextMenuStrip = null;
                    _tray.Dispose();
                    _tray = null;
                }
            }
            catch (Exception) { }
            try { if (_trayMenu != null) { _trayMenu.Dispose(); _trayMenu = null; } } catch (Exception) { }
            try { if (_trayIconOwned && _trayIcon != null) _trayIcon.Dispose(); _trayIcon = null; } catch (Exception) { }

            try { if (_host != null) { _host.Dispose(); _host = null; } } catch (Exception) { }
            try { if (_settingsWin != null) _settingsWin.CloseForShutdown(); } catch (Exception) { }
            try { if (_overlay != null) _overlay.Close(); } catch (Exception) { }

            Shutdown(0);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 正常退出不写日志：一个开机自启的常驻程序没必要在 TEMP 里留痕
            try { HotkeyService.Dispose(); } catch (Exception) { }
            try { if (_single != null) { _single.ReleaseMutex(); _single.Dispose(); _single = null; } } catch (Exception) { }
            base.OnExit(e);
        }
    }

    /// <summary>托盘菜单配色，跟主界面统一的白蓝。</summary>
    internal sealed class LightMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Color BorderColor = Color.FromArgb(0xE2, 0xEC, 0xFA);
        private static readonly Color TextColor = Color.FromArgb(0x18, 0x2A, 0x40);
        private static readonly Color TextSelected = Color.FromArgb(0x2C, 0x6B, 0xF0);
        private static readonly Color HoverColor = Color.FromArgb(0xE9, 0xF0, 0xFE);
        private static readonly Color CheckColor = Color.FromArgb(0x2C, 0x6B, 0xF0);

        public LightMenuRenderer() : base(new LightColorTable()) { RoundedEdges = false; }

        // 默认会铺一层灰渐变，还带 3D 边框 —— 这就是"系统味"的来源，全部改成手绘
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            int radius = Math.Max(4, (int)Math.Round(8.0 * e.ToolStrip.DeviceDpi / 96.0));
            using (var path = MenuSkin.RoundedPath(r, radius))
            {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var p = new Pen(BorderColor)) g.DrawPath(p, path);
            }
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            // 边框已经画在背景里，这里必须清空，否则又叠一层系统 3D 边
        }

        // 左侧那条浅灰竖条 / 图标栏不要
        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using (var path = MenuSkin.RoundedPath(r, Math.Max(3, r.Height / 3)))
            {
                using (var b = new SolidBrush(HoverColor)) g.FillPath(b, path);
            }
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            var color = !e.Item.Enabled
                ? Color.FromArgb(0x9A, 0xAE, 0xC6)
                : (e.Item.Selected ? TextSelected : TextColor);

            // ToolStripMenuItem 的 TextAlign 是不生效的 —— 它的专用布局
            // (ToolStripMenuItemInternalLayout) 把文字矩形写死在左侧，为了对齐勾选列。
            // 所以这里整段自绘。两个踩过的坑：
            //   1) e.Graphics 已经平移到**该 item 的局部坐标系**（ContentRectangle/TextRectangle
            //      的 Y 都是 1 而不是 ToolStrip 绝对坐标），所以按 (0,0,Item.Height) 垂直居中，
            //      不能再叠加 Bounds.Top，否则文字会画到项外面去。
            //   2) 菜单窗口的 ClientSize 比 item.Width 窄（SetRegion 裁圆角之后更明显），
            //      横向若按 item.Width 居中，整行文字会右偏约 6px；基准要取菜单可视宽度。
            // 只画一次 —— 画两遍会让抗锯齿叠加、字重变粗，看着像加粗了。
            var r = new Rectangle(0, 0, e.ToolStrip.ClientSize.Width, e.Item.Height);
            Forms.TextRenderer.DrawText(
                e.Graphics, e.Text ?? "", e.Item.Font, r, color,
                Forms.TextFormatFlags.HorizontalCenter
                | Forms.TextFormatFlags.VerticalCenter
                | Forms.TextFormatFlags.SingleLine
                | Forms.TextFormatFlags.NoPadding);
        }

        // 系统勾选图标是蓝白 3D 方块，换成一笔细勾
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float cx = r.Left + r.Width / 2f;
            float cy = r.Top + r.Height / 2f;
            float k = r.Width / 16f;
            using (var pen = new Pen(CheckColor, 1.8f))
            {
                pen.StartCap = Drawing2D.LineCap.Round;
                pen.EndCap = Drawing2D.LineCap.Round;
                g.DrawLines(pen, new[]
                {
                    new PointF(cx - 4f * k, cy + 0.2f * k),
                    new PointF(cx - 1f * k, cy + 3.4f * k),
                    new PointF(cx + 4.4f * k, cy - 3.6f * k)
                });
            }
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            int y = e.Item.Height / 2;
            using (var p = new Pen(Color.FromArgb(0xED, 0xF2, 0xFA)))
                g.DrawLine(p, 10, y, e.Item.Width - 10, y);
        }
    }

    /// <summary>WinForms 没有圆角 API，只能自己造路径再裁 Region。</summary>
    internal static class MenuSkin
    {
        public static Drawing2D.GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            var path = new Drawing2D.GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return path;
            int d = Math.Max(1, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void ApplyRoundedRegion(Forms.ToolStripDropDown d)
        {
            if (d == null || d.Width <= 0 || d.Height <= 0) return;
            int radius = Math.Max(4, (int)Math.Round(8.0 * d.DeviceDpi / 96.0));
            var old = d.Region;
            using (var path = RoundedPath(new Rectangle(0, 0, d.Width, d.Height), radius))
                d.Region = new Region(path);
            if (old != null) old.Dispose();
        }
    }

    internal sealed class LightColorTable : Forms.ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Color.FromArgb(0xE9, 0xF0, 0xFE); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(0xE9, 0xF0, 0xFE); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(0xE9, 0xF0, 0xFE); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(0xD6, 0xE4, 0xF8); } }
        public override Color ToolStripDropDownBackground { get { return Color.White; } }
        public override Color ImageMarginGradientBegin { get { return Color.White; } }
        public override Color ImageMarginGradientMiddle { get { return Color.White; } }
        public override Color ImageMarginGradientEnd { get { return Color.White; } }
        public override Color SeparatorDark { get { return Color.FromArgb(0xEC, 0xF3, 0xFD); } }
        public override Color SeparatorLight { get { return Color.White; } }
    }
}
