using WinSnap.App.Hotkeys;
using WinSnap.Interop;

namespace WinSnap.App.Tests;

public class HotkeyManagerTests
{
    [Fact]
    public void TryParse_DistinguishesMainDigitAndNumPadDigit()
    {
        Assert.True(HotkeyManager.TryParse("Ctrl+Alt+1", out uint mainMods, out uint mainVk));
        Assert.True(HotkeyManager.TryParse("Ctrl+Alt+NumPad1", out uint numPadMods, out uint numPadVk));

        Assert.Equal(GlobalHotkey.ModControl | GlobalHotkey.ModAlt, mainMods);
        Assert.Equal(mainMods, numPadMods);
        Assert.Equal((uint)'1', mainVk);
        Assert.Equal(0x61u, numPadVk);
    }

    [Fact]
    public void TryParse_RejectsHotkeyWithoutModifier()
    {
        Assert.False(HotkeyManager.TryParse("NumPad1", out _, out _));
        Assert.False(HotkeyManager.TryParse("A", out _, out _));
    }
}
