using System.Reflection;
using System.Windows.Input;
using WinSnap.App.Settings;

namespace WinSnap.App.Tests;

public class HotkeyRecorderControlTests
{
    [Fact]
    public void TryFormatMainKey_DistinguishesMainDigitAndNumPadDigit()
    {
        Assert.True(TryFormatMainKey(Key.D1, out string mainDigit));
        Assert.True(TryFormatMainKey(Key.NumPad1, out string numPadDigit));

        Assert.Equal("1", mainDigit);
        Assert.Equal("NumPad1", numPadDigit);
    }

    [Fact]
    public void TryFormatMainKey_RejectsUnsupportedKeys()
    {
        Assert.False(TryFormatMainKey(Key.OemPlus, out string token));

        Assert.Empty(token);
    }

    private static bool TryFormatMainKey(Key key, out string token)
    {
        var method = typeof(HotkeyRecorderControl).GetMethod(
            "TryFormatMainKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        object?[] args = [key, string.Empty];
        bool result = Assert.IsType<bool>(method.Invoke(null, args));
        token = Assert.IsType<string>(args[1]);
        return result;
    }
}
