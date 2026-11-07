using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PerfOverlay.Services;

namespace PerfOverlay.Views
{
    /// <summary>
    /// 「CPU 温度需要一次安装」的提示窗。
    ///
    /// 这里的分寸很关键：程序自己**绝不**偷偷装东西。用户在设置面板看到感叹号、
    /// 点开、读完说明、按下「下载并安装」，才会有下面这一串动作：
    ///   下载官方安装器 → 校验 PE + 数字签名者 → 以管理员身份 -install -silent → 验证设备可开
    /// 任何一步失败都如实显示原因，不重试、不换源、不静默降级。
    /// </summary>
    public partial class DriverPromptWindow : Window
    {
        public enum Mode
        {
            /// <summary>还没装：主按钮是「下载并安装」。</summary>
            Install,
            /// <summary>装好了但当前不是管理员：主按钮是「以管理员身份重启」。</summary>
            Elevate,
            /// <summary>装完了：主按钮变「完成」，1 秒后自己关。</summary>
            Done
        }

        private Mode _mode;
        private readonly Action _onInstalled;
        private bool _busy;

        private DriverPromptWindow(Mode mode, Action onInstalled)
        {
            InitializeComponent();
            _onInstalled = onInstalled;
            ApplyMode(mode);
        }

        /// <summary>打开提示窗（模态）。onInstalled 在安装成功后调用，用于刷新采集端状态。</summary>
        public static void ShowPrompt(Window owner, Mode mode, Action onInstalled)
        {
            var w = new DriverPromptWindow(mode, onInstalled);
            if (owner != null && owner.IsVisible) w.Owner = owner;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.ShowDialog();
        }

        // ==================================================================

        private void ApplyMode(Mode mode)
        {
            _mode = mode;

            switch (mode)
            {
                case Mode.Elevate:
                    TxtHead.Text = "驱动已就绪，还差管理员权限";
                    TxtBody.Text =
                        "PawnIO 的设备对象只允许 SYSTEM 与 Administrators 访问（INF 里写死的 ACL），"
                        + "所以读 CPU 温度必须以管理员身份运行本程序。\n\n"
                        + "程序启动时本来就会自动提权一次，只有被拒绝或没走上那条路时才会停在这里。"
                        + "点下面的按钮重启一次即可，浮窗的所有设置都会保留。";
                    BtnPrimary.Content = "以管理员身份重启";
                    break;

                case Mode.Done:
                    TxtHead.Text = "已就绪";
                    TxtBody.Text = "CPU 温度组件已经装好，温度会在一秒内出现在浮窗上。";
                    BtnPrimary.Content = "完成";
                    break;

                default:
                    TxtHead.Text = "CPU 温度需要一次安装";
                    TxtBody.Text = BuildInstallText();
                    BtnPrimary.Content = "下载并安装";
                    break;
            }
        }

        private static string BuildInstallText()
        {
            string vendor = PawnIoCpuTemp.IsIntel ? "Intel" : "AMD";
            string source = PawnIoCpuTemp.IsIntel
                ? "目标温度与实测偏移只能从 MSR 寄存器（0x1A2 / 0x1B1）里读"
                : "封装温度只能从 SMN 寄存器（0x00059800）里读";

            return "Windows 用户态没有稳定的读 CPU 温度接口。你的处理器是 " + vendor + "，"
                 + source + "，而这需要内核态驱动。\n\n"
                 + "本程序不带驱动（新建内核驱动必须由微软签名，要企业证书与审核流程）。"
                 + "改为引导安装 PawnIO：微软签名、内存完整性（HVCI）兼容、不在易受攻击驱动阻止列表里，"
                 + "装完约 1 MB，随时可以在「应用和功能」里正常卸载。\n\n"
                 + "接下来会发生三件事：下载官方安装包（约 3.3 MB）→ 校验数字签名 → "
                 + "弹出一次管理员授权并静默安装。不装也不影响其它任何指标，CT 显示 -- 而已。";
        }

        private void SetStatus(string text, bool isError)
        {
            BoxStatus.Visibility = Visibility.Visible;
            BoxStatus.Background = isError
                ? new SolidColorBrush(Color.FromRgb(0xFD, 0xF0, 0xF0))
                : new SolidColorBrush(Color.FromRgb(0xF3, 0xF7, 0xFD));
            TxtStatus.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xB4, 0x2A, 0x2A))
                : new SolidColorBrush(Color.FromRgb(0x33, 0x44, 0x55));
            TxtStatus.Text = text;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BtnPrimary.IsEnabled = !busy;
            BtnCancel.IsEnabled = !busy;
        }

        // ==================================================================
        // 交互
        // ==================================================================

        private void OnPrimaryClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            switch (_mode)
            {
                case Mode.Done:
                    Close();
                    return;

                case Mode.Elevate:
                    RestartElevated();
                    return;

                default:
                    StartInstall();
                    return;
            }
        }

        private void RestartElevated()
        {
            SetBusy(true);
            SetStatus("正在请求管理员权限…", false);

            // 复用程序已有的提权链路：与「以管理员身份启动」按钮同一条路
            string err = PrivilegeHelper.RestartElevated("--startup --takeover");
            if (err == null)
            {
                Application.Current.Shutdown();
                return;
            }

            SetBusy(false);
            SetStatus("提权失败：" + err, true);
        }

        private void StartInstall()
        {
            SetBusy(true);
            SetStatus("正在下载安装程序（约 3.3 MB）…", false);
            BtnPrimary.Content = "安装中…";

            // 网络下载 + 安装器等待都是阻塞调用，扔后台线程，别冻住界面
            var t = new Thread(InstallWorker) { IsBackground = true, Name = "PerfOverlay.PawnIoInstall" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private void InstallWorker()
        {
            string why;

            string path = PawnIoInstaller.EnsureSetup(out why);
            if (path == null)
            {
                Fail("安装包获取失败：" + why + "\n\n"
                     + "如果网络访问 GitHub 不稳定，可以手动下载：" + PawnIoInstaller.OfficialUrl
                     + "\n下载后放到「" + PawnIoInstaller.CachePath + "」再点一次即可。");
                return;
            }

            Ui(() => SetStatus("正在安装驱动（马上会弹出管理员授权）…", false));

            string err = PawnIoInstaller.Install(path);
            if (err != null)
            {
                Fail("安装未完成：" + err);
                return;
            }

            Ui(() =>
            {
                if (_onInstalled != null)
                {
                    try { _onInstalled(); } catch (Exception) { }
                }
            });

            // 装完再验一遍：设备能打开才叫真的可用（-silent 下退出码不代表什么）
            string openWhy;
            bool usable = false;
            try { usable = PawnIoInstaller.CanOpenDevice(out openWhy); }
            catch (Exception) { openWhy = ""; }

            if (usable)
            {
                Ui(() =>
                {
                    ApplyMode(Mode.Done);
                    SetStatus("安装完成，驱动已就绪。", false);
                    SetBusy(false);
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        Close();
                    };
                    timer.Start();
                });
            }
            else
            {
                Ui(() =>
                {
                    ApplyMode(Mode.Elevate);
                    SetStatus("驱动已装好，但当前进程读不到设备 —— " + openWhy, false);
                    SetBusy(false);
                });
            }
        }

        private void Fail(string message)
        {
            Ui(() =>
            {
                BtnPrimary.Content = "重试";
                SetBusy(false);
                SetStatus(message, true);
            });
        }

        private void Ui(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.Invoke(action);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            Close();
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch (Exception) { }
        }
    }
}
