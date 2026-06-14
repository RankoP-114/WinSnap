using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WinSnap.Interop;

/// <summary>
/// 把 <see cref="CapturedImage"/> 写入系统剪贴板，最大化与第三方应用（微信/QQ/Office/浏览器/
/// Claude Code 等 Chromium/Electron 系）粘贴的兼容性。
/// <para>
/// 关键设计：
/// ① 主格式写自己手写的 <c>CF_DIBV5</c>（BITMAPV5HEADER、bottom-up、BI_BITFIELDS、
///    AlphaMask=0xFF000000、sRGB）——这是 Chromium 写/读剪贴板图片用的标准形态。
///    <b>不写 CF_DIB</b>：否则 Windows 会用 CF_DIB 反向合成并覆盖我们的 CF_DIBV5
///    （使 AlphaMask 退化为 0），让 Chromium 系读取失败。系统会自动从我们的 V5 合成 CF_DIB。
/// ② <c>CF_BITMAP</c>：写入后由系统接管 HBITMAP 所有权，不可删除。
/// ③ <c>CF_HDROP</c>：附带临时 PNG 文件路径，供"粘贴文件"的应用。
/// </para>
/// 本类不依赖 WPF，仅使用 Win32 + System.Drawing。
/// </summary>
public static class ClipboardHelper
{
    private const uint CF_BITMAP = 2;
    private const uint CF_DIBV5 = 17;
    private const uint CF_HDROP = 15;

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const uint GHND = GMEM_MOVEABLE | GMEM_ZEROINIT;

    /// <summary>把图像写入剪贴板（CF_DIBV5 + CF_BITMAP + 可选 CF_HDROP）。线程须为 STA。</summary>
    public static void CopyImage(CapturedImage image, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] dibV5 = EncodeToDibV5(image);

        if (!OpenClipboardWithRetry(IntPtr.Zero))
            throw new InvalidOperationException("无法打开剪贴板（可能被其它进程占用）。");

