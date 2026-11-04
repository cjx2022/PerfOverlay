using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PerfOverlay.Services;

namespace PerfOverlay.Views
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _s = new AppSettings();
        // 必须是 true：XAML 解析期 SldOpacity 设 Maximum 就会触发 ValueChanged，
        // 那一刻其它控件还没构造出来，Collect() 必然 NRE。
        private bool _loading = true;
        private bool _allowClose;

        public event EventHandler<AppSettings> SettingsChanged;
        public event EventHandler ResetPositionRequested;
        public event EventHandler ExitRequested;

        public SettingsWindow()
        {
            InitializeComponent();
        }

        /// <summary>诊断用：复位按钮，供「文字是否放得下」的断言。</summary>
        public System.Windows.FrameworkElement ResetButton { get { return BtnReset; } }

        /// <summary>诊断用：四张卡片的实际布局矩形，供「是否对齐」的数值断言。</summary>
        public System.Windows.FrameworkElement[] Cards()
        {
            return new System.Windows.FrameworkElement[] { CardDisplay, CardAppearance, CardBehavior, CardFrame };
        }

        /// <summary>诊断用：内容区根（不含标题栏），按钮越界检查以它为右边界。</summary>
        public System.Windows.FrameworkElement ContentArea { get { return ContentRoot; } }

        /// <summary>
        /// 诊断用：内容在给定宽度下需要多高。窗口是 SizeToContent=Height，
        /// 所以这个值应当等于 ActualHeight；两者一致就说明没有被裁掉、
        /// 也不需要滚动条。
        /// </summary>
        public Size MeasureContentHeight()
        {
            var root = Content as FrameworkElement;
            if (root == null) return new Size(0, 0);
            root.Measure(new Size(Width, double.PositiveInfinity));
            return root.DesiredSize;
        }

        /// <summary>
        /// 诊断用：把窗口挪到屏幕外。配合 PrintWindow 抓图，
        /// 测试期间不在用户眼前闪一下、也不抢焦点。
        /// </summary>
        public void MoveOffscreen()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -4200;
            Top = 0;
        }

        /// <summary>把配置灌进控件（不触发保存）。</summary>
        public void Bind(AppSettings settings)
        {
            _s = settings.Clone();
            _loading = true;

            ChkCpu.IsChecked = _s.ShowCpu;
            ChkMem.IsChecked = _s.ShowMemory;
            ChkFps.IsChecked = _s.ShowFps;
            ChkLow.IsChecked = _s.ShowLowFps;
            ChkVram.IsChecked = _s.ShowVram;

            if (_s.SizeMode == "S") SegS.IsChecked = true;
            else if (_s.SizeMode == "L") SegL.IsChecked = true;
            else SegM.IsChecked = true;

            if (_s.RefreshMs <= 250) SegR250.IsChecked = true;
            else if (_s.RefreshMs <= 500) SegR500.IsChecked = true;
            else if (_s.RefreshMs <= 1000) SegR1000.IsChecked = true;
            else SegR2000.IsChecked = true;

            SldOpacity.Value = _s.Opacity;
            TxtOpacity.Text = ((int)Math.Round(_s.Opacity * 100)).ToString() + "%";

            SelectColor(_s.TextColor);
            ChkOutline.IsChecked = _s.ShowOutline;


            ChkLock.IsChecked = _s.LockPosition;
            ChkThrough.IsChecked = _s.ClickThrough;
            ChkTray.IsChecked = _s.ShowTrayIcon;

            // 以注册表实际状态为准：用户可能在别处删过启动项
            ChkStartup.IsChecked = StartupHelper.IsEnabled;

            _loading = false;
            RefreshPrivilegeUi();
        }

        public void UpdateFrameStatus(MonitorHost host)
        {
            if (host == null) return;

            bool can = PrivilegeHelper.CanCaptureFrames;
            bool running = host.FramesRunning;

            if (running)
            {
                DotStatus.Fill = new SolidColorBrush(Color.FromRgb(0x2C, 0xBF, 0x6E));
                TxtFrameStatus.Text = "采集中（前台窗口）";
            }
            else if (!host.Frames.PresentMonFound)
            {
                DotStatus.Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
                TxtFrameStatus.Text = "缺少 PresentMon.exe（tools 目录）";
            }
            else if (!can)
            {
                DotStatus.Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23));
                TxtFrameStatus.Text = "未授权，点下方按钮授权";
            }
            else
            {
                DotStatus.Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
                TxtFrameStatus.Text = "启动失败：" + host.FramesDiagnostic;
            }

            RefreshPrivilegeUi();
        }

        private void RefreshPrivilegeUi()
        {
            BtnElevate.IsEnabled = !PrivilegeHelper.IsElevated;
        }

        // ==================================================================

        private void Collect()
        {
            _s.ShowCpu = ChkCpu.IsChecked == true;
            _s.ShowMemory = ChkMem.IsChecked == true;
            _s.ShowFps = ChkFps.IsChecked == true;
            _s.ShowLowFps = ChkLow.IsChecked == true;
            _s.ShowVram = ChkVram.IsChecked == true;

            if (SegS.IsChecked == true) _s.SizeMode = "S";
            else if (SegL.IsChecked == true) _s.SizeMode = "L";
            else _s.SizeMode = "M";

            if (SegR250.IsChecked == true) _s.RefreshMs = 250;
            else if (SegR500.IsChecked == true) _s.RefreshMs = 500;
            else if (SegR1000.IsChecked == true) _s.RefreshMs = 1000;
            else _s.RefreshMs = 2000;

            _s.Opacity = SldOpacity.Value;
            _s.ShowOutline = ChkOutline.IsChecked == true;

            var picked = CheckedColor();
            if (picked != null) _s.TextColor = picked;
            _s.LockPosition = ChkLock.IsChecked == true;
            _s.ClickThrough = ChkThrough.IsChecked == true;
            _s.ShowTrayIcon = ChkTray.IsChecked == true;
            _s.StartWithWindows = ChkStartup.IsChecked == true;
        }

        private void Emit()
        {
            // 解析期与绑定期一律不回调，避免半构造状态下取值
            if (_loading || !IsLoaded) return;
            Collect();
            var h = SettingsChanged;
            if (h != null) h(this, _s.Clone());
        }

        private void OnAnyOptionChanged(object sender, RoutedEventArgs e) { Emit(); }

        private void OnColorChanged(object sender, RoutedEventArgs e) { Emit(); }

        /// <summary>把配置里的颜色映射到对应色块；不在预设里就回落到白色。</summary>
        private void SelectColor(string hex)
        {
            string want = (hex ?? "").Trim().ToUpperInvariant();
            bool hit = false;
            foreach (var rb in AllSwatches())
            {
                string tag = ((string)rb.Tag ?? "").ToUpperInvariant();
                if (!hit && tag == want) { rb.IsChecked = true; hit = true; }
            }
            if (!hit) Sw0.IsChecked = true;
        }

        private string CheckedColor()
        {
            foreach (var rb in AllSwatches())
            {
                if (rb.IsChecked == true) return (string)rb.Tag;
            }
            return null;
        }

        private System.Windows.Controls.RadioButton[] AllSwatches()
        {
            return new[] { Sw0, Sw1, Sw2, Sw3, Sw4, Sw5 };
        }

        private void OnSizeChanged(object sender, RoutedEventArgs e) { Emit(); }

        private void OnRateChanged(object sender, RoutedEventArgs e) { Emit(); }


        private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtOpacity != null)
                TxtOpacity.Text = ((int)Math.Round(e.NewValue * 100)).ToString() + "%";
            Emit();
        }

        private void OnStartupChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool want = ChkStartup.IsChecked == true;
            bool ok = StartupHelper.Apply(want);

            if (!ok)
            {
                // 写注册表失败就把界面拨回真实状态，不留下"看起来开了其实没开"的假象
                _loading = true;
                ChkStartup.IsChecked = StartupHelper.IsEnabled;
                _loading = false;
                MessageBox.Show(this, "写入开机启动项失败，请检查当前用户的注册表权限。",
                    "性能浮窗", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Emit();
        }

        private void OnResetPosition(object sender, RoutedEventArgs e)
        {
            _s.OffsetX = 0;
            _s.OffsetY = 0;
            var h = ResetPositionRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void OnElevateClick(object sender, RoutedEventArgs e)
        {
            // --takeover：提权后的新进程要等旧实例释放单实例互斥体再继续，
            // 否则它会误判「已有实例」并立刻退出（表现为点了 UAC 之后什么都没发生）。
            string err = PrivilegeHelper.RestartElevated("--startup --takeover");
            if (err == null)
            {
                TxtFrameStatus.Text = "已以管理员身份重启，正在接管…";
                var h = ExitRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
            else
            {
                TxtFrameStatus.Text = err;
                MessageBox.Show(this,
                    err + "\n\n可改用在「以管理员身份运行」的命令提示符中启动本程序。",
                    "性能浮窗", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnResetDefaults(object sender, RoutedEventArgs e)
        {
            var d = new AppSettings { FirstRunDone = true };
            Bind(d);
            var h = SettingsChanged;
            if (h != null) h(this, d.Clone());
        }

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            // 模态子窗：版本信息是个「看一眼就走」的小面板，没必要管理多实例。
            // Owner 指到本窗口，跟随居中；主面板此时不可点，避免把子窗盖到后面找不着。
            var w = new AboutWindow { Owner = this };
            w.ShowDialog();
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        public void CloseForShutdown()
        {
            _allowClose = true;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowClose)
            {
                // 点右上角 × 只是收起面板，程序继续在后台跑
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
        }
    }
}
