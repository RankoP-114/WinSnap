using System.Runtime.InteropServices;

namespace WinSnap.Interop;

/// <summary>
/// 基于 Win32 <c>SendInput</c> 的鼠标输入模拟，供「长截图（滚动截图）」逐帧驱动滚动。
///
/// 兼容性限制：合成的滚轮事件由系统按当前光标位置命中窗口下的滚动逻辑，
/// 多数标准窗口/控件（Win32 列表、浏览器、文本编辑器等）会响应；但以下情况可能不滚动：
/// 1) 以管理员（更高完整性级别）运行的窗口——非提权进程无法向其注入输入（UIPI）；
/// 2) 自绘 / 自行处理原始输入而忽略 WM_MOUSEWHEEL 的应用，或仅响应触控板精密滚动手势的控件；
/// 3) 焦点/命中策略特殊的全屏游戏或 DirectComposition 表面。
/// 这些场景下自动长截图可能提前停止并返回当前已拼接内容。
/// 所有 P/Invoke 声明均为本类私有，不与现有 <c>NativeMethods</c> 冲突。
/// </summary>
public static class InputSimulator
{
    /// <summary>一个滚轮刻度对应的 delta（Win32 约定）。</summary>
    public const int WheelDelta = 120;

    /// <summary>
    /// 在当前光标位置发送一次竖直鼠标滚轮。
    /// <paramref name="delta"/> 为正向上滚、为负向下滚；幅度以 <see cref="WheelDelta"/>(120) 为一刻度
    /// （例如 -120 约等于一个标准滚轮缺口向下）。
    /// </summary>
    public static void ScrollVertical(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = unchecked((uint)delta),
                    dwFlags = MOUSEEVENTF_WHEEL,
                    time = 0,
                    dwExtraInfo = GetMessageExtraInfo()
                }
            }
        };

        var inputs = new[] { input };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 将光标移动到虚拟桌面坐标系下的物理像素点 (<paramref name="physicalX"/>, <paramref name="physicalY"/>)。
    /// 进程须为 Per-Monitor V2 DPI 感知（本应用经 app.manifest 声明），此处坐标即物理像素。
    /// </summary>
    public static void MoveCursorTo(int physicalX, int physicalY)
    {
        SetCursorPos(physicalX, physicalY);
    }

    // ---- 以下为本类私有 P/Invoke / 互操作结构 ----

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs,
        [MarshalAs(UnmanagedType.LPArray)] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
