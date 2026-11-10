using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PerfOverlay.Views;
using Drawing2D = System.Drawing.Drawing2D;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using Pen = System.Drawing.Pen;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace PerfOverlay.Services
{
    /// <summary>
    /// README 配图生成：把「控制面板」「深色背景上的浮窗」「浅色背景上的浮窗」离屏渲染并合成一张。
    ///
    /// 为什么不用屏幕截图：截屏要求窗口真的显示出来，会抢焦点、会闪一下，
    /// 而且浮窗压在壁纸上时底色不可控（壁纸亮度随时变）。
    /// 这里用 RenderTargetBitmap 直接渲染视觉树得到带 alpha 的位图，再自己铺底色 ——
    /// 颜色固定、可复现、随时能重出。
    ///
    /// 窗口仍然 Show（WPF 的布局/DPI 依赖真实 Hwnd），但整个过程 Opacity=0，
    /// 屏幕上不会出现任何东西。
    /// </summary>
    public static class ReadmeShot
    {
        private const int Scale = 2;                 // 2x 渲染，README 里缩放后依然清晰
        private static readonly Color DarkBackdrop = Color.FromArgb(0x14, 0x18, 0x1E);
        private static readonly Color LightBackdrop = Color.FromArgb(0xF2, 0xF5, 0xF9);
        private static readonly Color CanvasBackdrop = Color.FromArgb(0xFF, 0xFF, 0xFF);

        public static string Compose(string outDir)
        {
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            // 配图固定浅色打头：README 的图必须可复现，不能因为生成图的那台机器
            // 设了深色主题就换一套配色。深色面板另外单独出一张 console-dark.png。
            string prev = ThemeManager.Effective;
            ThemeManager.Apply(ThemeManager.Light);
            try
            {
                using (var overlay = RenderOverlay())
                using (var panel = RenderPanel())
                {
                    var hero = ComposeHero(panel, overlay);
                    string heroPath = Path.Combine(outDir, "screenshot.png");
                    hero.Save(heroPath, ImageFormat.Png);
                    hero.Dispose();

                    // 顺带输出几张单独的图，README 里可以按需引用
                    Save(overlay, Path.Combine(outDir, "overlay-dark.png"), DarkBackdrop);
                    Save(overlay, Path.Combine(outDir, "overlay-light.png"), LightBackdrop);
                    Save(panel, Path.Combine(outDir, "console.png"), CanvasBackdrop);

                    using (var darkPanel = RenderPanel(ThemeManager.Dark))
                        Save(darkPanel, Path.Combine(outDir, "console-dark.png"), DarkBackdrop);

                    return heroPath;
                }
            }
            finally
            {
                ThemeManager.Apply(prev);
            }
        }

        // ------------------------------------------------------------------

        /// <summary>浮窗：九项指标全开（配图要展示全部能力）、中等字号、默认白字带描边，填一组示例数值。</summary>
        private static Bitmap RenderOverlay()
        {
            // 同 RenderPanel：不读本机配置，保证图可复现、不带个人痕迹。
            var s = new AppSettings
            {
                ShowCpu = true, ShowMemory = true, ShowFps = true, ShowLowFps = true, ShowVram = true,
                ShowClock = true, ShowGpu = true, ShowCpuTemp = true, ShowGpuTemp = true,
                SizeMode = "M",
                TextColor = "#FFFFFF",
                ShowLabels = true,
                ShowOutline = true,
                Opacity = 1.0,
                OffsetX = 0,
                OffsetY = 0,
                Hotkey = "",
                FirstRunDone = true
            };

            var w = new OverlayWindow();
            w.ApplySettings(s, true);
            w.Opacity = 0;              // 必须在 Show 之前置 0，否则窗口会闪一下
            w.Show();
            w.UpdateLayout();

            // 用真实观感的示例数值，比一排 N/A 更能说明这是什么东西
            w.UpdateMetrics(new MetricsSnapshot
            {
                CpuOk = true, CpuPercent = 23,
                MemoryPercent = 61, MemoryUsed = 10UL << 30, MemoryTotal = 16UL << 30,
                FpsOk = true, Fps = 144, LowFps = 118, FrameCount = 1440, TargetProcess = "game",
                VramOk = true, VramPercent = 42, VramUsed = 3UL << 30, VramBudget = 8UL << 30,
                ClockText = "21:45",
                GpuOk = true, GpuPercent = 87,
                TempCpuOk = true, TempCpuC = 62,
                TempGpuOk = true, TempGpuC = 71
            });

            w.UpdateLayout();           // 数值位数变了，宽度要重排
            var bmp = Snap(w.Content as Visual);
            w.Close();
            return bmp;
        }

        /// <summary>控制面板：与用户实际打开时完全一样的视觉树（显示项全开，配图才看得出两列布局）。</summary>
        private static Bitmap RenderPanel()
        {
            return RenderPanel(ThemeManager.Light);
        }

        private static Bitmap RenderPanel(string theme)
        {
            string prev = ThemeManager.Effective;
            ThemeManager.Apply(theme);
            try
            {
                return RenderPanelCore(theme);
            }
            finally
            {
                ThemeManager.Apply(prev);
            }
        }

        private static Bitmap RenderPanelCore(string theme)
        {
            // 配图必须可复现、且不带任何本机痕迹：刻意从 new AppSettings() 起，
            // 不读 SettingsStore —— 否则用户的热键（如 Ctrl+F）、开机自启勾选、
            // 字号档位会跟着进图，既不可复现也等于把本机配置贴到公开仓库里。
            var s = new AppSettings
            {
                ShowCpu = true, ShowMemory = true, ShowFps = true, ShowLowFps = true, ShowVram = true,
                ShowClock = true, ShowGpu = true, ShowCpuTemp = true, ShowGpuTemp = true,
                Hotkey = "",
                StartWithWindows = false,
                SizeMode = "S",
                RefreshMs = 1000,
                TextColor = "#FFFFFF",
                ShowLabels = true,
                ShowOutline = true,
                Opacity = 1.0,
                LockPosition = false,
                ClickThrough = false,
                ShowTrayIcon = true,
                OverlayVisible = true,
                // 主题分段按钮要跟实际配色一致：深色图里点亮「浅色」会自相矛盾
                Theme = theme,
                FirstRunDone = true
            };

            var w = new SettingsWindow();
            w.Bind(s);
            w.ApplyShotState(false);   // 自启关、帧采集显示正常态：图可复现且不带本机状态
            w.ShowActivated = false;
            w.Opacity = 0;
            w.MoveOffscreen();
            w.Show();
            w.UpdateLayout();

            var bmp = Snap(w.Content as Visual);
            w.CloseForShutdown();
            return bmp;
        }

        private static Bitmap Snap(Visual v)
        {
            if (v == null) throw new InvalidOperationException("视觉树为空");
            var fe = v as FrameworkElement;
            if (fe == null) throw new InvalidOperationException("视觉树不是 FrameworkElement");

            int pw = Math.Max(1, (int)Math.Ceiling(fe.ActualWidth * Scale));
            int ph = Math.Max(1, (int)Math.Ceiling(fe.ActualHeight * Scale));

            var rtb = new RenderTargetBitmap(pw, ph, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            rtb.Render(v);

            var bmp = new Bitmap(pw, ph, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, pw, ph), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                rtb.CopyPixels(new Int32Rect(0, 0, pw, ph), data.Scan0, data.Stride * ph, data.Stride);
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        // ------------------------------------------------------------------

        private static Bitmap ComposeHero(Bitmap panel, Bitmap overlay)
        {
            const int pad = 30;
            const int gap = 24;
            int panelW = 780;
            int panelH = (int)Math.Round(panel.Height * (double)panelW / panel.Width);
            int rightW = 900;

            int blockH = Math.Max((int)(rightW * 0.30), overlay.Height + 64);
            int bodyH = Math.Max(panelH, blockH * 2 + gap);

            int totalW = pad * 2 + panelW + gap + rightW;
            int totalH = pad * 2 + bodyH;

            var canvas = new Bitmap(totalW, totalH, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
                g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
                g.Clear(CanvasBackdrop);

                // 左：控制面板（白底 + 细边框）
                var panelRect = new Rectangle(pad, pad + (bodyH - panelH) / 2, panelW, panelH);
                FillRounded(g, panelRect, Color.White, Color.FromArgb(0xD5, 0xE2, 0xF2), 8);
                g.DrawImage(panel, Inside(panelRect, 1));

                // 右侧两块整体在 bodyH 内垂直居中，和左边的面板上下对齐
                int rightTop = pad + (bodyH - (blockH * 2 + gap)) / 2;

                // 右上：深色背景
                var darkRect = new Rectangle(pad + panelW + gap, rightTop, rightW, blockH);
                DrawStage(g, darkRect, DarkBackdrop, overlay, "深色背景");

                // 右下：浅色背景
                var lightRect = new Rectangle(pad + panelW + gap, rightTop + blockH + gap, rightW, blockH);
                DrawStage(g, lightRect, LightBackdrop, overlay, "浅色背景");
            }
            return canvas;
        }

        /// <summary>把浮窗按「右上角贴边」的观感摆进背景块。</summary>
        private static void DrawStage(Graphics g, Rectangle rect, Color backdrop, Bitmap overlay, string label)
        {
            FillRounded(g, rect, backdrop, Color.FromArgb(30, 90, 110, 140), 8);

            double k = Math.Min(1.0, (rect.Width * 0.68) / overlay.Width);
            int ow = (int)Math.Round(overlay.Width * k);
            int oh = (int)Math.Round(overlay.Height * k);
            int ox = rect.Right - 30 - ow;
            int oy = rect.Top + (rect.Height - oh) / 2;

            g.DrawImage(overlay, new Rectangle(ox, oy, ow, oh));
            Label(g, rect, label, backdrop.GetBrightness() < 0.45);
        }

        private static void Label(Graphics g, Rectangle rect, string text, bool onDark)
        {
            using (var f = new Font("Microsoft YaHei UI", 11f * Scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(onDark
                ? Color.FromArgb(110, 255, 255, 255)
                : Color.FromArgb(140, 90, 110, 140)))
            {
                var sz = g.MeasureString(text, f);
                g.DrawString(text, f, b, rect.Left + 16, rect.Bottom - sz.Height - 8);
            }
        }

        private static void Save(Bitmap content, string path, Color backdrop)
        {
            int m = 48;
            using (var bmp = new Bitmap(content.Width + m * 2, content.Height + m * 2, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(backdrop);
                    g.DrawImage(content, m, m);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        // ------------------------------------------------------------------

        private static Rectangle Inside(Rectangle r, int inset)
        {
            return new Rectangle(r.X + inset, r.Y + inset, r.Width - inset * 2, r.Height - inset * 2);
        }

        private static void FillRounded(Graphics g, Rectangle r, Color fill, Color border, int radius)
        {
            using (var path = Rounded(r, radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border.A > 0)
                {
                    using (var p = new Pen(border, 1f)) g.DrawPath(p, path);
                }
            }
        }

        private static Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new Drawing2D.GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
