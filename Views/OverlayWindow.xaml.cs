using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PerfOverlay.Controls;
using PerfOverlay.Services;

namespace PerfOverlay.Views
{
    /// <summary>
    /// 裸文字叠加层：一行白字直接压在画面上（FPS 60  LOW 48  CPU 23% …），
    /// 白字带黑色描边，保证在亮背景上也能看清。可在设置里切成淡卡片底。
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private AppSettings _settings = new AppSettings();
        private readonly Dictionary<string, GroupUi> _groups = new Dictionary<string, GroupUi>();

        private bool _dragging;
        private Point _lastScreenPoint;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        // 贴角边距：复位后要紧贴右上角。这里基于「工作区」计算，所以顶部停靠的
        // 任务栏/Dock 已经被工作区让开，不需要再额外加几十像素的固定偏移
        // （以前 EdgeTop=52 是给顶部任务栏留的，在这台机器上等于白留一条 52 DIP 的缝）。
        // EdgeTop 再收到 2：8 的时候肉眼还看得出"没贴上去"（1.25 DPI 下 8 DIP = 10 像素），
        // 2 DIP 在物理屏上只有 2~3 像素，等于贴边但仍留出描边不被屏幕边缘压住的余地。
        public const double EdgeRight = 8;
        public const double EdgeTop = 2;

        /// <summary>
        /// 诊断用：把右键菜单在指定屏幕坐标展开，用于核对菜单皮肤的真实渲染。
        /// 正常运行时不会调用。
        /// </summary>
        public void OpenMenuAt(double x, double y)
        {
            CtxMenu.PlacementTarget = this;
            CtxMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
            CtxMenu.HorizontalOffset = x;
            CtxMenu.VerticalOffset = y;
            CtxMenu.IsOpen = true;
        }

        /// <summary>
        /// 诊断用：右键菜单本体（主题体检要遍历它的文字颜色与底色）。
        /// 正常运行时不会碰。
        /// </summary>
        internal System.Windows.Controls.ContextMenu CtxMenuForProbe { get { return CtxMenu; } }

        public event EventHandler SettingsRequested;
        public event EventHandler HideRequested;
        public event EventHandler ExitRequested;
        public event EventHandler<AppSettings> PositionChanged;

        public OverlayWindow()
        {
            InitializeComponent();

            MouseLeftButtonDown += OnCardMouseDown;
            MouseMove += OnCardMouseMove;
            MouseLeftButtonUp += OnCardMouseUp;
            MouseRightButtonUp += OnCardRightClick;

            // 宽度在 BuildGroups 里按实测文字算定，这里只需定位
            Loaded += (s, e) => { UpdateLayout(); Reposition(); };
            SizeChanged += (s, e) => Reposition();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyWindowStyle();
        }

