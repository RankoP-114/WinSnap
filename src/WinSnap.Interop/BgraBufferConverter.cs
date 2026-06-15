using System.Runtime.InteropServices;

namespace WinSnap.Interop;

internal static class BgraBufferConverter
{
    public static void CopyBgra8ToOpaqueBgra(
        ReadOnlySpan<byte> source,
        int sourceStride,
        Span<byte> destination,
        int width,
        int height)
    {
        ThrowIfBigEndian();
        int rowBytes = ValidateCopyArguments(source, sourceStride, destination, width, height);

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> srcRow = source.Slice(y * sourceStride, rowBytes);
            Span<byte> dstRow = destination.Slice(y * rowBytes, rowBytes);
            ReadOnlySpan<uint> srcPixels = MemoryMarshal.Cast<byte, uint>(srcRow);
            Span<uint> dstPixels = MemoryMarshal.Cast<byte, uint>(dstRow);

            for (int i = 0; i < srcPixels.Length; i++)
                dstPixels[i] = srcPixels[i] | 0xFF000000u;
        }
    }

    public static void CopyRgba8ToOpaqueBgra(
        ReadOnlySpan<byte> source,
        int sourceStride,
        Span<byte> destination,
        int width,
        int height)
    {
        ThrowIfBigEndian();
        int rowBytes = ValidateCopyArguments(source, sourceStride, destination, width, height);

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> srcRow = source.Slice(y * sourceStride, rowBytes);
            Span<byte> dstRow = destination.Slice(y * rowBytes, rowBytes);
            ReadOnlySpan<uint> srcPixels = MemoryMarshal.Cast<byte, uint>(srcRow);
            Span<uint> dstPixels = MemoryMarshal.Cast<byte, uint>(dstRow);

            for (int i = 0; i < srcPixels.Length; i++)
            {
                uint rgba = srcPixels[i];
                dstPixels[i] = 0xFF000000u
                               | ((rgba & 0x000000FFu) << 16)
                               | (rgba & 0x0000FF00u)
                               | ((rgba & 0x00FF0000u) >> 16);
            }
        }
    }

    private static int ValidateCopyArguments(
        ReadOnlySpan<byte> source,
        int sourceStride,
        Span<byte> destination,
        int width,
        int height)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));

        int rowBytes = checked(width * 4);
        if (sourceStride < rowBytes)
            throw new ArgumentOutOfRangeException(nameof(sourceStride), "源 stride 不能小于一行像素字节数。");

        int destinationBytes = checked(rowBytes * height);
        if (destination.Length < destinationBytes)
            throw new ArgumentException("目标缓冲区太小。", nameof(destination));

        int sourceBytes = height == 0 ? 0 : checked(((height - 1) * sourceStride) + rowBytes);
        if (source.Length < sourceBytes)
            throw new ArgumentException("源缓冲区太小。", nameof(source));

        return rowBytes;
    }

    private static void ThrowIfBigEndian()
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("BGRA/RGBA uint 像素转换仅支持 little-endian 平台。");
    }
}