        try
        {
            EmptyClipboard();
            // 只写 CF_DIBV5（GlobalAlloc 内存数据，跨进程可靠）；系统据此自动合成 CF_DIB 与 CF_BITMAP。
            // 关键：绝不自己写 CF_BITMAP —— 自写的 HBITMAP 是本进程 GDI 句柄，跨进程读取时失效，
            // 而 CF_BITMAP 优先级最高，会让 Clipboard.GetImage()（Claude Code 等据此读图）直接返回 NULL。
            SetByteBufferFormat(CF_DIBV5, dibV5);
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                SetHDropFormat(filePath);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>把单个文件作为 CF_HDROP 写入剪贴板，供聊天软件/资源管理器粘贴为文件。</summary>
    public static void CopyFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("要复制到剪贴板的文件不存在。", filePath);

        if (!OpenClipboardWithRetry(IntPtr.Zero))
            throw new InvalidOperationException("无法打开剪贴板（可能被其它进程占用）。");

        try
        {
            EmptyClipboard();
            SetHDropFormat(filePath);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>将图像编码为 PNG 字节（BGRA → 不透明 Bitmap → PNG）。</summary>
    public static byte[] EncodePng(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using Bitmap bmp = ToOpaqueBitmap(image);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------------
    // 内部实现
    // ---------------------------------------------------------------------

    /// <summary>
    /// 手写 CF_DIBV5：BITMAPV5HEADER(124B) + 32bpp BGRA 像素（bottom-up、Alpha=255）。
    /// BI_BITFIELDS + AlphaMask=0xFF000000 + sRGB —— Chromium/Electron 读剪贴板图的标准形态。
    /// </summary>
    private static byte[] EncodeToDibV5(CapturedImage image)
    {
        int w = image.Width, h = image.Height;
        int pixelBytes = w * h * 4;
        const int headerSize = 124;
        var buffer = new byte[headerSize + pixelBytes];

        using (var ms = new MemoryStream(buffer))
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(headerSize);       // bV5Size
            bw.Write(w);                // bV5Width
            bw.Write(h);                // bV5Height（正 = bottom-up）
            bw.Write((ushort)1);        // bV5Planes
            bw.Write((ushort)32);       // bV5BitCount
            bw.Write(3u);               // bV5Compression = BI_BITFIELDS
            bw.Write((uint)pixelBytes); // bV5SizeImage
            bw.Write(2835);             // bV5XPelsPerMeter
            bw.Write(2835);             // bV5YPelsPerMeter
            bw.Write(0u);               // bV5ClrUsed
            bw.Write(0u);               // bV5ClrImportant
            bw.Write(0x00FF0000u);      // bV5RedMask
            bw.Write(0x0000FF00u);      // bV5GreenMask
            bw.Write(0x000000FFu);      // bV5BlueMask
            bw.Write(0xFF000000u);      // bV5AlphaMask
            bw.Write(0x73524742u);      // bV5CSType = LCS_sRGB
            bw.Write(new byte[36]);     // bV5Endpoints
            bw.Write(0u);               // bV5GammaRed
            bw.Write(0u);               // bV5GammaGreen
            bw.Write(0u);               // bV5GammaBlue
            bw.Write(4u);               // bV5Intent = LCS_GM_IMAGES
            bw.Write(0u);               // bV5ProfileData
            bw.Write(0u);               // bV5ProfileSize
            bw.Write(0u);               // bV5Reserved
        }

        // 像素：bottom-up（逐行倒置），Alpha 统一置 255。
        var src = image.PixelsBgra;
        int stride = w * 4;
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src, (h - 1 - y) * stride, buffer, headerSize + y * stride, stride);
        for (int i = headerSize + 3; i < buffer.Length; i += 4)
            buffer[i] = 0xFF;

        return buffer;
    }

    /// <summary>把 32bpp BGRA(top-down, Alpha 未定义) 转为不透明 Bitmap（Format32bppRgb，Alpha=255）。</summary>
    private static Bitmap ToOpaqueBitmap(CapturedImage image)
    {
        int width = image.Width, height = image.Height, stride = image.Stride;
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var rect = new Rectangle(0, 0, width, height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            byte[] src = image.PixelsBgra;
            int rowBytes = width * 4;
            byte[] row = new byte[rowBytes];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(src, y * stride, row, 0, rowBytes);
                for (int i = 3; i < rowBytes; i += 4)
                    row[i] = 0xFF;
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private static void SetByteBufferFormat(uint format, byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return;

        IntPtr hGlobal = GlobalAlloc(GHND, (UIntPtr)bytes.Length);
        if (hGlobal == IntPtr.Zero)
            return;

        bool ownershipTransferred = false;
        try
        {
            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
                return;
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(format, hGlobal) != IntPtr.Zero)
                ownershipTransferred = true;
        }
        finally
        {
            if (!ownershipTransferred)
                GlobalFree(hGlobal);
        }
    }

    private static void SetBitmapFormat(Bitmap bmp)
    {
        // CF_BITMAP：SetClipboardData 成功后系统接管 HBITMAP 所有权，绝不能再删除，否则位图失效。
        IntPtr hBitmap = bmp.GetHbitmap();
        if (SetClipboardData(CF_BITMAP, hBitmap) == IntPtr.Zero)
            DeleteObject(hBitmap);
    }

    /// <summary>把单个文件路径写入 CF_HDROP（DROPFILES 头 + 宽字符路径 + 双 null 结尾）。</summary>
    private static void SetHDropFormat(string filePath)
    {
        byte[] path = System.Text.Encoding.Unicode.GetBytes(filePath + "\0\0");
        int total = 20 + path.Length;
        IntPtr hGlobal = GlobalAlloc(GHND, (UIntPtr)total);
        if (hGlobal == IntPtr.Zero)
            return;

        bool ownershipTransferred = false;
        try
        {
            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
                return;
            try
            {
                Marshal.WriteInt32(ptr, 0, 20);
                Marshal.WriteInt32(ptr, 4, 0);
                Marshal.WriteInt32(ptr, 8, 0);
                Marshal.WriteInt32(ptr, 12, 0);
                Marshal.WriteInt32(ptr, 16, 1); // fWide = 1
                Marshal.Copy(path, 0, ptr + 20, path.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CF_HDROP, hGlobal) != IntPtr.Zero)
                ownershipTransferred = true;
        }
        finally
        {
            if (!ownershipTransferred)
                GlobalFree(hGlobal);
        }
    }

    private static bool OpenClipboardWithRetry(IntPtr hWndNewOwner)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(hWndNewOwner))
                return true;
            Thread.Sleep(10);
        }
        return false;
    }

    // ---------------------------------------------------------------------
    // P/Invoke
    // ---------------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);
}
