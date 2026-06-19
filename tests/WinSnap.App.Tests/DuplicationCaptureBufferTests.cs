using System.Reflection;
using System.Runtime.InteropServices;
using WinSnap.Interop;

namespace WinSnap.App.Tests;

public class DuplicationCaptureBufferTests
{
    [Fact]
    public void ReadBgra8_WritesIntoProvidedBufferAndForcesAlphaOpaque()
    {
        byte[] source =
        [
            0x10, 0x20, 0x30, 0x00,
            0x40, 0x50, 0x60, 0x7F,
        ];
        byte[] destination = Enumerable.Repeat((byte)0xCC, source.Length).ToArray();

        InvokeWithPinnedSource(
            "ReadBgra8",
            source,
            rowPitch: 8,
            width: 2,
            height: 1,
            destination);

        Assert.Equal([0x10, 0x20, 0x30, 0xFF, 0x40, 0x50, 0x60, 0xFF], destination);
    }

    [Fact]
    public void ReadRgba8AsBgra_WritesIntoProvidedBufferAndSwapsChannels()
    {
        byte[] source =
        [
            0x10, 0x20, 0x30, 0x00,
            0x40, 0x50, 0x60, 0x7F,
        ];
        byte[] destination = Enumerable.Repeat((byte)0xCC, source.Length).ToArray();

        InvokeWithPinnedSource(
            "ReadRgba8AsBgra",
            source,
            rowPitch: 8,
            width: 2,
            height: 1,
            destination);

        Assert.Equal([0x30, 0x20, 0x10, 0xFF, 0x60, 0x50, 0x40, 0xFF], destination);
    }

    [Fact]
    public void MapScRgbFp16ToSdrBgra_WritesIntoProvidedBuffer()
    {
        ushort[] source =
        [
            BitConverter.HalfToUInt16Bits((Half)1f),
            BitConverter.HalfToUInt16Bits((Half)0f),
            BitConverter.HalfToUInt16Bits((Half)0f),
            BitConverter.HalfToUInt16Bits((Half)1f),
        ];
        byte[] destination = [0xCC, 0xCC, 0xCC, 0xCC];

        var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            var method = GetPrivateMethod(
                "MapScRgbFp16ToSdrBgra",
                typeof(IntPtr),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(double),
                typeof(double),
                typeof(bool),
                typeof(byte[]));

            method.Invoke(null, [handle.AddrOfPinnedObject(), 8, 1, 1, 80d, 80d, false, destination]);
        }
        finally
        {
            handle.Free();
        }

        Assert.Equal([0x00, 0x00, 0xFF, 0xFF], destination);
    }

    [Fact]
    public void BuildGdiBaseCanvas_RejectsOversizedCanvasBeforeNativeCapture()
    {
        var method = GetPrivateMethod(
            "BuildGdiBaseCanvas",
            typeof(int),
            typeof(int),
            typeof(List<string>));

        var ex = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [50_000, 50_000, new List<string>()]));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    private static void InvokeWithPinnedSource(
        string methodName,
        byte[] source,
        int rowPitch,
        int width,
        int height,
        byte[] destination)
    {
        var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            var method = GetPrivateMethod(
                methodName,
                typeof(IntPtr),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(byte[]));

            method.Invoke(null, [handle.AddrOfPinnedObject(), rowPitch, width, height, destination]);
        }
        finally
        {
            handle.Free();
        }
    }

    private static MethodInfo GetPrivateMethod(string name, params Type[] parameterTypes)
    {
        var method = typeof(DuplicationCapture).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        Assert.NotNull(method);
        return method;
    }
}
