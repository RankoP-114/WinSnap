using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSnap.Interop;

namespace WinSnap.App.Imaging;

/// <summary>捕获数据与 WPF 位图之间的转换。</summary>
public static class ImagingHelper
{
    /// <summary>
    /// 把捕获的 BGRA 像素转为可冻结的 WPF 位图。
    /// 固定 96 DPI，按 Bgr32 不透明使用（忽略 GDI 抓取时未定义的 Alpha 通道）。
    /// </summary>
    public static BitmapSource ToBitmapSource(CapturedImage img)
    {
        var bmp = BitmapSource.Create(
            img.Width, img.Height,
            96, 96,
            PixelFormats.Bgr32, null,
            img.PixelsBgra, img.Stride);
        bmp.Freeze(); // 可跨线程、渲染更高效
        return bmp;
    }
}
