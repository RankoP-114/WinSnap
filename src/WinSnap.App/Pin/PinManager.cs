using System.Collections.Generic;
using System.Windows.Media.Imaging;
using WinSnap.Interop;

namespace WinSnap.App.Pin;

/// <summary>
/// 「钉图」管理器：创建并跟踪屏幕上的 <see cref="PinWindow"/> 实例。
/// 窗口关闭时自动从列表移除；<see cref="CloseAll"/> 可一次性关闭全部（如退出时）。
/// 须在 UI 线程调用。
/// </summary>
public sealed class PinManager
{
    private const int DefaultMaxPinnedWindows = 20;
    private readonly List<PinWindow> _windows = new();

    /// <summary>钉图软上限；小于等于 0 表示不限制。超过时自动关闭最早的钉图。</summary>
    public int MaxPinnedWindows { get; init; } = DefaultMaxPinnedWindows;

    /// <summary>当前打开的钉图窗口数量。</summary>
    public int Count => _windows.Count;

    /// <summary>
    /// 把一张图片钉到屏幕：创建并显示一个无边框置顶小窗口，初始左上角位于给定物理像素坐标。
    /// </summary>
    /// <param name="image">要显示的位图（物理像素，建议已 Freeze）。</param>
    /// <param name="captured">可选的原始捕获数据，供「复制」使用；为 null 时复制时从 <paramref name="image"/> 即时转换。</param>
    /// <param name="physicalX">初始左上角的屏幕物理像素 X。</param>
    /// <param name="physicalY">初始左上角的屏幕物理像素 Y。</param>
    /// <returns>新建的钉图窗口。</returns>
    public PinWindow Pin(BitmapSource image, CapturedImage? captured, int physicalX, int physicalY)
    {
        ArgumentNullException.ThrowIfNull(image);

        TrimToCapacityForNewPin();
        var window = new PinWindow(image, captured, physicalX, physicalY);
        window.PinClosed += OnPinClosed;
        _windows.Add(window);
        window.Show();
        return window;
    }

    /// <summary>关闭所有钉图窗口。</summary>
    public void CloseAll()
    {
        // 复制一份再遍历：Close 会触发 PinClosed → 修改 _windows
        foreach (var window in _windows.ToArray())
            window.Close();
        _windows.Clear();
    }

    private void OnPinClosed(object? sender, EventArgs e)
    {
        if (sender is PinWindow window)
        {
            window.PinClosed -= OnPinClosed;
            _windows.Remove(window);
        }
    }

    private void TrimToCapacityForNewPin()
    {
        if (MaxPinnedWindows <= 0)
            return;

        while (_windows.Count >= MaxPinnedWindows)
        {
            var oldest = _windows[0];
            oldest.PinClosed -= OnPinClosed;
            _windows.RemoveAt(0);
            oldest.Close();
        }
    }
}
