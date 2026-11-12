using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 主题切换：把 Application.Resources 里的主题字典（Light.xaml / Dark.xaml）整份换掉。
    ///
    /// 为什么用「换字典」而不是「改每个控件的颜色」：
    ///   1) 颜色只有一处定义（Themes/Light.xaml），新加控件不用再写一套深色；
    ///   2) 所有样式都引用 DynamicResource，换字典后已经开着的窗口会自己重绘，
    ///      不需要重建窗口，也不用逐个控件赋值（那是最容易漏的写法）。
    ///
    /// 三条必须遵守的约束，违反任意一条都会出现「切了一半」：
    ///   · 主题字典必须是 MergedDictionaries 里的独立一项，且位于 Styles.xaml 之前；
    ///   · 引用颜色的地方一律 DynamicResource，StaticResource 会把值冻住；
    ///   · 动画目标值只能是数值，颜色状态用 Setter 表达。
    /// </summary>
    public static class ThemeManager
    {
        public const string Light = "light";
        public const string Dark = "dark";
        public const string Auto = "auto";

        /// <summary>当前生效的主题（Auto 已解析成 light / dark）。</summary>
        public static string Effective { get; private set; }

        static ThemeManager()
        {
            Effective = Light;
        }

        public static bool IsDark
        {
            get { return Effective == Dark; }
        }

        /// <summary>
        /// 按配置里的主题名应用。theme 取 light / dark / auto，非法值当 light。
        /// 返回实际生效的主题。
        /// </summary>
        public static string Apply(string theme)
        {
            string want = Resolve(theme);

            var app = Application.Current;
            if (app == null) { Effective = want; return Effective; }

            var merged = app.Resources.MergedDictionaries;

            // 已经是这个主题就什么都不做：面板切主题会先自己 Apply 一次、再经
            // SettingsChanged 绕回 App 一次，不去重就是同一件事做两遍（整窗重绘两次）。
            if (Effective == want && IsLoaded(want)) return Effective;

            var target = new ResourceDictionary
            {
                Source = new Uri(want == Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
            };
            int slot = -1;
            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source;
                if (src == null) continue;
                string s = src.OriginalString.Replace('\\', '/');
                if (s.IndexOf("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    slot = i;
                    break;
                }
            }

            if (slot >= 0)
            {
                // 先把新字典插到旧字典**前面**再删旧的：中间不留一刻「空档」，
                // 否则 DynamicResource 在这瞬间会解析失败、控件退回黑字黑底闪一下。
                merged.Insert(slot, target);
                merged.RemoveAt(slot + 1);
            }
            else
            {
                merged.Insert(0, target);
            }

            Effective = want;
            return Effective;
        }

        /// <summary>应用资源里当前合并的是不是这一套主题字典。</summary>
        private static bool IsLoaded(string theme)
        {
            try
            {
                string name = theme == Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
                foreach (var d in Application.Current.Resources.MergedDictionaries)
                {
                    if (d.Source == null) continue;
                    if (d.Source.OriginalString.Replace('\\', '/')
                            .IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>light / dark 原样返回；auto 读系统「应用模式」。</summary>
        public static string Resolve(string theme)
        {
            if (string.IsNullOrEmpty(theme)) return Light;
            string t = theme.Trim().ToLowerInvariant();
            if (t == Dark) return Dark;
            if (t == Light) return Light;
            if (t == Auto) return SystemPrefersDark() ? Dark : Light;
            return Light;
        }

        /// <summary>
        /// 系统是否偏好深色。只看「应用」模式（AppsUseLightTheme），不看任务栏那项 ——
        /// 用户把任务栏设成深色、窗口仍用浅色是很常见的组合。
        /// 读不到就按浅色处理：读注册表失败不该让界面变样。
        /// </summary>
        public static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("AppsUseLightTheme");
                    if (v == null) return false;
                    return Convert.ToInt32(v) == 0;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// 任务栏（系统 UI）是否深色 —— 托盘图标跟这一个，不跟 AppsUseLightTheme。
        ///
        /// 这两项是注册表里**两个独立的值**，用户完全可以把窗口设成浅色、任务栏设成深色：
        ///   AppsUseLightTheme   → 窗口、控件、我们的面板（见 SystemPrefersDark）
        ///   SystemUsesLightTheme → 任务栏、开始菜单、系统弹窗（托盘图标就活在这块背景上）
        /// 图标要的是「在我背后那块底色上看得清」，所以只认后者。
        /// 读不到按浅色处理：宁可给浅色版，也不要让图标在浅色任务栏上糊成一块。
        /// </summary>
        public static bool SystemTrayPrefersDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("SystemUsesLightTheme");
                    if (v == null) return false;
                    return Convert.ToInt32(v) == 0;
                }
            }
            catch (Exception) { }
            return false;
        }

        // ==================================================================
        // 给 WinForms 托盘菜单取色
        //
        // 托盘菜单是 NotifyIcon + ContextMenuStrip（WinForms），读不到 WPF 的
        // DynamicResource，所以这里从应用资源里把同一份 Brush 取出来换算成 GDI 颜色。
        // 好处是只有一处定义：改 Dark.xaml 就能同时改到 WPF 面板与托盘菜单。
        // ==================================================================

        public static System.Drawing.Color MenuColor(string key, System.Drawing.Color fallback)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return fallback;
                var brush = app.TryFindResource(key) as SolidColorBrush;
                if (brush == null) return fallback;
                var c = brush.Color;
                return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
            catch (Exception) { return fallback; }
        }

        /// <summary>托盘菜单自绘用的整套颜色，换主题时重新取一次。</summary>
        public sealed class MenuPalette
        {
            public System.Drawing.Color Back;
            public System.Drawing.Color Border;
            public System.Drawing.Color Text;
            public System.Drawing.Color TextDisabled;
            public System.Drawing.Color TextSelected;
            public System.Drawing.Color Hover;
            public System.Drawing.Color Separator;

            public static MenuPalette Current()
            {
                bool dark = IsDark;
                return new MenuPalette
                {
                    Back = MenuColor("MenuBg", dark ? System.Drawing.Color.FromArgb(0x18, 0x20, 0x29) : System.Drawing.Color.White),
                    Border = MenuColor("MenuBorder", dark ? System.Drawing.Color.FromArgb(0x2A, 0x35, 0x44) : System.Drawing.Color.FromArgb(0xE2, 0xEC, 0xFA)),
                    Text = MenuColor("TextPrimary", dark ? System.Drawing.Color.FromArgb(0xE6, 0xED, 0xF7) : System.Drawing.Color.FromArgb(0x18, 0x2A, 0x40)),
                    TextDisabled = MenuColor("TextHint", dark ? System.Drawing.Color.FromArgb(0x84, 0x97, 0xAE) : System.Drawing.Color.FromArgb(0x9A, 0xAE, 0xC6)),
                    TextSelected = MenuColor("AccentText", dark ? System.Drawing.Color.FromArgb(0x82, 0xB0, 0xFF) : System.Drawing.Color.FromArgb(0x2C, 0x6B, 0xF0)),
                    Hover = MenuColor("MenuHover", dark ? System.Drawing.Color.FromArgb(0x21, 0x2C, 0x3C) : System.Drawing.Color.FromArgb(0xE9, 0xF0, 0xFE)),
                    Separator = MenuColor("MenuSeparator", dark ? System.Drawing.Color.FromArgb(0x2A, 0x35, 0x44) : System.Drawing.Color.FromArgb(0xED, 0xF2, 0xFA))
                };
            }
        }

        // ==================================================================
        // 自检用：两套字典的键必须完全一致，否则切过去有键缺失 → 控件退回默认黑白
        // ==================================================================

        /// <summary>返回两套主题字典的键差异描述；完全一致返回空串。</summary>
        public static string KeyDiff()
        {
            var light = LoadKeys(Light);
            var dark = LoadKeys(Dark);
            if (light.Count == 0 || dark.Count == 0) return "主题字典加载失败";

            var missingInDark = new System.Collections.Generic.List<string>();
            var missingInLight = new System.Collections.Generic.List<string>();
            foreach (string k in light) if (!dark.Contains(k)) missingInDark.Add(k);
            foreach (string k in dark) if (!light.Contains(k)) missingInLight.Add(k);

            if (missingInDark.Count == 0 && missingInLight.Count == 0) return "";
            return (missingInDark.Count > 0 ? "Dark 缺: " + string.Join(",", missingInDark.ToArray()) + "  " : "")
                 + (missingInLight.Count > 0 ? "Light 缺: " + string.Join(",", missingInLight.ToArray()) : "");
        }

        /// <summary>键的数量（自检报告用）。</summary>
        public static int KeyCount(string theme)
        {
            return LoadKeys(theme).Count;
        }

        private static System.Collections.Generic.List<string> LoadKeys(string theme)
        {
            var keys = new System.Collections.Generic.List<string>();
            try
            {
                var d = new ResourceDictionary
                {
                    Source = new Uri(theme == Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
                };
                foreach (object k in d.Keys) keys.Add(Convert.ToString(k));
            }
            catch (Exception) { }
            return keys;
        }

        /// <summary>取当前主题下的 Color（自检做对比度计算用）。</summary>
        public static Color ColorOf(string key)
        {
            try
            {
                var brush = Application.Current != null ? Application.Current.TryFindResource(key) as SolidColorBrush : null;
                if (brush != null) return brush.Color;
            }
            catch (Exception) { }
            return Colors.Transparent;
        }
    }
}
