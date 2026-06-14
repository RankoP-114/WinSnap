using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WinSnap.Interop;

/// <summary>
/// 基于 GDI BitBlt 的屏幕捕获（SDR 路径）。
/// 使用 <c>SRCCOPY | CAPTUREBLT</c> 以正确抓取 layered（半透明）窗口。
/// HDR 显示器请改用 Desktop Duplication 路径（见 M4）。
/// </summary>
public static class GdiCapture
{
    /// <summary>抓取整个虚拟桌面（所有显示器）。</summary>
    public static CapturedImage CaptureVirtualScreen()
    {
        var vs = VirtualScreenInfo.Get();
        return CaptureRegion(vs.X, vs.Y, vs.Width, vs.Height);
    }

    /// <summary>抓取虚拟桌面坐标系下的指定物理像素矩形。</summary>
    public static CapturedImage CaptureRegion(int x, int y, int width, int height)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
        IntPtr oldObj = NativeMethods.SelectObject(memDc, hBitmap);
        try
        {
            NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, x, y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            using var bmp = Image.FromHbitmap(hBitmap);
            return ToBgra(bmp);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, oldObj);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static CapturedImage ToBgra(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bmp.Width * 4;
            var buffer = new byte[stride * bmp.Height];
            // 源 stride 可能含行对齐填充，逐行紧凑复制
            for (int row = 0; row < bmp.Height; row++)
            {
                Marshal.Copy(data.Scan0 + row * data.Stride, buffer, row * stride, stride);
            }
            return new CapturedImage(bmp.Width, bmp.Height, buffer);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
