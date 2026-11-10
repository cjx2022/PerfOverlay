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
        /// <summary>采集端引用，只用来查 CT 状态和装完驱动后重新探测。</summary>
        private MonitorHost _host;

        public event EventHandler<AppSettings> SettingsChanged;
        /// <summary>0=左上 1=中上 2=右上。参数是选中的档位。</summary>
        public event EventHandler<int> PositionPicked;
        public event EventHandler ExitRequested;

        public SettingsWindow()
        {
            InitializeComponent();

            // 标题栏上的产品名后面挂版本号：出问题时一眼知道在跑哪一版
            TxtProduct.Text = "PerfOverlay v" + UpdateChecker.CurrentVersion;
        }

        /// <summary>诊断用：三个位置按钮，供「等宽、不越界」的断言。</summary>
        public System.Windows.FrameworkElement[] PositionButtons()
        {
            return new System.Windows.FrameworkElement[] { PosLeft, PosCenter, PosRight };
        }

        /// <summary>诊断用：四张卡片的实际布局矩形，供「是否对齐」的数值断言。</summary>
        public System.Windows.FrameworkElement[] Cards()
        {
            return new System.Windows.FrameworkElement[] { CardDisplay, CardAppearance, CardBehavior, CardFrame };
        }

        /// <summary>诊断用：CT 行（开关 + 感叹号）。感叹号平时是 Collapsed，断言要临时点亮它。</summary>
        public System.Windows.FrameworkElement[] TempRow()
        {
            return new System.Windows.FrameworkElement[] { ChkCpuTemp, BtnTempWarn };
        }

        /// <summary>
        /// 诊断用：显示项目卡里的九个开关，顺序是「左列 4 个 + 右列 5 个」。
        /// 两列布局光看截图会漏掉文字被压窄、列间重叠这类问题，必须有 rect 可量。
        /// </summary>
        public System.Windows.FrameworkElement[] MetricSwitches()
        {
            return new System.Windows.FrameworkElement[]
            { ChkCpu, ChkMem, ChkFps, ChkLow, ChkVram, ChkClock, ChkGpu, ChkCpuTemp, ChkGpuTemp };
        }

        /// <summary>诊断用：内容区根（不含标题栏），按钮越界检查以它为右边界。</summary>
        public System.Windows.FrameworkElement ContentArea { get { return ContentRoot; } }

        /// <summary>
        /// 诊断用：主题三档（浅色 / 深色 / 跟随系统）。带上缩略图后这一排变宽不少，
        /// 是最容易被挤窄或顶出卡片右界的一行，必须有 rect 可量。
        /// </summary>
        public System.Windows.FrameworkElement[] ThemeSegments()
        {
            return new System.Windows.FrameworkElement[] { SegThemeLight, SegThemeDark, SegThemeAuto };
        }

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
            ChkClock.IsChecked = _s.ShowClock;
            ChkGpu.IsChecked = _s.ShowGpu;
            ChkCpuTemp.IsChecked = _s.ShowCpuTemp;
            ChkGpuTemp.IsChecked = _s.ShowGpuTemp;

            if (_s.SizeMode == "S") SegS.IsChecked = true;
            else if (_s.SizeMode == "L") SegL.IsChecked = true;
            else SegM.IsChecked = true;

            if (_s.RefreshMs <= 250) SegR250.IsChecked = true;
            else if (_s.RefreshMs <= 500) SegR500.IsChecked = true;
            else if (_s.RefreshMs <= 1000) SegR1000.IsChecked = true;
            else SegR2000.IsChecked = true;

            string theme = ThemeManager.Resolve(_s.Theme);
            bool isAuto = string.Equals((_s.Theme ?? "").Trim(), ThemeManager.Auto, StringComparison.OrdinalIgnoreCase);
            if (isAuto) SegThemeAuto.IsChecked = true;
            else if (theme == ThemeManager.Dark) SegThemeDark.IsChecked = true;
            else SegThemeLight.IsChecked = true;

            SldOpacity.Value = _s.Opacity;
            TxtOpacity.Text = ((int)Math.Round(_s.Opacity * 100)).ToString() + "%";

            SelectColor(_s.TextColor);
            ChkOutline.IsChecked = _s.ShowOutline;


            ChkLock.IsChecked = _s.LockPosition;
            ChkThrough.IsChecked = _s.ClickThrough;
            ChkTray.IsChecked = _s.ShowTrayIcon;
            ShowHotkey(_s.Hotkey, false, null);
            SetPositionMode(-1);

            // 以注册表实际状态为准：用户可能在别处删过启动项
            ChkStartup.IsChecked = StartupHelper.IsEnabled;

            _loading = false;
            RefreshPrivilegeUi();
        }

        /// <summary>
        /// 配图专用：把面板里两处「读系统真实状态」的地方改成固定值 ——
        /// 开机自启（注册表）与帧数采集（实时探测结果）。否则 README 截图里会带上
        /// 本机是否已授权、是否已设自启的痕迹，而且每次生成还不一样。
        /// 只在 ReadmeShot 里调用，正常运行路径不受影响。
        /// </summary>
        internal void ApplyShotState(bool startupEnabled)
        {
            ChkStartup.IsChecked = startupEnabled;

            DotStatus.Fill = new SolidColorBrush(Color.FromRgb(0x2C, 0xBF, 0x6E));
            TxtFrameStatus.Text = "采集中（前台窗口）";
            BtnElevate.Visibility = Visibility.Collapsed;
        }

        public void UpdateFrameStatus(MonitorHost host)
        {
            if (host == null) return;

            _host = host;

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
            RefreshTempUi();
        }

        private void RefreshPrivilegeUi()
        {
            BtnElevate.IsEnabled = !PrivilegeHelper.IsElevated;
        }

        /// <summary>
        /// 温度项的感叹号只在「确实卡住了、而且用户能动手解决」时出现：
        ///   not_installed → 该装组件
        ///   no_access     → 装好了但缺管理员
        /// 其它状态（读得到、架构不支持、装了也读不到）都不打扰 —— 原因写在浮窗 tooltip 里。
        /// </summary>
        private void RefreshTempUi()
        {
            if (BtnTempWarn == null) return;

            if (_host == null || ChkCpuTemp.IsChecked != true)
            {
                BtnTempWarn.Visibility = Visibility.Collapsed;
                return;
            }

            string state = _host.CpuTempState;
            if (state == "not_installed")
            {
                BtnTempWarn.Visibility = Visibility.Visible;
                BtnTempWarn.ToolTip = "CPU 温度需要装一个驱动组件，点这里看说明";
            }
            else if (state == "no_access")
            {
                BtnTempWarn.Visibility = Visibility.Visible;
                BtnTempWarn.ToolTip = "温度组件已装好，需要管理员权限才能读";
            }
            else
            {
                BtnTempWarn.Visibility = Visibility.Collapsed;
            }
        }

        private void OnTempWarnClick(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;

            var mode = _host.CpuTempState == "no_access"
                ? DriverPromptWindow.Mode.Elevate
                : DriverPromptWindow.Mode.Install;

            DriverPromptWindow.ShowPrompt(this, mode, delegate
            {
                _host.ResetCpuTempProbe();
                RefreshTempUi();
            });

            RefreshTempUi();
        }

        // ==================================================================

        private void Collect()
        {
            _s.ShowCpu = ChkCpu.IsChecked == true;
            _s.ShowMemory = ChkMem.IsChecked == true;
            _s.ShowFps = ChkFps.IsChecked == true;
            _s.ShowLowFps = ChkLow.IsChecked == true;
            _s.ShowVram = ChkVram.IsChecked == true;
            _s.ShowClock = ChkClock.IsChecked == true;
            _s.ShowGpu = ChkGpu.IsChecked == true;
            _s.ShowCpuTemp = ChkCpuTemp.IsChecked == true;
            _s.ShowGpuTemp = ChkGpuTemp.IsChecked == true;

            if (SegS.IsChecked == true) _s.SizeMode = "S";
            else if (SegL.IsChecked == true) _s.SizeMode = "L";
            else _s.SizeMode = "M";

            if (SegR250.IsChecked == true) _s.RefreshMs = 250;
            else if (SegR500.IsChecked == true) _s.RefreshMs = 500;
            else if (SegR1000.IsChecked == true) _s.RefreshMs = 1000;
            else _s.RefreshMs = 2000;

            if (SegThemeDark.IsChecked == true) _s.Theme = ThemeManager.Dark;
            else if (SegThemeAuto.IsChecked == true) _s.Theme = ThemeManager.Auto;
            else _s.Theme = ThemeManager.Light;

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

        private void OnAnyOptionChanged(object sender, RoutedEventArgs e) { Emit(); RefreshTempUi(); }

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

        /// <summary>
        /// 主题切换。这里立刻换，不等 SettingsChanged 绕回 App —— 否则「点了没反应」
        /// 的感觉非常明显（尤其是面板自己是浅色、用户正要点深色）。
        /// 托盘菜单是 WinForms，WPF 资源管不到，由 App.ApplySettings 里的同名判断补一次。
        /// </summary>
        private void OnThemeChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ThemeManager.Apply(CollectTheme());
            Emit();
        }

        private string CollectTheme()
        {
            if (SegThemeDark.IsChecked == true) return ThemeManager.Dark;
            if (SegThemeAuto.IsChecked == true) return ThemeManager.Auto;
            return ThemeManager.Light;
        }


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

        /// <summary>把位置档位点亮，-1 = 当前位置不在三档上（用户拖过）。</summary>
        public void SetPositionMode(int mode)
        {
            PosLeft.IsChecked = mode == 0;
            PosCenter.IsChecked = mode == 1;
            PosRight.IsChecked = mode == 2;
        }

        private void OnPositionPicked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            int mode = sender == PosLeft ? 0 : sender == PosCenter ? 1 : 2;
            SetPositionMode(mode);
            var h = PositionPicked;
            if (h != null) h(this, mode);
        }

        // ==================================================================
        // 显示 / 隐藏浮窗的全局热键
        //
        // 冲突检测靠系统：RegisterHotKey 失败就是 1409（已被占用），
        // 不需要自己维护一张「哪些软件用了哪些键」的表 —— 那种表永远不全。
        // ==================================================================

        private bool _capturingHotkey;

        /// <summary>诊断用：热键框与它当前显示的文字，供布局断言。</summary>
        public System.Windows.FrameworkElement HotkeyBoxElement { get { return HotkeyBox; } }
        public string HotkeyText { get { return TxtHotkey.Text; } }

        /// <summary>按主题取色：错误红与聚焦蓝都由主题决定，深色下用更亮的红才看得清。</summary>
        private Brush Res(string key)
        {
            try { return (Brush)FindResource(key); } catch (Exception) { return Brushes.Gray; }
        }

        private void ShowHotkey(string spec, bool error, string errorText)
        {
            if (error)
            {
                TxtHotkey.Text = errorText;
                TxtHotkey.Foreground = Res("Danger");
                HotkeyBox.BorderBrush = Res("Danger");
                return;
            }

            // 空闲态与文字色都走资源引用，而不是 new 一个固定画刷：
            // 换主题时这两处会自己跟着变，不用再手动回填（固定画刷是「切了主题还留一条浅蓝边」的元凶）。
            HotkeyBox.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "WindowBorder");
            if (string.IsNullOrEmpty(spec))
            {
                TxtHotkey.Text = "未设置";
                TxtHotkey.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondary");
            }
            else
            {
                TxtHotkey.Text = HotkeyService.Pretty(spec);
                TxtHotkey.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimary");
            }
        }

        private void OnHotkeyBoxClick(object sender, MouseButtonEventArgs e)
        {
            if (_loading) return;
            _capturingHotkey = true;
            TxtHotkey.Text = "按下组合键…";
            TxtHotkey.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondary");
            HotkeyBox.BorderBrush = Res("Accent");
            Keyboard.Focus(HotkeyBox);
            e.Handled = true;
        }

        private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturingHotkey) return;
            e.Handled = true;

            // 按住 Alt 时 WPF 给的是 Key.System，真正的主键在 SystemKey 里
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape) { EndHotkeyCapture(); return; }
            if (key == Key.Back || key == Key.Delete) { SetHotkey(""); return; }

            // 只按了修饰键，继续等主键
            if (!HotkeyService.IsRealKey(key)) return;

            ModifierKeys mods = Keyboard.Modifiers;
            if (!HotkeyService.IsAcceptable(mods, key))
            {
                // 纯字母/数字会抢走全局输入，必须带修饰键
                EndHotkeyCapture();
                MessageBox.Show(this, "请按住 Ctrl、Alt 或 Win 再按一个键。",
                                "浮窗显示快捷键", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetHotkey(HotkeyService.Format(mods, key));
        }

        private void OnHotkeyLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // 点到别处就等于放弃这次修改，不要把半截状态留在界面上
            if (_capturingHotkey) EndHotkeyCapture();
        }

        private void EndHotkeyCapture()
        {
            _capturingHotkey = false;
            Keyboard.ClearFocus();
            ShowHotkey(_s.Hotkey, false, null);
        }

        private void SetHotkey(string spec)
        {
            string old = HotkeyService.Current;
            string err;
            if (HotkeyService.Apply(spec, out err))
            {
                _s.Hotkey = spec;
                _capturingHotkey = false;
                Keyboard.ClearFocus();
                ShowHotkey(spec, false, null);
                Emit();
                MessageBox.Show(this,
                    string.IsNullOrEmpty(spec) ? "已清除浮窗显示快捷键。"
                                               : "已设为 " + HotkeyService.Pretty(spec) + "。",
                    "浮窗显示快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 注册失败时旧键已经被解掉了，必须抢回来：
            // 否则用户只是「试一下别的键」，原来能用的热键就没了。
            if (!string.IsNullOrEmpty(old))
            {
                string ignored;
                HotkeyService.Apply(old, out ignored);
            }
            _capturingHotkey = false;
            Keyboard.ClearFocus();
            ShowHotkey(_s.Hotkey, false, null);
            MessageBox.Show(this, err, "浮窗显示快捷键",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
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
