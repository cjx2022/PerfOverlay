using System.Windows;
using System.Windows.Media;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 全程序唯一的字体来源。
    ///
    /// 为什么必须集中：界面里有两套排版路径，一套是 WPF 布局（TextBlock / CheckBox，取
    /// Themes/Styles.xaml 的 UiFont），一套是自己用 FormattedText 量宽再排（浮窗描边文字、
    /// 各个 --measure 探针）。两套一旦取到不同字体，探针量出来的「这行字放得下」就和屏幕上
    /// 真实渲染不是一回事 —— 断言全绿而实际被裁掉，这种假绿最难查。
    ///
    /// 字体链：微软雅黑优先，最后才回落到 Segoe UI（只在雅黑被裁掉的精简系统上才会用到）。
    /// 顺序不能反：写成 "Segoe UI, Microsoft YaHei UI" 时，数字与拉丁字母会走 Segoe UI，
    /// 中文走雅黑，同一行里两种字形混排，视觉上字重和字面高度都对不齐。
    /// </summary>
    internal static class UiFonts
    {
        /// <summary>与 Themes/Styles.xaml 里的 UiFont 键**必须逐字相同**（XAML 引不了 C# 常量）。</summary>
        public const string FamilyList = "Microsoft YaHei UI, Microsoft YaHei, Segoe UI";

        public static readonly FontFamily Family = new FontFamily(FamilyList);

        public static Typeface Typeface(FontWeight weight)
        {
            return new System.Windows.Media.Typeface(Family, FontStyles.Normal, weight, FontStretches.Normal);
        }
    }
}
