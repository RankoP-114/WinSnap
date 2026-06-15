using WinSnap.Interop;

namespace WinSnap.App.Tests;

public class BgraBufferConverterTests
{
    [Fact]
    public void CopyBgra8ToOpaqueBgra_SkipsPaddingAndOverwritesAlpha()
    {
        byte[] source =
        [
            1, 2, 3, 4,
            5, 6, 7, 8,
            0xEE, 0xEE,
            9, 10, 11, 12,
            13, 14, 15, 16,
            0xDD, 0xDD,
        ];
        byte[] destination = new byte[16];

        BgraBufferConverter.CopyBgra8ToOpaqueBgra(
            source,
            sourceStride: 10,
            destination,
            width: 2,
            height: 2);

        Assert.Equal(
            [
                1, 2, 3, 255,
                5, 6, 7, 255,
                9, 10, 11, 255,
                13, 14, 15, 255,
            ],
            destination);
    }

    [Fact]
    public void CopyRgba8ToOpaqueBgra_SwapsChannelsSkipsPaddingAndOverwritesAlpha()
    {
        byte[] source =
        [
            1, 2, 3, 4,
            5, 6, 7, 8,
            0xEE, 0xEE,
            9, 10, 11, 12,
            13, 14, 15, 16,
            0xDD, 0xDD,
        ];
        byte[] destination = new byte[16];

        BgraBufferConverter.CopyRgba8ToOpaqueBgra(
            source,
            sourceStride: 10,
            destination,
            width: 2,
            height: 2);

        Assert.Equal(
            [
                3, 2, 1, 255,
                7, 6, 5, 255,
                11, 10, 9, 255,
                15, 14, 13, 255,
            ],
            destination);
    }

    [Fact]
    public void CopyBgra8ToOpaqueBgra_ZeroSizeDoesNothing()
    {
        byte[] destination = [1, 2, 3, 4];

        BgraBufferConverter.CopyBgra8ToOpaqueBgra(
            ReadOnlySpan<byte>.Empty,
            sourceStride: 0,
            destination,
            width: 0,
            height: 0);

        Assert.Equal([1, 2, 3, 4], destination);
    }

    [Fact]
    public void CopyBgra8ToOpaqueBgra_RejectsUndersizedSource()
    {
        byte[] source = [1, 2, 3];
        byte[] destination = new byte[4];

        Assert.Throws<ArgumentException>(() =>
            BgraBufferConverter.CopyBgra8ToOpaqueBgra(
                source,
                sourceStride: 4,
                destination,
                width: 1,
                height: 1));
    }

    [Fact]
    public void CopyBgra8ToOpaqueBgra_RejectsUndersizedDestination()
    {
        byte[] source = [1, 2, 3, 4];
        byte[] destination = new byte[3];

        Assert.Throws<ArgumentException>(() =>
            BgraBufferConverter.CopyBgra8ToOpaqueBgra(
                source,
                sourceStride: 4,
                destination,
                width: 1,
                height: 1));
    }
}
