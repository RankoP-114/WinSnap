using System.Windows.Interop;
using Serilog;
using WinSnap.Interop;

namespace WinSnap.App.Hotkeys;

/// <summary>
/// 管理全局热键：创建 message-only 窗口接收 WM_HOTKEY，并把配置中的热键字符串
/// （如 "Ctrl+Alt+A"）解析为修饰键 + 虚拟键后注册。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HwndMessage = -3;
    private const int CaptureHotkeyId = 1;

    private HwndSource? _source;
    private string? _currentCaptureHotkey;

    /// <summary>截图热键被按下。</summary>
    public event Action? CaptureTriggered;

    /// <summary>创建 message-only 窗口并注册截图热键。返回热键是否注册成功。</summary>
    public bool Initialize(string captureHotkey)
    {
        var parameters = new HwndSourceParameters("WinSnapHotkeyWindow")
        {
            ParentWindow = new IntPtr(HwndMessage), // message-only 窗口
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        return RegisterCaptureHotkey(captureHotkey);
    }

    /// <summary>重新绑定截图热键（M9 设置界面用）。返回是否成功。</summary>
    public bool RegisterCaptureHotkey(string hotkey)
    {
        if (_source is null)
            return false;

        GlobalHotkey.Unregister(_source.Handle, CaptureHotkeyId);

        if (!TryParse(hotkey, out var mods, out var vk))
        {
            Log.Warning("无法解析热键字符串：{Hotkey}", hotkey);
            return false;
        }

        bool ok = GlobalHotkey.Register(_source.Handle, CaptureHotkeyId, mods | GlobalHotkey.ModNoRepeat, vk);
        if (ok)
        {
            _currentCaptureHotkey = hotkey;
            Log.Information("已注册截图热键：{Hotkey}", hotkey);
        }
        else
        {
            Log.Warning("截图热键注册失败（可能已被其它程序占用）：{Hotkey}", hotkey);
        }
        return ok;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == GlobalHotkey.WmHotkey && wParam.ToInt32() == CaptureHotkeyId)
        {
            handled = true;
            Log.Debug("截图热键触发：{Hotkey}", _currentCaptureHotkey);
            CaptureTriggered?.Invoke();
        }
        return IntPtr.Zero;
    }

    /// <summary>解析 "Ctrl+Alt+A" 形式的热键。主键支持 A-Z / 0-9 / F1-F24。</summary>
    private static bool TryParse(string hotkey, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(hotkey))
            return false;

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= GlobalHotkey.ModControl; break;
                case "alt": modifiers |= GlobalHotkey.ModAlt; break;
                case "shift": modifiers |= GlobalHotkey.ModShift; break;
                case "win" or "windows" or "meta": modifiers |= GlobalHotkey.ModWin; break;
                default: return false;
            }
        }

        var key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && (char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0])))
        {
            virtualKey = key[0]; // 'A'-'Z' / '0'-'9' 的虚拟键码等于其 ASCII 值
            return modifiers != 0; // 至少需要一个修饰键
        }
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + fn - 1); // VK_F1 = 0x70
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            GlobalHotkey.Unregister(_source.Handle, CaptureHotkeyId);
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
