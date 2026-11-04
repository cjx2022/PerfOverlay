using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PerfOverlay.Services;

namespace PerfOverlay.Views
{
    /// <summary>
    /// 版本与更新：报当前版本，按需向 GitHub 查一次最新版本。
    ///
    /// 只有「查」和「跳转」两个动作，下载与替换交给用户自己 —— 程序常驻托盘,
    /// 自动替换正在运行的 exe 会跟单实例互斥体、提权进程和 ETW 采集打架。
    /// </summary>
    public partial class AboutWindow : Window
    {
        private bool _busy;

        public AboutWindow()
        {
            InitializeComponent();
            TxtVersion.Text = "v" + UpdateChecker.CurrentVersion;
            Loaded += (s, e) => StartCheck();
        }

        /// <summary>探针用：把「检查更新」跑一遍（窗口不用真的显示出来）。</summary>
        public void StartCheck()
        {
            if (_busy) return;
            _busy = true;
            BtnCheck.IsEnabled = false;
            BtnCheck.Content = "检查中…";
            SetStatus("正在查询 GitHub 上的最新版本…", "#FF7189A6", "#FFB9C8DA");
            RunCheckAsync();
        }

        /// <summary>探针用：当前状态文字。</summary>
        public string StatusText { get { return TxtStatus.Text; } }

        /// <summary>探针用：下载按钮是否已出现。</summary>
        public bool DownloadVisible { get { return BtnDownload.Visibility == Visibility.Visible; } }

        /// <summary>探针用：结果落一个字符串，便于无头断言。</summary>
        public string ResultSummary
        {
            get
            {
                return string.Format("{0}|{1}|{2}", TxtStatus.Text, DownloadVisible ? "download" : "nodownload",
                    BtnCheck.Content);
            }
        }

        private async void RunCheckAsync()
        {
            UpdateResult r = null;
            try
            {
                // 网络请求放后台线程：GitHub 慢的时候（几秒）绝不能让面板假死
                r = await Task.Run(() => UpdateChecker.Check(8000));
            }
            catch (Exception) { }

            _busy = false;
            BtnCheck.IsEnabled = true;
            BtnCheck.Content = "重新检查";

            if (r == null)
            {
                SetStatus("检查失败，请稍后重试", "#FFC0392B", "#FFE08A80");
                BtnDownload.Visibility = Visibility.Collapsed;
                return;
            }

            if (r.Failed)
            {
                SetStatus(r.Error, "#FFC0392B", "#FFE08A80");
                BtnDownload.Visibility = Visibility.Collapsed;
                return;
            }

            if (r.HasUpdate)
            {
                SetStatus("发现新版本 v" + r.Latest + "，当前 v" + r.Current, "#FF2C6BF0", "#FF2C6BF0");
                BtnDownload.Visibility = Visibility.Visible;
            }
            else
            {
                SetStatus("已是最新版本（v" + r.Current + "）", "#FF1F9D55", "#FF5CC98B");
                BtnDownload.Visibility = Visibility.Collapsed;
            }
        }

        private void SetStatus(string text, string textColor, string dotColor)
        {
            TxtStatus.Text = text;
            try
            {
                TxtStatus.Foreground = (Brush)new BrushConverter().ConvertFromString(textColor);
                DotStatus.Fill = (Brush)new BrushConverter().ConvertFromString(dotColor);
            }
            catch (Exception) { }
        }

        private void OnCheckUpdate(object sender, RoutedEventArgs e)
        {
            StartCheck();
        }

        private void OnOpenDownload(object sender, RoutedEventArgs e)
        {
            OpenUrl(UpdateChecker.ReleasesUrl);
        }

        private void OnOpenRepo(object sender, RoutedEventArgs e)
        {
            OpenUrl(UpdateChecker.RepoUrl);
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception) { }
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>探针用：把窗口挪到屏幕外，不抢焦点也不闪。</summary>
        public void MoveOffscreen()
        {
            Left = -20000;
            Top = -20000;
        }

        /// <summary>
        /// 探针用：把几何量出来定案。
        /// 这一窗最容易出的事是「左下按钮和右下按钮组撞在一起」（窗口只有 404 宽），
        /// 肉眼看截图不一定发现 1~2 DIP 的贴合，所以这里按矩形算间距与越界。
        /// </summary>
        public string GeometryReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("window = " + ActualWidth.ToString("F1") + " x " + ActualHeight.ToString("F1"));

            var repoScreen = Box(BtnRepo, this);
            var checkScreen = Box(BtnCheck, this);
            bool hasDownload = BtnDownload.Visibility == Visibility.Visible;
            var dlScreen = hasDownload ? Box(BtnDownload, this) : new Rect();

            double gap = hasDownload
                ? dlScreen.Left - repoScreen.Right
                : checkScreen.Left - repoScreen.Right;
            sb.AppendLine("leftButton = [" + repoScreen.Left.ToString("F1") + ".." + repoScreen.Right.ToString("F1") + "]");
            sb.AppendLine("rightGroup = [" + (hasDownload ? dlScreen.Left : checkScreen.Left).ToString("F1")
                          + ".." + checkScreen.Right.ToString("F1") + "]");
            sb.AppendLine("gap = " + gap.ToString("F1") + " => " + (gap >= 0 ? "OK" : "OVERLAP"));
            sb.AppendLine("downloadVisible = " + hasDownload);

            // 状态文字不许越过它所在卡片的右边界（- 内边距）
            var card = (FrameworkElement)VisualTreeHelper.GetParent(TxtStatus);
            var cardBorder = VisualTreeHelper.GetParent(card) as FrameworkElement;
            if (cardBorder != null)
            {
                var cb = Box(cardBorder, this);
                var tb = Box(TxtStatus, this);
                bool inside = tb.Right <= cb.Right - 11.5 + 0.5;
                sb.AppendLine("statusRight = " + tb.Right.ToString("F1") + " / cardRight = " + cb.Right.ToString("F1")
                              + " => " + (inside ? "OK" : "OVERFLOW"));
            }

            // 按钮不许被压窄：先用布局期 ActualWidth 记账，再拿 Measure(∞) 的期望宽度比
            foreach (var b in new[] { BtnRepo, BtnDownload, BtnCheck })
            {
                if (b.Visibility != Visibility.Visible) continue;
                double actual = b.ActualWidth;
                b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                bool ok = actual + 0.5 >= b.DesiredSize.Width;
                sb.AppendLine("btn[" + b.Content + "] actual=" + actual.ToString("F1")
                              + " need=" + b.DesiredSize.Width.ToString("F1")
                              + " => " + (ok ? "OK" : "SQUEEZED"));
            }

            sb.AppendLine("status = " + TxtStatus.Text);
            return sb.ToString();
        }

        private static Rect Box(FrameworkElement e, UIElement root)
        {
            var p = e.TranslatePoint(new Point(0, 0), root);
            return new Rect(p.X, p.Y, e.ActualWidth, e.ActualHeight);
        }
    }
}