        private void ApplyWindowStyle()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int ex = NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TOOLWINDOW;
            if (_settings.ClickThrough) ex |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            else ex &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
            NativeMethods.SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        }

        // ==================================================================
        // 配置
        // ==================================================================

        public void ApplySettings(AppSettings s, bool rebuildRows)
        {
            // 档位必须在换配置之前读：OffsetX 按「当前宽度 + 当前偏移」编码，
            // 读的是浮窗此刻的真实位置。放在 _settings = s 之后读，
            // 一旦 s 里的偏移是旧值（面板克隆滞后），档位就会判错。
            // -2 = 启动首次应用（窗口还没加载），不做复位，照常还原保存的位置。
            int mode = -2;
            if (rebuildRows && IsLoaded) mode = CurrentAlignMode();

            _settings = s;
            Opacity = Math.Max(0.3, Math.Min(1.0, s.Opacity));

            if (rebuildRows)
            {
                BuildGroups();

                // 开关显示项 = 行重建 = 宽度变。SizeToContent 固定左上角往右长，
                // 贴角的浮窗会被顶出屏幕外。语义上开关等价于「重新点一次当前
                // 位置按钮」：拖过的自定义位置不保留（档位 -1 时回默认右上）。
                UpdateLayout();
                if (mode > -2) AlignHorizontal(mode < 0 ? 2 : mode);
            }

            ApplyWindowStyle();
            Reposition();
        }

        private void BuildGroups()
        {
            RowsHost.Children.Clear();
            _groups.Clear();

            double fs = _settings.FontSizeMain;
            // 标签与数值的间隙：0.34 偏松（"FPS   60" 中间能塞进两个字），收到 0.22
            double labelGap = Math.Round(fs * 0.22);
            // 组间距：早先设 0，靠数值控件的定宽余量当间隙，5 项时看着够用；
            // 加到 8 项后时间（"21:45" 几乎填满自己的格子）与 FPS 会贴在一起读成一串。
            // 改成显式 0.28 字高（M 号 = 5 DIP）：给出稳定的呼吸感，又不至于把整行拉长。
            double groupGap = Math.Round(fs * 0.28);

            // 颜色与描边：描边关掉时 strokeW=0，宽度和绘制都不再预留外扩空间
            Color textColor = ParseColor(_settings.TextColor);
            Brush valueBrush = new SolidColorBrush(textColor);
            Brush labelBrush = new SolidColorBrush(Color.FromArgb(0xE6, textColor.R, textColor.G, textColor.B));
            Brush outlineBrush = _settings.ShowOutline ? ContrastOutline(textColor) : null;
            double strokeW = _settings.ShowOutline ? StrokeWidth(fs) : 0;

            // 值宽取「最宽可能值」的实测宽度：100% / 240 之类都不会顶破
            double valueW = Math.Ceiling(Math.Max(
                Math.Max(TextWidth("100%", fs, FontWeights.SemiBold), TextWidth("240", fs, FontWeights.SemiBold)),
                fs * 2.4)) + strokeW;

            // 每一项自带「数值宽度模板」：按该项上限定宽，既防抖动，又不会让
            // "144" 在按 100% 定的格子里右端空出一大截、把组间空隙撑得忽大忽小。
            // 时间不在这张表里：它固定排在最右、不带标签，见下面的 AddClockGroup。
            var items = new List<string[]>();
            if (_settings.ShowFps) items.Add(new[] { "fps", "FPS", "888" });
            // 标签文案：low(1%) 比裸 LOW 更说清口径
            if (_settings.ShowLowFps) items.Add(new[] { "low", "low(1%)", "888" });
            // LAT = 显示延迟（毫秒）。默认关闭，勾了才占位置。
            if (_settings.ShowDisplayLatency) items.Add(new[] { "lat", "LAT", "888" });
            if (_settings.ShowCpu) items.Add(new[] { "cpu", "CPU", "100%" });
            if (_settings.ShowMemory) items.Add(new[] { "mem", "MEM", "100%" });
            if (_settings.ShowVram) items.Add(new[] { "vram", "VRAM", "100%" });
            if (_settings.ShowGpu) items.Add(new[] { "gpu", "GPU", "100%" });
            // CT = CPU 温度，GT = GPU 温度：两个字足够表意，又不占宽度
            if (_settings.ShowCpuTemp) items.Add(new[] { "ct", "CT", "100°" });
            if (_settings.ShowGpuTemp) items.Add(new[] { "gt", "GT", "100°" });

            bool clockShown = _settings.ShowClock;
            for (int i = 0; i < items.Count; i++)
            {
                // 后面还跟着时间块时，最后一项也要留组间距，否则竖条会贴到数值上
                bool isLast = i == items.Count - 1 && !clockShown;
                double labelW = Math.Ceiling(TextWidth(items[i][1], fs, FontWeights.Normal)) + strokeW;
                double cellW = Math.Ceiling(TextWidth(items[i][2], fs, FontWeights.SemiBold)) + strokeW;
                if (cellW < valueW * 0.8) cellW = valueW * 0.8;   // 太窄的格子读起来像漏字
                AddGroup(items[i][0], items[i][1], fs, labelW, cellW,
                         labelGap, isLast ? 0 : groupGap, strokeW,
                         labelBrush, valueBrush, outlineBrush);
            }

            if (clockShown)
            {
                double clockW = Math.Ceiling(TextWidth("88:88", fs, FontWeights.SemiBold)) + strokeW;
                AddClockGroup(fs, clockW, groupGap, strokeW, valueBrush, outlineBrush, labelBrush);
            }
        }

        /// <summary>
        /// 时间块：固定最右、不带标签，靠前面一条小竖条和性能指标隔开。
        /// 竖条用直角 Border 而不是画一个 "|" 字符 —— 字符会被描边一起加粗，
        /// 宽窄还受字体影响；1.5 DIP 的矩形在三档字号下都是同一根细线。
        /// </summary>
        private void AddClockGroup(double fs, double cellW, double gapBefore, double strokeW,
                                   Brush valueBrush, Brush outlineBrush, Brush barBrush)
        {
            var root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 竖条要有框线但要细：条芯 1 DIP，边框取文字描边的一半
            // （描边是跨字形两侧各半画的，条上整份描边会显得比文字粗一圈）；
            // 描边关掉就回到裸条。
            double barStroke = strokeW > 0 ? Math.Max(1.0, strokeW * 0.5) : 0;
            var bar = new Border
            {
                Width = 1.0 + barStroke * 2,
                Height = Math.Round(fs * 0.82),
                Background = barBrush,
                BorderBrush = outlineBrush,
                BorderThickness = barStroke > 0 ? new Thickness(barStroke) : new Thickness(0),
                Opacity = 0.85,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(gapBefore, 0, Math.Round(fs * 0.42), 0)
            };

            var val = new OutlineText
            {
                Text = "N/A",
                FontSize = fs,
                FontWeight = FontWeights.SemiBold,
                Fill = valueBrush,
                Outline = outlineBrush,
                OutlineWidth = strokeW,
                TextAlignment = TextAlignment.Left,
                Width = cellW,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2),
                IsHitTestVisible = false
            };

            root.Children.Add(bar);
            root.Children.Add(val);
            RowsHost.Children.Add(root);

            _groups["time"] = new GroupUi { Root = root, Label = null, Value = val };
        }

        private static double StrokeWidth(double fs)
        {
            return Math.Max(2.0, fs * 0.11);
        }

        /// <summary>解析 "#RRGGBB"；失败回落纯白，不让一个坏配置把文字变透明。</summary>
        private static Color ParseColor(string hex)
        {
            try
            {
                if (!string.IsNullOrEmpty(hex))
                {
                    string h = hex.TrimStart('#');
                    if (h.Length == 6)
                    {
                        return Color.FromRgb(
                            Convert.ToByte(h.Substring(0, 2), 16),
                            Convert.ToByte(h.Substring(2, 2), 16),
                            Convert.ToByte(h.Substring(4, 2), 16));
                    }
                }
            }
            catch (Exception) { }
            return Colors.White;
        }

        /// <summary>
        /// 描边色按文字颜色自动取反：亮字配黑边，暗字配白边。
        /// 这样只需暴露「描边开/关」，不必再让用户选描边颜色。
        /// </summary>
        private static Brush ContrastOutline(Color c)
        {
            double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            return lum > 0.5 ? Brushes.Black : Brushes.White;
        }

        /// <summary>用 FormattedText 精确量文字宽度（DIP），与布局系统无关。</summary>
        private static double TextWidth(string text, double fontSize, FontWeight weight)
        {
            try
            {
                var tf = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"),
                                      FontStyles.Normal, weight, FontStretches.Normal);
                var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                                           FlowDirection.LeftToRight, tf, fontSize, Brushes.Black, 1.0);
                return ft.WidthIncludingTrailingWhitespace;
            }
            catch (Exception)
            {
                return text.Length * fontSize * 0.62;
            }
        }

        private void AddGroup(string key, string label, double fs,
                              double labelW, double valueW, double labelGap, double groupGap,
                              double strokeW, Brush labelBrush, Brush valueBrush, Brush outlineBrush)
        {
            var root = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, groupGap, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var lbl = new OutlineText
            {
                Text = label,
                FontSize = fs,
                Fill = labelBrush,
                Outline = outlineBrush,
                OutlineWidth = strokeW,
                TextAlignment = TextAlignment.Left,
                Width = labelW,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, labelGap, 2),
                IsHitTestVisible = false
            };

            var val = new OutlineText
            {
                Text = "N/A",
                FontSize = fs,
                FontWeight = FontWeights.SemiBold,
                Fill = valueBrush,
                Outline = outlineBrush,
                OutlineWidth = strokeW,
                // 数值改成左对齐：右对齐时数值被推到 valueW 的右端，
                // 「60」这种两位数前面会空出一大截，看着像 FPS 和 60 之间有大缝。
                // valueW 仍然固定，所以组与组的列位置照样不抖，只是数值紧贴自己的标签。
                TextAlignment = TextAlignment.Left,
                Width = valueW,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2),
                IsHitTestVisible = false
            };

            root.Children.Add(lbl);
            root.Children.Add(val);
            RowsHost.Children.Add(root);

            _groups[key] = new GroupUi { Root = root, Label = lbl, Value = val };
        }

        // ==================================================================
        // 刷新
        // ==================================================================

        public void UpdateMetrics(MetricsSnapshot s)
        {
            if (!_settings.AnyMetricVisible()) return;

            if (_settings.ShowCpu)
                Set("cpu", s.CpuOk ? FormatPercent(s.CpuPercent) : null,
                    s.CpuOk ? null : "CPU 读取失败");

            if (_settings.ShowMemory)
            {
                bool ok = s.MemoryTotal > 0;
                string tip = ok
                    ? string.Format("{0:0.0} GB / {1:0.0} GB", s.MemoryUsed / 1073741824.0, s.MemoryTotal / 1073741824.0)
                    : "内存读取失败";
                Set("mem", ok ? FormatPercent(s.MemoryPercent) : null, tip);
            }

            if (_settings.ShowFps)
            {
                if (s.FpsOk)
                    Set("fps", ((int)Math.Round(s.Fps)).ToString(),
                        s.TargetProcess.Length > 0 ? ("前台：" + s.TargetProcess) : null);
                else
                    Set("fps", null, s.FpsMessage);
            }

            if (_settings.ShowLowFps)
                Set("low", s.FpsOk ? ((int)Math.Round(s.LowFps)).ToString() : null,
                    s.FpsOk ? "最近 10 秒最差 1% 帧" : s.FpsMessage);

            if (_settings.ShowDisplayLatency)
            {
                // 值取中位数、不带单位（跟 FPS 一样裸数字），毫秒数写进悬停提示。
                // 拿不到就显示 -- 而不是 0：0 会被读成「零延迟」。
                Set("lat", s.LatencyOk ? FormatLatency(s.LatencyMs) : null,
                    s.LatencyDetail, "--");
            }

            if (_settings.ShowVram)
            {
                if (s.VramOk)
                    Set("vram", FormatPercent(s.VramPercent),
                        string.Format("已用 {0:0.0} GB / 共 {1:0.0} GB",
                            s.VramUsed / 1073741824.0, s.VramBudget / 1073741824.0));
                else
                    Set("vram", null, "显存不可用");
            }

            if (_settings.ShowClock)
                Set("time", string.IsNullOrEmpty(s.ClockText) ? null : s.ClockText, null);

            if (_settings.ShowGpu)
                Set("gpu", s.GpuOk ? FormatPercent(s.GpuPercent) : null,
                    s.GpuOk ? s.GpuDetail
                            : (string.IsNullOrEmpty(s.GpuDetail) ? "GPU 利用率不可用" : s.GpuDetail));

            if (_settings.ShowCpuTemp)
            {
                // CT = CPU 温度；读不到就把原因挂在 tooltip 上，别只留一个哑的 N/A
                Set("ct", s.TempCpuOk ? FormatTemp(s.TempCpuC) : null,
                    s.TempCpuOk ? s.TempCpuDetail
                                : (string.IsNullOrEmpty(s.TempCpuDetail) ? "CPU 温度不可用" : s.TempCpuDetail),
                    "--");
            }

            if (_settings.ShowGpuTemp)
            {
                Set("gt", s.TempGpuOk ? FormatTemp(s.TempGpuC) : null,
                    s.TempGpuOk ? s.TempGpuDetail
                                : (string.IsNullOrEmpty(s.TempGpuDetail) ? "GPU 温度不可用" : s.TempGpuDetail),
                    "--");
            }
        }

        private static string FormatTemp(double celsius)
        {
            return ((int)Math.Round(celsius)).ToString() + "°";
        }

        /// <summary>
        /// 显示延迟：10ms 以下保留一位小数。这个区间里 8.4 与 9.1 的差别是真的
        /// （差一帧 vs 差半帧），取整会把它们显示成同一个数；10ms 以上一位小数没有意义。
        /// </summary>
        private static string FormatLatency(double ms)
        {
            return ms < 10 ? ms.ToString("0.0") : ((int)Math.Round(ms)).ToString();
        }

        private static string FormatPercent(double v)
        {
            return ((int)Math.Round(v)).ToString() + "%";
        }

        private void Set(string key, string text, string tooltip)
        {
            Set(key, text, tooltip, "N/A");
        }

        /// <summary>
        /// placeholder 是数值缺失时的占位。温度用 "--"：安装引导的文案里承诺的就是
        /// 「不装也不影响其它指标，CT 显示 --」，界面不该写成 N/A 跟文案打架。
        /// </summary>
        private void Set(string key, string text, string tooltip, string placeholder)
        {
            GroupUi g;
            if (!_groups.TryGetValue(key, out g)) return;

            // 三项都先判等再赋值。Opacity / ToolTip 原来是无条件写，
            // 每 0.5 秒就把所有依赖属性刷一遍 —— 值没变却白白触发属性变更通知。
            string show = string.IsNullOrEmpty(text) ? placeholder : text;
            bool changed = g.Value.Text != show;
            if (changed) g.Value.Text = show;

            double op = string.IsNullOrEmpty(text) ? 0.62 : 1.0;
            if (Math.Abs(g.Value.Opacity - op) > 0.001) g.Value.Opacity = op;

            // 显示值没变就不用管 tooltip（它同源，晚一拍更新无感）
            if (!changed) return;

            var curTip = g.Value.ToolTip as string;
            bool tipEmpty = string.IsNullOrEmpty(tooltip);
            if (tipEmpty ? curTip == null : string.Equals(curTip, tooltip, StringComparison.Ordinal)) return;
            g.Value.ToolTip = tipEmpty ? null : tooltip;
        }

        // ==================================================================
        // 定位：贴主屏右上角
        // ==================================================================

        public void Reposition()
        {
            // SizeToContent 下宽度由内容决定，必须用布局完成后的 ActualWidth；
            // 布局还没跑完时依次退回 Width / DesiredSize，保证 Show 之前也有个合理位置。
            double w = ActualWidth > 0 ? ActualWidth : Width;
            if (double.IsNaN(w) || w <= 0) w = DesiredSize.Width;
            if (double.IsNaN(w) || w <= 0) w = 200;

            double workLeft, workTop, workRight, workBottom;
            GetWorkAreaDip(out workLeft, out workTop, out workRight, out workBottom);

            double left = workRight - w - EdgeRight + _settings.OffsetX;
            double top = workTop + EdgeTop + _settings.OffsetY;

            if (left < workLeft) left = workLeft;
            if (left > workRight - 40) left = workRight - 40;
            if (top < workTop) top = workTop;
            if (top > workBottom - 24) top = workBottom - 24;

            Left = left;
            Top = top;
        }

        /// <summary>
        /// 把浮窗横向贴到 0=左上 / 1=中上 / 2=右上。纵向一律回到顶部。
        /// 位置仍以 OffsetX 表达，「拖动」那条路径不用改。
        /// </summary>
        public void AlignHorizontal(int mode)
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            if (double.IsNaN(w) || w <= 0) w = DesiredSize.Width;
            if (double.IsNaN(w) || w <= 0) w = 200;

            double workLeft, workTop, workRight, workBottom;
            GetWorkAreaDip(out workLeft, out workTop, out workRight, out workBottom);

            double baseLeft = workRight - w - EdgeRight;
            double target;
            if (mode == 0) target = workLeft + EdgeRight;
            else if (mode == 1) target = workLeft + (workRight - workLeft - w) / 2.0;
            else target = baseLeft;

            _settings.OffsetX = target - baseLeft;
            _settings.OffsetY = 0;
            Reposition();
        }

        /// <summary>当前落在哪一档（0/1/2）；拖到别处就是 -1。容差 3 DIP。</summary>
        public int CurrentAlignMode()
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            if (double.IsNaN(w) || w <= 0) w = DesiredSize.Width;
            if (double.IsNaN(w) || w <= 0) w = 200;

            double workLeft, workTop, workRight, workBottom;
            GetWorkAreaDip(out workLeft, out workTop, out workRight, out workBottom);

            double baseLeft = workRight - w - EdgeRight;
            double left = baseLeft + _settings.OffsetX;

            if (Math.Abs(left - (workLeft + EdgeRight)) <= 3) return 0;
            if (Math.Abs(left - (workLeft + (workRight - workLeft - w) / 2.0)) <= 3) return 1;
            if (Math.Abs(left - baseLeft) <= 3) return 2;
            return -1;
        }

        /// <summary>诊断用：当前 DPI 缩放。</summary>
        public double GetDpiScale()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return dpi.DpiScaleX;
        }

        /// <summary>窗口右边缘到工作区右边缘的距离（DIP），正常应等于 EdgeRight。</summary>
        public double RightMargin
        {
            get
            {
                double l, t, r, b;
                GetWorkAreaDip(out l, out t, out r, out b);
                return r - (Left + ActualWidth);
            }
        }

        /// <summary>窗口上边缘到工作区上边缘的距离（DIP），正常应等于 EdgeTop。</summary>
        public double TopMargin
        {
            get
            {
                double l, t, r, b;
                GetWorkAreaDip(out l, out t, out r, out b);
                return Top - t;
            }
        }

        /// <summary>诊断用：当前认定的工作区（DIP）与窗口实际矩形。</summary>
        public string GetWorkAreaText()
        {
            double l, t, r, b;
            GetWorkAreaDip(out l, out t, out r, out b);
            return string.Format("L={0:0.0} T={1:0.0} R={2:0.0} B={3:0.0}  |  Left={4:0.0} Top={5:0.0} Width={6:0.0} 右边距={7:0.0}",
                l, t, r, b, Left, Top, ActualWidth, r - (Left + ActualWidth));
        }

        /// <summary>
        /// 主显示器工作区，换算成 WPF 的 DIP。
        /// 用 WinForms 的 WorkingArea（物理像素、已排除任务栏）——
        /// WPF 的 SystemParameters.WorkArea 在「任务栏停靠在顶部/侧边」或
        /// 多显示器混合 DPI 时会给出错误工作区（实测：顶部任务栏时它仍返回 Top=0，
        /// 浮窗直接被任务栏压住）。
        /// </summary>
        private void GetWorkAreaDip(out double left, out double top, out double right, out double bottom)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

            try
            {
                var r = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                left = r.Left / sx;
                top = r.Top / sy;
                right = r.Right / sx;
                bottom = r.Bottom / sy;
                return;
            }
            catch (Exception) { }

            var wa = SystemParameters.WorkArea;
            left = wa.Left; top = wa.Top; right = wa.Right; bottom = wa.Bottom;
        }

        // ==================================================================
        // 交互
        // ==================================================================

        private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                var h = SettingsRequested;
                if (h != null) h(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (_settings.LockPosition || _settings.ClickThrough) return;

            _dragging = true;
            _lastScreenPoint = PointToScreen(e.GetPosition(this));
            CaptureMouse();
            e.Handled = true;
        }

        private void OnCardMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            var cur = PointToScreen(e.GetPosition(this));
            var dpi = VisualTreeHelper.GetDpi(this);
            double dx = (cur.X - _lastScreenPoint.X) / dpi.DpiScaleX;
            double dy = (cur.Y - _lastScreenPoint.Y) / dpi.DpiScaleY;

            Left += dx;
            Top += dy;
            _lastScreenPoint = cur;
        }

        private void OnCardMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;

            double wl, wt, wr, wb;
            GetWorkAreaDip(out wl, out wt, out wr, out wb);
            _settings.OffsetX = Left - (wr - ActualWidth - EdgeRight);
            _settings.OffsetY = Top - (wt + EdgeTop);

            var h = PositionChanged;
            if (h != null) h(this, _settings);
        }

        private void OnCardRightClick(object sender, MouseButtonEventArgs e)
        {
            if (_settings.ClickThrough) return;
            CtxMenu.PlacementTarget = this;
            CtxMenu.IsOpen = true;
            e.Handled = true;
        }

        private void OnMenuSettings(object sender, RoutedEventArgs e)
        {
            var h = SettingsRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void OnMenuHide(object sender, RoutedEventArgs e)
        {
            var h = HideRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void OnMenuExit(object sender, RoutedEventArgs e)
        {
            var h = ExitRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        private sealed class GroupUi
        {
            public StackPanel Root;
            public OutlineText Label;
            public OutlineText Value;
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
