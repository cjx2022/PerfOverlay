using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PerfOverlay.Views;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 主题体检：把「深色主题看着对不对」变成数值断言，而不是靠眼睛扫截图。
    ///
    /// 四项检查，任何一项不过就返回非 0：
    ///   1) 两套主题字典的键集合完全一致（少一个键，那个控件就会退回系统默认黑白）；
    ///   2) 底色确实是深的 / 浅的（亮度阈值），防止「换了字典但页面还是白的」这种假成功；
    ///   3) 面板里每一段文字与它背后实际底色的对比度 —— 硬性线 3.0，4.5 以下记警告；
    ///   4) 托盘菜单调色板（WinForms 自绘取的是同一份颜色）文字对比度 ≥ 4.5。
    ///
    /// 为什么要遍历视觉树取「真实底色」：控件背景一层套一层（Window → Card → Grid），
    /// 只看 Window.Background 会把卡片里的文字当成压在页面底色上算，算出来的对比度是假的。
    /// </summary>
    public static class ThemeProbe
    {
        private const double HardMin = 3.0;    // 低于这条基本看不清，判失败
        private const double AaMin = 4.5;      // WCAG AA 正文
        private const int MinTextNodes = 25;   // 少于这个数说明遍历本身出了问题（假绿）

        public static int Run()
        {
            var sb = new StringBuilder();
            int fails = 0;

            sb.AppendLine("== 主题体检 ==");
            sb.AppendLine("系统偏好深色：" + (ThemeManager.SystemPrefersDark() ? "是" : "否"));

            string diff = ThemeManager.KeyDiff();
            sb.AppendLine(string.Format("字典键数：Light={0} Dark={1}  差异=[{2}]",
                ThemeManager.KeyCount(ThemeManager.Light),
                ThemeManager.KeyCount(ThemeManager.Dark),
                diff.Length == 0 ? "无" : diff));
            if (diff.Length > 0) { fails++; sb.AppendLine("  ✗ 两套主题的键不一致"); }

            foreach (string theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                bool dark = theme == ThemeManager.Dark;

                var page = ThemeManager.ColorOf("PageBg");
                double pageLum = Luminance(page);
                sb.AppendLine();
                sb.AppendLine(string.Format("-- {0}：PageBg=#{1:X2}{2:X2}{3:X2} 亮度={4:0.000}",
                    dark ? "深色" : "浅色", page.R, page.G, page.B, pageLum));

                bool lumOk = dark ? pageLum < 0.10 : pageLum > 0.70;
                sb.AppendLine("   底色方向：" + (lumOk ? "OK" : "✗ 不符合主题"));
                if (!lumOk) fails++;

                // ---- 控制面板 ----
                var w = new SettingsWindow();
                w.Bind(new AppSettings());
                w.Opacity = 0;
                w.MoveOffscreen();
                w.Show();
                w.UpdateLayout();

                int nodes = 0, hard = 0, warn = 0;
                double worst = 99;
                string worstDesc = "";

                foreach (var t in CollectText(w))
                {
                    Color fg = t.Item1, bg = t.Item2;
                    double ratio = Contrast(fg, bg);
                    nodes++;
                    if (ratio < worst) { worst = ratio; worstDesc = t.Item3; }
                    if (ratio < HardMin)
                    {
                        hard++;
                        if (hard <= 8)
                            sb.AppendLine(string.Format("   ✗ 对比度 {0:0.00}：{1}  字#{2:X2}{3:X2}{4:X2} 底#{5:X2}{6:X2}{7:X2}",
                                ratio, t.Item3, fg.R, fg.G, fg.B, bg.R, bg.G, bg.B));
                    }
                    else if (ratio < AaMin) warn++;
                }

                sb.AppendLine(string.Format("   面板文字节点 {0} 个：硬性不足 {1}，AA 未达 {2}，最低 {3:0.00}（{4}）",
                    nodes, hard, warn, worst, worstDesc));
                if (nodes < MinTextNodes) { fails++; sb.AppendLine("   ✗ 文字节点过少，遍历可能没跑起来（假绿）"); }
                if (hard > 0) fails++;

                // ---- 浮窗右键菜单（WPF ContextMenu，独立弹窗）----
                var ov = new OverlayWindow();
                ov.ApplySettings(new AppSettings(), true);
                ov.Opacity = 0;
                ov.Show();
                ov.UpdateLayout();
                ov.OpenMenuAt(20, 20);
                ov.UpdateLayout();

                int menuNodes = 0, menuHard = 0;
                double menuWorst = 99;
                foreach (var t in CollectText(ov.CtxMenuForProbe))
                {
                    menuNodes++;
                    double ratio = Contrast(t.Item1, t.Item2);
                    if (ratio < menuWorst) menuWorst = ratio;
                    if (ratio < HardMin) menuHard++;
                }
                sb.AppendLine(string.Format("   浮窗菜单文字 {0} 项：硬性不足 {1}，最低 {2:0.00}",
                    menuNodes, menuHard, menuWorst));
                if (menuNodes == 0 || menuHard > 0) fails++;

                ov.Close();

                // ---- 托盘菜单调色板（WinForms 自绘，颜色来自同一份主题字典）----
                var p = ThemeManager.MenuPalette.Current();
                double trayRatio = Contrast(Wpf(p.Text), Wpf(p.Back));
                double traySel = Contrast(Wpf(p.TextSelected), Wpf(p.Hover));
                sb.AppendLine(string.Format("   托盘菜单：文字/底色 {0:0.00}，选中/悬浮底 {1:0.00}",
                    trayRatio, traySel));
                if (trayRatio < AaMin || traySel < HardMin) { fails++; sb.AppendLine("   ✗ 托盘菜单配色对比度不足"); }

                w.CloseForShutdown();
            }

            // ---- 在线切换：同一个窗口不重建，直接把字典换掉，看颜色有没有跟着变 ----
            // 这一项才是这个功能的真身。前面两段都是「按主题建窗口」，窗口重建的话
            // 哪怕资源没换对也可能碰巧正确；只有就地切换能证明 DynamicResource 链路是通的。
            sb.AppendLine();
            sb.AppendLine("-- 在线切换（同一窗口，不重建）--");
            ThemeManager.Apply(ThemeManager.Light);
            var live = new SettingsWindow();
            live.Bind(new AppSettings());
            live.Opacity = 0;
            live.MoveOffscreen();
            live.Show();
            live.UpdateLayout();

            Color cBefore = FindTextColor(live, "CPU 使用率");
            ThemeManager.Apply(ThemeManager.Dark);
            live.UpdateLayout();
            Color cAfter = FindTextColor(live, "CPU 使用率");
            ThemeManager.Apply(ThemeManager.Light);
            live.UpdateLayout();
            Color cBack = FindTextColor(live, "CPU 使用率");

            ThemeManager.Apply(ThemeManager.Dark);
            Color darkToken = ThemeManager.ColorOf("TextPrimary");
            ThemeManager.Apply(ThemeManager.Light);
            Color lightToken = ThemeManager.ColorOf("TextPrimary");
            live.CloseForShutdown();

            bool toDark = Same(cAfter, darkToken) && !Same(cAfter, cBefore);
            bool toLight = Same(cBack, lightToken) && !Same(cBack, cAfter);
            sb.AppendLine(string.Format("   浅→深：{0} → {1}（深色 token {2}）{3}",
                Hex(cBefore), Hex(cAfter), Hex(darkToken), toDark ? "OK" : "✗"));
            sb.AppendLine(string.Format("   深→浅：{0} → {1}（浅色 token {2}）{3}",
                Hex(cAfter), Hex(cBack), Hex(lightToken), toLight ? "OK" : "✗"));
            if (!toDark || !toLight) fails++;

            ThemeManager.Apply(ThemeManager.Light);

            sb.AppendLine();
            sb.AppendLine(fails == 0 ? "RESULT: OK" : "RESULT: FAIL (" + fails + ")");

            string path = Path.Combine(Path.GetTempPath(), "perfoverlay-theme.txt");
            try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch (Exception) { }
            Console.WriteLine(sb.ToString());
            return fails == 0 ? 0 : 1;
        }

        // ==================================================================

        /// <summary>按文字内容找第一个可见 TextBlock 的前景色（在线切换断言用）。</summary>
        private static Color FindTextColor(DependencyObject root, string text)
        {
            if (root == null) return Colors.Transparent;
            var tb = root as TextBlock;
            if (tb != null && tb.Text == text && tb.Visibility == Visibility.Visible)
                return SolidColor(tb.Foreground);

            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                Color c = FindTextColor(VisualTreeHelper.GetChild(root, i), text);
                if (c.A != 0) return c;
            }
            return Colors.Transparent;
        }

        private static bool Same(Color a, Color b)
        {
            return a.R == b.R && a.G == b.G && a.B == b.B;
        }

        private static string Hex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        /// <summary>遍历视觉树，收集 (文字色, 实际底色, 描述)。</summary>
        private static List<Tuple<Color, Color, string>> CollectText(DependencyObject root)
        {
            var list = new List<Tuple<Color, Color, string>>();
            if (root == null) return list;
            Walk(root, list);
            return list;
        }

        private static void Walk(DependencyObject d, List<Tuple<Color, Color, string>> outList)
        {
            var tb = d as TextBlock;
            if (tb != null)
            {
                string text = tb.Text;
                bool hidden = tb.Visibility != Visibility.Visible || string.IsNullOrEmpty(text)
                              || tb.ActualWidth < 1 || tb.Opacity < 0.05;
                if (!hidden)
                {
                    Color fg = SolidColor(tb.Foreground);
                    Color bg;
                    if (DebugTarget != null && text == DebugTarget)
                    {
                        DebugChain = new StringBuilder();
                        TryFindBackground(tb, out bg);
                        DebugChain.Insert(0, "链（" + text + " 字#" + Describe(tb.Foreground) + "）：" + Environment.NewLine);
                    }
                    else if (TryFindBackground(tb, out bg))
                        outList.Add(Tuple.Create(fg, bg, "「" + Trim(text) + "」 " + SizeDesc(tb)));
                }
            }

            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), outList);
        }

        private static string Trim(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 14 ? s.Substring(0, 14) + "…" : s;
        }

        private static string SizeDesc(TextBlock tb)
        {
            return tb.FontSize.ToString("0") + "px" + (tb.FontWeight.ToOpenTypeWeight() >= 600 ? "/粗" : "");
        }

        /// <summary>从元素往上找第一个不透明的背景色（Border/Panel/Control 的 Background）。</summary>
        private static bool TryFindBackground(DependencyObject start, out Color color)
        {
            color = Colors.Transparent;
            var cur = start;
            for (int guard = 0; cur != null && guard < 64; guard++)
            {
                var fe = cur as FrameworkElement;
                if (fe != null && fe.Opacity < 0.05) return false;   // 整块是隐藏的，不算

                Brush b = null;
                var border = cur as Border;
                if (border != null) b = border.Background;
                if (b == null)
                {
                    var panel = cur as Panel;
                    if (panel != null) b = panel.Background;
                }
                if (b == null)
                {
                    var ctl = cur as Control;
                    if (ctl != null) b = ctl.Background;
                }

                if (DebugChain != null)
                    DebugChain.Append("    ↑ " + cur.GetType().Name + " bg=" + Describe(b) + Environment.NewLine);

                var solid = b as SolidColorBrush;
                if (solid != null && solid.Color.A >= 200)
                {
                    color = solid.Color;
                    return true;
                }

                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private static string Describe(Brush b)
        {
            if (b == null) return "(null)";
            var s = b as SolidColorBrush;
            if (s != null) return string.Format("A{0:X2} #{1:X2}{2:X2}{3:X2}", s.Color.A, s.Color.R, s.Color.G, s.Color.B);
            return b.GetType().Name;
        }

        /// <summary>排障开关：非 null 时把每次向上找底色的整条链记进来。</summary>
        internal static StringBuilder DebugChain;

        /// <summary>
        /// 排障开关：设了 PERFOVERLAY_THEME_DEBUG=&lt;某段文字&gt; 时，
        /// 会把它向上找底色的整条祖先链打进报告（判断「底色为什么算错」时用）。
        /// </summary>
        internal static string DebugTarget =
            Environment.GetEnvironmentVariable("PERFOVERLAY_THEME_DEBUG");

        private static Color SolidColor(Brush b)
        {
            var solid = b as SolidColorBrush;
            if (solid != null) return solid.Color;
            return Colors.Black;
        }

        private static Color Wpf(System.Drawing.Color c)
        {
            return Color.FromRgb(c.R, c.G, c.B);
        }

        private static double Luminance(Color c)
        {
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }

        private static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        private static double Contrast(Color a, Color b)
        {
            double l1 = Luminance(a), l2 = Luminance(b);
            if (l1 < l2) { double t = l1; l1 = l2; l2 = t; }
            return (l1 + 0.05) / (l2 + 0.05);
        }
    }
}
