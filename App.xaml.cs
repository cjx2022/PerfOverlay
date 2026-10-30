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
        private int _uiPending;

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

            // ---- 自动提权 ----
            // 帧采集（FPS / 1% Low）要靠 PresentMon 开 ETW 会话，必须管理员权限。
            // 双击 exe 时如果当前不是管理员，就地拉起一个提权实例再退出自己，
            // 用户不需要记任何启动脚本。这台机器 ConsentPromptBehaviorAdmin=0，
            // 提权是静默的，不会弹 UAC 框。
            //
            // --elevated-attempt 是防循环的保险丝：万一提权成功但进程仍非管理员，
            // 也只允许重试一次。提权失败（被拒绝/策略禁止）时继续以普通权限运行，
            // CPU / 内存 / 显存照常工作，只有 FPS 显示 N/A。
            bool uiProbe = HasArg(e.Args, "--open-settings") || HasArg(e.Args, "--open-menu");
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

                string elevArgs = "--startup --takeover --elevated-attempt" + (_diagnostic ? " --diag" : "");
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

            if (_settings.OverlayVisible) _overlay.Show();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += (s, a) => { _saveTimer.Stop(); SettingsStore.Save(_settings); };

            InitTray();
            _host.Start(_settings);

            // ---- UI 探针：仅供截图核对，正常启动不走这里（静默：窗口放屏幕外，不抢焦点）----
            if (HasArg(e.Args, "--open-settings")) ShowSettings(true);
            if (HasArg(e.Args, "--open-menu")) _overlay.OpenMenuAt(320, 160);

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

            // 按钮文字必须放得下：实际宽度 ≥ 内容期望宽度（含左右内边距）
            var btn = w.ResetButton;
            btn.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double btnNeed = btn.DesiredSize.Width;
            bool btnFits = btn.ActualWidth + 0.5 >= btnNeed;
            if (!btnFits) fails++;

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
            sb.AppendLine(string.Format("   复位按钮  = 实测 {0:0.0} / 需要 {1:0.0} DIP => {2}",
                btn.ActualWidth, btnNeed, btnFits ? "OK（文字放得下）" : "FAIL（被压缩）"));
            sb.AppendLine(string.Format("   全部按钮  = {0} 个，压缩 {1} / 越界 {2} => {3}",
                btnChecked, squeezed, overflow, btnsOk ? "OK" : "FAIL"));
            if (!btnsOk && badBtns.Length > 0) sb.AppendLine("     明细：" + badBtns.ToString().Trim());
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
                s.SizeMode = size;
                s.OffsetX = s.OffsetY = 0;

                var ov = new OverlayWindow();
                ov.ApplySettings(s, true);
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
            bool rowsChanged = rebuild
                || cfg.ShowCpu != _settings.ShowCpu || cfg.ShowMemory != _settings.ShowMemory
                || cfg.ShowFps != _settings.ShowFps || cfg.ShowLowFps != _settings.ShowLowFps
                || cfg.ShowVram != _settings.ShowVram
                || cfg.SizeMode != _settings.SizeMode
                || cfg.TextColor != _settings.TextColor
                || cfg.ShowOutline != _settings.ShowOutline;

            _settings = cfg;
            _overlay.ApplySettings(cfg, rowsChanged);
            if (_host != null) _host.ApplySettings(cfg);

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
                _settingsWin.ResetPositionRequested += (s, a) =>
                {
                    _settings.OffsetX = 0;
                    _settings.OffsetY = 0;
                    _overlay.ApplySettings(_settings, false);
                    SettingsStore.Save(_settings);
                };
                _settingsWin.ExitRequested += (s, a) => ExitApp();
            }

            _settingsWin.Bind(_settings);
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

        private void SetOverlayVisible(bool visible)
        {
            _settings.OverlayVisible = visible;
            if (visible) _overlay.Show(); else _overlay.Hide();
            if (_tray != null) _miShow.Text = visible ? "隐藏浮窗" : "显示浮窗";
            SettingsStore.Save(_settings);
        }

        // ==================================================================
        // 托盘
        // ==================================================================

        private void InitTray()
        {
            _trayIcon = LoadIcon(out _trayIconOwned);
            _tray = new Forms.NotifyIcon
            {
                Text = "性能浮窗",
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
