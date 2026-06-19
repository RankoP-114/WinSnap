using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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

            CopyBgraPixelsToOpaqueBgra(srcPixels, dstPixels);
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

            CopyRgbaPixelsToOpaqueBgra(srcPixels, dstPixels);
        }
    }

    private static void CopyBgraPixelsToOpaqueBgra(ReadOnlySpan<uint> source, Span<uint> destination)
    {
        int i = 0;
        if (Avx2.IsSupported)
        {
            var alpha = Vector256.Create(0xFF000000u);
            int vectorCount = Vector256<uint>.Count;
            for (; i <= source.Length - vectorCount; i += vectorCount)
            {
                var vector = MemoryMarshal.Read<Vector256<uint>>(MemoryMarshal.AsBytes(source.Slice(i, vectorCount)));
                var opaque = Avx2.Or(vector, alpha);
                MemoryMarshal.Write(MemoryMarshal.AsBytes(destination.Slice(i, vectorCount)), in opaque);
            }
        }

        if (Sse2.IsSupported)
        {
            var alpha = Vector128.Create(0xFF000000u);
            int vectorCount = Vector128<uint>.Count;
            for (; i <= source.Length - vectorCount; i += vectorCount)
            {
                var vector = MemoryMarshal.Read<Vector128<uint>>(MemoryMarshal.AsBytes(source.Slice(i, vectorCount)));
                var opaque = Sse2.Or(vector, alpha);
                MemoryMarshal.Write(MemoryMarshal.AsBytes(destination.Slice(i, vectorCount)), in opaque);
            }
        }

        for (; i < source.Length; i++)
            destination[i] = source[i] | 0xFF000000u;
    }

    private static void CopyRgbaPixelsToOpaqueBgra(ReadOnlySpan<uint> source, Span<uint> destination)
    {
        int i = 0;
        if (Avx2.IsSupported)
        {
            var redMask = Vector256.Create(0x000000FFu);
            var greenMask = Vector256.Create(0x0000FF00u);
            var blueMask = Vector256.Create(0x00FF0000u);
            var alpha = Vector256.Create(0xFF000000u);
            int vectorCount = Vector256<uint>.Count;
            for (; i <= source.Length - vectorCount; i += vectorCount)
            {
                var rgba = MemoryMarshal.Read<Vector256<uint>>(MemoryMarshal.AsBytes(source.Slice(i, vectorCount)));
                var red = Avx2.ShiftLeftLogical(Avx2.And(rgba, redMask), 16);
                var green = Avx2.And(rgba, greenMask);
                var blue = Avx2.ShiftRightLogical(Avx2.And(rgba, blueMask), 16);
                var bgra = Avx2.Or(alpha, Avx2.Or(green, Avx2.Or(red, blue)));
                MemoryMarshal.Write(MemoryMarshal.AsBytes(destination.Slice(i, vectorCount)), in bgra);
            }
        }

        if (Sse2.IsSupported)
        {
            var redMask = Vector128.Create(0x000000FFu);
            var greenMask = Vector128.Create(0x0000FF00u);
            var blueMask = Vector128.Create(0x00FF0000u);
            var alpha = Vector128.Create(0xFF000000u);
            int vectorCount = Vector128<uint>.Count;
            for (; i <= source.Length - vectorCount; i += vectorCount)
            {
                var rgba = MemoryMarshal.Read<Vector128<uint>>(MemoryMarshal.AsBytes(source.Slice(i, vectorCount)));
                var red = Sse2.ShiftLeftLogical(Sse2.And(rgba, redMask), 16);
                var green = Sse2.And(rgba, greenMask);
                var blue = Sse2.ShiftRightLogical(Sse2.And(rgba, blueMask), 16);
                var bgra = Sse2.Or(alpha, Sse2.Or(green, Sse2.Or(red, blue)));
                MemoryMarshal.Write(MemoryMarshal.AsBytes(destination.Slice(i, vectorCount)), in bgra);
            }
        }

        for (; i < source.Length; i++)
        {
            uint rgba = source[i];
            destination[i] = 0xFF000000u
                             | ((rgba & 0x000000FFu) << 16)
                             | (rgba & 0x0000FF00u)
                             | ((rgba & 0x00FF0000u) >> 16);
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
