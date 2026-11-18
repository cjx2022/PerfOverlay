using System;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfOverlay.Services
{
    /// <summary>
    /// 界面里要用「同一个 logo」的地方（控制面板标题栏等）统一从这里取图。
    /// 资源就是 exe 内嵌的 tray-light / tray-dark 两套 ico，和托盘、文件图标同源 ——
    /// 另画一份 PNG 迟早会和 exe 图标长得不一样。
    /// </summary>
    public static class IconAssets
    {
        /// <summary>
        /// 挑一帧当 logo。挑法：**不小于目标像素的最小一帧**，都不够大就用最大的那帧。
        /// 直接取第 0 帧会拿到 16px，放大到 20+ DIP 就糊了（150% 缩放尤其明显）。
        /// 资源缺失返回 null，调用方按「没有 logo」处理，不影响其余布局。
        /// </summary>
        public static ImageSource Logo(bool dark, int wantPx)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string name = dark ? "PerfOverlay.tray-dark.ico" : "PerfOverlay.tray-light.ico";
                using (var st = asm.GetManifestResourceStream(name))
                {
                    if (st == null) return null;

                    var dec = new IconBitmapDecoder(st, BitmapCreateOptions.PreservePixelFormat,
                                                    BitmapCacheOption.OnLoad);
                    BitmapFrame best = null;
                    foreach (var f in dec.Frames)
                    {
                        if (best == null) { best = f; continue; }

                        bool fEnough = f.PixelWidth >= wantPx;
                        bool bEnough = best.PixelWidth >= wantPx;

                        if (fEnough && !bEnough) best = f;
                        else if (fEnough && bEnough && f.PixelWidth < best.PixelWidth) best = f;
                        else if (!fEnough && !bEnough && f.PixelWidth > best.PixelWidth) best = f;
                    }

                    if (best == null) return null;
                    if (best.CanFreeze) best.Freeze();
                    return best;
                }
            }
            catch (Exception) { return null; }
        }
    }
}
