using System.Reflection;
using System.Runtime.InteropServices;
using WinSnap.Interop;

namespace WinSnap.App.Tests;

public class GdiCaptureLayoutTests
{
    [Fact]
    public void BitmapInfoHeader_UsesExpectedWin32Layout()
    {
        Type headerType = GetNestedType("BitmapInfoHeader");
        Type infoType = GetNestedType("BitmapInfo");

        Assert.Equal(40, Marshal.SizeOf(headerType));
        Assert.Equal(40, Marshal.SizeOf(infoType));
    }

    [Fact]
    public void CreateTopDownBgra_ProducesTopDown32BppHeader()
    {
        Type infoType = GetNestedType("BitmapInfo");
        Type headerType = GetNestedType("BitmapInfoHeader");
        var method = infoType.GetMethod("CreateTopDownBgra", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        object info = method.Invoke(null, [3, 2])!;
        object header = infoType.GetField("Header", BindingFlags.Public | BindingFlags.Instance)!.GetValue(info)!;

        Assert.Equal(40u, GetField<uint>(headerType, header, "Size"));
        Assert.Equal(3, GetField<int>(headerType, header, "Width"));
        Assert.Equal(-2, GetField<int>(headerType, header, "Height"));
        Assert.Equal((ushort)1, GetField<ushort>(headerType, header, "Planes"));
        Assert.Equal((ushort)32, GetField<ushort>(headerType, header, "BitCount"));
        Assert.Equal(0u, GetField<uint>(headerType, header, "Compression"));
        Assert.Equal(24u, GetField<uint>(headerType, header, "SizeImage"));
    }

    [Fact]
    public void CreateDIBSection_PInvokeUsesSetLastError()
    {
        var method = typeof(GdiCapture).GetMethod("CreateDIBSection", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var attribute = method.GetCustomAttribute<DllImportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("gdi32.dll", attribute.Value);
        Assert.True(attribute.SetLastError);
    }

    [Fact]
    public void CaptureRegionInto_RejectsTooSmallDestinationBeforeNativeCapture()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            GdiCapture.CaptureRegionInto(0, 0, 2, 2, new byte[15]));

        Assert.Equal("destinationBgra", ex.ParamName);
    }

    private static Type GetNestedType(string name)
    {
        var type = typeof(GdiCapture).GetNestedType(name, BindingFlags.NonPublic);
        Assert.NotNull(type);
        return type;
    }

    private static T GetField<T>(Type type, object instance, string name)
        => Assert.IsType<T>(type.GetField(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(instance));
}
