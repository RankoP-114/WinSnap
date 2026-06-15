using System.Drawing;
using System.Drawing.Imaging;
using System.Buffers.Binary;
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
/// ② 不主动写 <c>CF_BITMAP</c>：自写 HBITMAP 跨进程兼容性差，且会干扰读取优先级；
/// ③ <c>CF_HDROP</c>：附带临时 PNG 文件路径，供"粘贴文件"的应用。
/// </para>
/// 本类不依赖 WPF，仅使用 Win32 + System.Drawing。
/// </summary>
public static class ClipboardHelper
{
    private const uint CF_DIBV5 = 17;
    private const uint CF_HDROP = 15;

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const uint GHND = GMEM_MOVEABLE | GMEM_ZEROINIT;

    public delegate void CopyBgraRow(int sourceRow, IntPtr destination, int destinationStride);

    /// <summary>把图像写入剪贴板（CF_DIBV5 + 可选 CF_HDROP）。线程须为 STA。</summary>
    public static void CopyImage(CapturedImage image, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (!OpenClipboardWithRetry(IntPtr.Zero))
            throw new InvalidOperationException("无法打开剪贴板（可能被其它进程占用）。");

        try
        {
            EmptyClipboard();
            // 只写 CF_DIBV5（GlobalAlloc 内存数据，跨进程可靠）；系统据此自动合成 CF_DIB 与 CF_BITMAP。
            // 关键：绝不自己写 CF_BITMAP —— 自写的 HBITMAP 是本进程 GDI 句柄，跨进程读取时失效，
            // 而 CF_BITMAP 优先级最高，会让 Clipboard.GetImage()（Claude Code 等据此读图）直接返回 NULL。
            SetDibV5Format(image);
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                SetHDropFormat(filePath);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// 从调用方提供的逐行 BGRA 写入器复制图像到剪贴板，避免先构造完整托管 <see cref="CapturedImage"/>。
    /// 行写入器接收 top-down 源行号，必须向目标指针写入 destinationStride 字节 BGRA 数据。
    /// </summary>
    public static void CopyImage(int width, int height, CopyBgraRow copyRow, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(copyRow);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        if (!OpenClipboardWithRetry(IntPtr.Zero))
            throw new InvalidOperationException("无法打开剪贴板（可能被其它进程占用）。");

        try
        {
            EmptyClipboard();
            SetDibV5Format(width, height, copyRow);
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

    /// <summary>
    /// 手写 CF_DIBV5：BITMAPV5HEADER(124B) + 32bpp BGRA 像素（bottom-up、Alpha=255）。
    /// 直接写入 GlobalAlloc 内存，避免为大图再分配一份托管 DIB 中间缓冲。
    /// </summary>
    private static unsafe void SetDibV5Format(CapturedImage image)
    {
        int width = image.Width;
        int height = image.Height;
        if (width <= 0 || height <= 0 || image.PixelsBgra.Length == 0)
            return;

        int stride = checked(width * 4);
        int pixelBytes = checked(stride * height);
        if (image.PixelsBgra.Length < pixelBytes)
            return;

        SetDibV5Format(width, height, CopyCapturedImageRow);
        return;

        void CopyCapturedImageRow(int sourceRow, IntPtr destination, int destinationStride)
        {
            int srcOffset = checked(sourceRow * image.Stride);
            Marshal.Copy(image.PixelsBgra, srcOffset, destination, Math.Min(destinationStride, image.Stride));
        }
    }

    private static unsafe void SetDibV5Format(int width, int height, CopyBgraRow copyRow)
    {
        const int headerSize = 124;
        int stride = checked(width * 4);
        int pixelBytes = checked(stride * height);
        int totalBytes = checked(headerSize + pixelBytes);

        IntPtr hGlobal = GlobalAlloc(GHND, (UIntPtr)totalBytes);
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
                var destination = new Span<byte>((void*)ptr, totalBytes);
                WriteDibV5Header(destination[..headerSize], width, height, pixelBytes);
                byte* dstBase = (byte*)ptr + headerSize;
                for (int y = 0; y < height; y++)
                {
                    byte* dstRow = dstBase + (y * stride);
                    copyRow(height - 1 - y, (IntPtr)dstRow, stride);
                    for (int a = 3; a < stride; a += 4)
                        dstRow[a] = 0xFF;
                }
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CF_DIBV5, hGlobal) != IntPtr.Zero)
                ownershipTransferred = true;
        }
        finally
        {
            if (!ownershipTransferred)
                GlobalFree(hGlobal);
        }
    }

    private static void WriteDibV5Header(Span<byte> header, int width, int height, int pixelBytes)
    {
        WriteInt32(header, 0, 124);             // bV5Size
        WriteInt32(header, 4, width);           // bV5Width
        WriteInt32(header, 8, height);          // bV5Height（正 = bottom-up）
        WriteUInt16(header, 12, 1);             // bV5Planes
        WriteUInt16(header, 14, 32);            // bV5BitCount
        WriteUInt32(header, 16, 3);             // bV5Compression = BI_BITFIELDS
        WriteUInt32(header, 20, (uint)pixelBytes);
        WriteInt32(header, 24, 2835);           // bV5XPelsPerMeter
        WriteInt32(header, 28, 2835);           // bV5YPelsPerMeter
        WriteUInt32(header, 40, 0x00FF0000);    // bV5RedMask
        WriteUInt32(header, 44, 0x0000FF00);    // bV5GreenMask
        WriteUInt32(header, 48, 0x000000FF);    // bV5BlueMask
        WriteUInt32(header, 52, 0xFF000000);    // bV5AlphaMask
        WriteUInt32(header, 56, 0x73524742);    // bV5CSType = LCS_sRGB
        WriteUInt32(header, 108, 4);            // bV5Intent = LCS_GM_IMAGES
    }

    private static void WriteInt32(Span<byte> destination, int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value);

    private static void WriteUInt16(Span<byte> destination, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], value);

    private static void WriteUInt32(Span<byte> destination, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], value);

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

}
