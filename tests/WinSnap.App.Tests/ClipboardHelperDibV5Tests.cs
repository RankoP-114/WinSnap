using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WinSnap.Interop;

namespace WinSnap.App.Tests;

public class ClipboardHelperDibV5Tests
{
    private const int HeaderSize = 124;

    [Fact]
    public void WriteDibV5_WritesExpectedBitmapV5Header()
    {
        byte[] dib = new byte[HeaderSize + (3 * 2 * 4)];

        ClipboardHelper.WriteDibV5(dib, 3, 2, WriteEmptyRow);

        Assert.Equal(124, ReadInt32(dib, 0));
        Assert.Equal(3, ReadInt32(dib, 4));
        Assert.Equal(2, ReadInt32(dib, 8));
        Assert.Equal((ushort)1, ReadUInt16(dib, 12));
        Assert.Equal((ushort)32, ReadUInt16(dib, 14));
        Assert.Equal(3u, ReadUInt32(dib, 16));
        Assert.Equal(24u, ReadUInt32(dib, 20));
        Assert.Equal(2835, ReadInt32(dib, 24));
        Assert.Equal(2835, ReadInt32(dib, 28));
        Assert.Equal(0u, ReadUInt32(dib, 32));
        Assert.Equal(0u, ReadUInt32(dib, 36));
        Assert.Equal(0x00FF0000u, ReadUInt32(dib, 40));
        Assert.Equal(0x0000FF00u, ReadUInt32(dib, 44));
        Assert.Equal(0x000000FFu, ReadUInt32(dib, 48));
        Assert.Equal(0xFF000000u, ReadUInt32(dib, 52));
        Assert.Equal(0x73524742u, ReadUInt32(dib, 56));
        Assert.Equal(4u, ReadUInt32(dib, 108));
    }

    [Fact]
    public void WriteDibV5_WritesPixelsBottomUpAndForcesOpaqueAlpha()
    {
        byte[] topDownBgra =
        [
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16,
        ];
        byte[] dib = new byte[HeaderSize + topDownBgra.Length];

        ClipboardHelper.WriteDibV5(
            dib,
            width: 2,
            height: 2,
            (sourceRow, destination, destinationStride) =>
                Marshal.Copy(topDownBgra, sourceRow * 8, destination, destinationStride));

        Assert.Equal(
            [
                9, 10, 11, 255,
                13, 14, 15, 255,
                1, 2, 3, 255,
                5, 6, 7, 255,
            ],
            dib[HeaderSize..]);
    }

    [Fact]
    public void WriteDibV5_RejectsUndersizedDestination()
    {
        byte[] dib = new byte[HeaderSize + 3];

        Assert.Throws<ArgumentException>(() =>
            ClipboardHelper.WriteDibV5(dib, 1, 1, WriteEmptyRow));
    }

    private static void WriteEmptyRow(int sourceRow, IntPtr destination, int destinationStride)
    {
        byte[] row = new byte[destinationStride];
        Marshal.Copy(row, 0, destination, destinationStride);
    }

    private static int ReadInt32(byte[] buffer, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));

    private static ushort ReadUInt16(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));

    private static uint ReadUInt32(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
}
