using System.Windows.Interop;
using Serilog;
using WinSnap.Core.Settings;
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
    private const int PinHotkeyId = 2;
    private const int GifCaptureHotkeyId = 3;

    private static readonly int[] HotkeyIds = [CaptureHotkeyId, PinHotkeyId, GifCaptureHotkeyId];

    private readonly Dictionary<int, string> _currentHotkeys = new();
    private HwndSource? _source;

    /// <summary>截图热键被按下。</summary>
    public event Action? CaptureTriggered;

    /// <summary>钉图热键被按下。</summary>
    public event Action? PinTriggered;

    /// <summary>GIF 录制热键被按下。</summary>
    public event Action? GifCaptureTriggered;

    /// <summary>创建 message-only 窗口并注册配置中的全局热键。返回是否全部注册成功。</summary>
    public bool Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureSource();
        bool captureOk = TryRegisterHotkey(CaptureHotkeyId, "截图", settings.CaptureHotkey, required: true);
        bool pinOk = TryRegisterHotkey(PinHotkeyId, "钉图", settings.PinHotkey, required: false);
        bool gifOk = TryRegisterHotkey(GifCaptureHotkeyId, "GIF 录制", settings.GifCaptureHotkey, required: false);
        return captureOk && pinOk && gifOk;
    }

    /// <summary>兼容旧调用：只注册截图热键。</summary>
    public bool Initialize(string captureHotkey)
    {
        EnsureSource();
        return RegisterConfiguredHotkeys(captureHotkey, string.Empty, string.Empty);
    }

    /// <summary>重新绑定截图热键（M9 设置界面用）。返回是否成功。</summary>
    public bool RegisterCaptureHotkey(string hotkey)
        => RegisterConfiguredHotkeys(
            hotkey,
            CurrentHotkey(PinHotkeyId),
            CurrentHotkey(GifCaptureHotkeyId));

    /// <summary>批量重新绑定截图 / 钉图 / GIF 录制热键；任一失败则恢复旧状态。</summary>
    public bool RegisterConfiguredHotkeys(
        string? captureHotkey,
        string? pinHotkey,
        string? gifCaptureHotkey)
    {
        if (_source is null)
            return false;

        var snapshot = new Dictionary<int, string>(_currentHotkeys);
        bool ok = TryRegisterHotkey(CaptureHotkeyId, "截图", captureHotkey, required: true)
            && TryRegisterHotkey(PinHotkeyId, "钉图", pinHotkey, required: false)
            && TryRegisterHotkey(GifCaptureHotkeyId, "GIF 录制", gifCaptureHotkey, required: false);

        if (ok)
            return true;

        RestoreSnapshot(snapshot);
        return false;
    }

    private void EnsureSource()
    {
        if (_source is not null)
            return;

        var parameters = new HwndSourceParameters("WinSnapHotkeyWindow")
        {
            ParentWindow = new IntPtr(HwndMessage), // message-only 窗口
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private bool TryRegisterHotkey(int id, string label, string? hotkey, bool required)
    {
        if (_source is null)
            return false;

        string normalized = hotkey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            if (required)
            {
                Log.Warning("{Label}热键为空，无法注册", label);
                return false;
            }

            UnregisterHotkey(id);
            return true;
        }

        if (_currentHotkeys.TryGetValue(id, out string? current) &&
            string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryParse(normalized, out var mods, out var vk))
        {
            Log.Warning("无法解析{Label}热键字符串：{Hotkey}", label, normalized);
            return false;
        }

        UnregisterHotkey(id);
        bool registered = GlobalHotkey.Register(_source.Handle, id, mods | GlobalHotkey.ModNoRepeat, vk);
        if (registered)
        {
            _currentHotkeys[id] = normalized;
            Log.Information("已注册{Label}热键：{Hotkey}", label, normalized);
            return true;
        }

        Log.Warning("{Label}热键注册失败（可能已被其它程序占用）：{Hotkey}", label, normalized);
        return false;
    }

    private void RestoreSnapshot(Dictionary<int, string> snapshot)
    {
        if (_source is null)
            return;

        foreach (int id in HotkeyIds)
            GlobalHotkey.Unregister(_source.Handle, id);
        _currentHotkeys.Clear();

        foreach (var (id, hotkey) in snapshot)
        {
            if (!TryParse(hotkey, out var mods, out var vk))
                continue;

            bool restored = GlobalHotkey.Register(_source.Handle, id, mods | GlobalHotkey.ModNoRepeat, vk);
            if (restored)
            {
                _currentHotkeys[id] = hotkey;
                Log.Information("已恢复原热键：{Hotkey}", hotkey);
            }
            else
            {
                Log.Error("恢复原热键失败：{Hotkey}", hotkey);
            }
        }
    }

    private void UnregisterHotkey(int id)
    {
        if (_source is null)
            return;

        GlobalHotkey.Unregister(_source.Handle, id);
        _currentHotkeys.Remove(id);
    }

    private string CurrentHotkey(int id)
        => _currentHotkeys.TryGetValue(id, out string? hotkey) ? hotkey : string.Empty;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != GlobalHotkey.WmHotkey)
            return IntPtr.Zero;

        int id = wParam.ToInt32();
        if (!_currentHotkeys.TryGetValue(id, out string? hotkey))
            return IntPtr.Zero;

        handled = true;
        Log.Debug("全局热键触发：{Hotkey}", hotkey);
        switch (id)
        {
            case CaptureHotkeyId:
                CaptureTriggered?.Invoke();
                break;
            case PinHotkeyId:
                PinTriggered?.Invoke();
                break;
            case GifCaptureHotkeyId:
                GifCaptureTriggered?.Invoke();
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>解析 "Ctrl+Alt+A" 形式的热键。主键支持 A-Z / 0-9 / NumPad0-NumPad9 / F1-F24。</summary>
    internal static bool TryParse(string hotkey, out uint modifiers, out uint virtualKey)
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
        if (key.Length == 7 &&
            key.StartsWith("NUMPAD", StringComparison.Ordinal) &&
            key[6] is >= '0' and <= '9')
        {
            virtualKey = (uint)(0x60 + key[6] - '0'); // VK_NUMPAD0 = 0x60
            return modifiers != 0;
        }
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + fn - 1); // VK_F1 = 0x70
            return modifiers != 0;
        }
        return false;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            foreach (int id in HotkeyIds)
                GlobalHotkey.Unregister(_source.Handle, id);
            _currentHotkeys.Clear();
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
