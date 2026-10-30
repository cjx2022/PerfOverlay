using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PerfOverlay.Controls
{
    /// <summary>
    /// 带真正描边的文字元素。
    ///
    /// 为什么不用 TextBlock + DropShadowEffect：阴影是高斯模糊，靠字形边缘一层很淡的
    /// 渐隐，遇到接近纯白的背景（实测桌面壁纸区域亮度 0.87）白字对比度只有 1.30:1，
    /// 等于看不见。改成「先把字形轮廓用黑色描一圈、再填白色」之后，字形四周是实心黑，
    /// 对比度直接到 21:1，任何底色上都读得清。
    ///
    /// 实现方式是取 FormattedText.BuildGeometry() 的字形轮廓，先用 Pen 描边（Pen 宽度取
    /// 目标描边宽度的 2 倍，因为 WPF 的描边是以路径为中心向两侧各扩一半），再填色覆盖内侧。
    ///
    /// 代价：形状绘制走灰度抗锯齿，不像 TextBlock 那样有 ClearType 次像素渲染。
    /// 在浮窗这种「压在任意画面上」的场景里，可读性远比次像素锐度重要，所以这样取舍。
    /// </summary>
    public sealed class OutlineText : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register("Text", typeof(string), typeof(OutlineText),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure
                                                | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register("FontSize", typeof(double), typeof(OutlineText),
                new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure
                                                   | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontWeightProperty =
            DependencyProperty.Register("FontWeight", typeof(FontWeight), typeof(OutlineText),
                new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure
                                                                | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register("Fill", typeof(Brush), typeof(OutlineText),
                new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty OutlineProperty =
            DependencyProperty.Register("Outline", typeof(Brush), typeof(OutlineText),
                new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty OutlineWidthProperty =
            DependencyProperty.Register("OutlineWidth", typeof(double), typeof(OutlineText),
                new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure
                                                 | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register("TextAlignment", typeof(TextAlignment), typeof(OutlineText),
                new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsRender));

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        public double FontSize
        {
            get { return (double)GetValue(FontSizeProperty); }
            set { SetValue(FontSizeProperty, value); }
        }

        public FontWeight FontWeight
        {
            get { return (FontWeight)GetValue(FontWeightProperty); }
            set { SetValue(FontWeightProperty, value); }
        }

        public Brush Fill
        {
            get { return (Brush)GetValue(FillProperty); }
            set { SetValue(FillProperty, value); }
        }

        public Brush Outline
        {
            get { return (Brush)GetValue(OutlineProperty); }
            set { SetValue(OutlineProperty, value); }
        }

        /// <summary>描边总厚度（向外扩出去的部分是它的一半）。0 = 不描边。</summary>
        public double OutlineWidth
        {
            get { return (double)GetValue(OutlineWidthProperty); }
            set { SetValue(OutlineWidthProperty, value); }
        }

        public TextAlignment TextAlignment
        {
            get { return (TextAlignment)GetValue(TextAlignmentProperty); }
            set { SetValue(TextAlignmentProperty, value); }
        }

        private static readonly FontFamily Face =
            new FontFamily("Segoe UI, Microsoft YaHei UI");

        // FormattedText 的构造要查字体、排版、算字宽，比看上去贵。
        // 每次 UpdateMetrics 都会走「Measure → Render」两遍，文本相同就没必要建两遍，
        // 所以按 (文本, 字号, 字重, DPI) 缓存最近一次的结果。
        private FormattedText _ft;
        private string _ftText;
        private double _ftSize;
        private double _ftPpd;
        private FontWeight _ftWeight;

        private FormattedText Build(double pixelsPerDip)
        {
            string text = Text ?? "";
            if (_ft != null && _ftText == text && _ftSize == FontSize
                && _ftPpd == pixelsPerDip && _ftWeight == FontWeight)
            {
                return _ft;
            }

            var tf = new Typeface(Face, FontStyles.Normal, FontWeight, FontStretches.Normal);
            _ft = new FormattedText(text,
                                    CultureInfo.CurrentUICulture,
                                    FlowDirection.LeftToRight,
                                    tf, FontSize, Brushes.White, pixelsPerDip);
            _ftText = text;
            _ftSize = FontSize;
            _ftPpd = pixelsPerDip;
            _ftWeight = FontWeight;
            return _ft;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double ppd = 1.0;
            try { ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch (Exception) { }

            var ft = Build(ppd);
            // 描边向外扩 OutlineWidth/2，测量值要把这一圈算进去，否则最后一位会被切
            double pad = Math.Max(0, OutlineWidth);
            return new Size(ft.WidthIncludingTrailingWhitespace + pad, ft.Height + pad);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (string.IsNullOrEmpty(Text)) return;

            double ppd = 1.0;
            try { ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch (Exception) { }

            var ft = Build(ppd);
            double pad = Math.Max(0, OutlineWidth) / 2.0;

            double textW = ft.WidthIncludingTrailingWhitespace;
            double x = pad;
            if (TextAlignment == TextAlignment.Right) x = ActualWidth - textW - pad;
            else if (TextAlignment == TextAlignment.Center) x = (ActualWidth - textW) / 2.0;
            if (x < 0) x = 0;

            var geo = ft.BuildGeometry(new Point(x, pad));
            if (geo == null) return;

            var outline = Outline;
            if (outline != null && OutlineWidth > 0)
            {
                var pen = new Pen(outline, OutlineWidth)
                {
                    LineJoin = PenLineJoin.Round,
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                pen.Freeze();
                dc.DrawGeometry(outline, pen, geo);
            }

            if (Fill != null) dc.DrawGeometry(Fill, null, geo);
        }
    }
}
