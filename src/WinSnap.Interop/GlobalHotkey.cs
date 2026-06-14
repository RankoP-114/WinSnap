namespace WinSnap.Interop;

/// <summary>
/// 全局热键注册的薄封装（user32 RegisterHotKey）。
/// 注册必须在拥有目标 HWND 的线程上调用；WM_HOTKEY 会投递到该窗口。
/// </summary>
public static class GlobalHotkey
{
    public const uint ModAlt = NativeMethods.MOD_ALT;
    public const uint ModControl = NativeMethods.MOD_CONTROL;
    public const uint ModShift = NativeMethods.MOD_SHIFT;
    public const uint ModWin = NativeMethods.MOD_WIN;
    public const uint ModNoRepeat = NativeMethods.MOD_NOREPEAT;
    public const int WmHotkey = NativeMethods.WM_HOTKEY;

    public static bool Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey)
        => NativeMethods.RegisterHotKey(hwnd, id, modifiers, virtualKey);

    public static bool Unregister(IntPtr hwnd, int id)
        => NativeMethods.UnregisterHotKey(hwnd, id);
}
