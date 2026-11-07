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

            using (var overlay = RenderOverlay())
            using (var panel = RenderPanel())
            {
                var hero = ComposeHero(panel, overlay);
                string heroPath = Path.Combine(outDir, "screenshot.png");
                hero.Save(heroPath, ImageFormat.Png);
                hero.Dispose();

                // 顺带输出三张单独的图，README 里可以按需引用
                Save(overlay, Path.Combine(outDir, "overlay-dark.png"), DarkBackdrop);
                Save(overlay, Path.Combine(outDir, "overlay-light.png"), LightBackdrop);
                Save(panel, Path.Combine(outDir, "console.png"), CanvasBackdrop);
                return heroPath;
            }
        }

        // ------------------------------------------------------------------

        /// <summary>浮窗：八项指标全开（配图要展示全部能力）、中等字号、默认白字带描边，填一组示例数值。</summary>
        private static Bitmap RenderOverlay()
        {
            var s = SettingsStore.Load();
            s.ShowCpu = s.ShowMemory = s.ShowFps = s.ShowLowFps = s.ShowVram = true;
            s.ShowClock = s.ShowGpu = s.ShowTemp = true;
            s.SizeMode = "M";
            s.TextColor = "#FFFFFF";
            s.ShowOutline = true;
            s.Opacity = 1.0;
            s.OffsetX = s.OffsetY = 0;

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
                TempOk = true, TempCelsius = 64
            });

            w.UpdateLayout();           // 数值位数变了，宽度要重排
            var bmp = Snap(w.Content as Visual);
            w.Close();
            return bmp;
        }

        /// <summary>控制面板：与用户实际打开时完全一样的视觉树（显示项全开，配图才看得出两列布局）。</summary>
        private static Bitmap RenderPanel()
        {
            var s = SettingsStore.Load();
            s.ShowCpu = s.ShowMemory = s.ShowFps = s.ShowLowFps = s.ShowVram = true;
            s.ShowClock = s.ShowGpu = s.ShowTemp = true;

            var w = new SettingsWindow();
            w.Bind(s);
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
