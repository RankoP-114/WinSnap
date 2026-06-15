using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinSnap.App.Settings;

/// <summary>
/// 热键录制控件：获得焦点后捕获 <see cref="UIElement.PreviewKeyDown"/>，
/// 把"修饰键组合 + 主键"解析为热键字符串。
/// <para>
/// 显示形如 <c>"Ctrl + Alt + A"</c>（带空格便于阅读）；对外暴露的 <see cref="Hotkey"/>
/// 则为 <c>"Ctrl+Alt+A"</c>（无空格），与 <c>AppSettings.CaptureHotkey</c> 及
/// <c>HotkeyManager</c> 的解析格式完全一致。
/// </para>
/// <para>
/// 过滤规则（与 HotkeyManager 对齐）：必须至少有一个修饰键（Ctrl/Alt/Shift/Win），
/// 主键限 A-Z / 0-9 / NumPad0-NumPad9 / F1-F24；单独按下修饰键、无修饰键的组合均视为无效、不接受。
/// </para>
/// </summary>
public partial class HotkeyRecorderControl : UserControl
{
    private static readonly SolidColorBrush NormalBorder = new(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly SolidColorBrush ActiveBorder = new(Color.FromRgb(0x00, 0x78, 0xD4));

    static HotkeyRecorderControl()
    {
        NormalBorder.Freeze();
        ActiveBorder.Freeze();
    }

    public HotkeyRecorderControl()
    {
        InitializeComponent();
        // 单击控件即获取键盘焦点开始录制。
        MouseLeftButtonDown += (_, _) => Focus();
    }

    /// <summary>
    /// 当前热键字符串（如 <c>"Ctrl+Alt+A"</c>，无空格）。空串表示未设置。
    /// 设置时会同步刷新显示。可在 XAML/代码中初始化为 AppSettings 中已有的值。
    /// </summary>
    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value ?? string.Empty);
    }

    /// <summary><see cref="Hotkey"/> 的依赖属性（支持绑定）。</summary>
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey),
        typeof(string),
        typeof(HotkeyRecorderControl),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnHotkeyChanged));

    /// <summary>热键变更事件（用户录入了新的合法组合，或被清空）。参数为新的热键字符串。</summary>
    public event Action<string>? HotkeyChanged;

    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (HotkeyRecorderControl)d;
        ctrl.RefreshDisplay();
        ctrl.HotkeyChanged?.Invoke((string)e.NewValue);
    }

    private void RefreshDisplay()
    {
        bool focused = IsKeyboardFocusWithin;
        if (!string.IsNullOrEmpty(Hotkey))
        {
            // 存储用无空格，显示用 " + " 分隔。
            DisplayText.Text = Hotkey.Replace("+", " + ");
        }
        else
        {
            DisplayText.Text = focused ? "按下快捷键…" : "未设置";
        }
    }

    private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        RootBorder.BorderBrush = ActiveBorder;
        if (string.IsNullOrEmpty(Hotkey))
            DisplayText.Text = "按下快捷键…";
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        RootBorder.BorderBrush = NormalBorder;
        RefreshDisplay();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Hotkey = string.Empty;
        Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 始终吞掉按键，避免触发 Tab 切焦点 / 空格点击等默认行为。
        e.Handled = true;

        // Alt 组合时真实主键在 SystemKey。
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Esc / Back（无修饰）：取消本次录制，保持原值并让出焦点。
        if (key is Key.Escape or Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            MoveFocusAway();
            return;
        }

        // 单独的修饰键 / IME 处理键：等待主键，暂不接受。
        if (IsModifierOrIgnored(key))
            return;

        var mods = Keyboard.Modifiers; // Control / Alt / Shift / Windows 的位组合

        // 过滤：无修饰键的组合一律不接受（符合 HotkeyManager 对单字符主键的要求，
        // 也避免误吞普通输入）。
        if (mods == ModifierKeys.None)
            return;

        if (!TryFormatMainKey(key, out string mainKey))
            return; // 主键不在 A-Z / 0-9 / F1-F24 范围

        Hotkey = BuildHotkeyString(mods, mainKey);
        MoveFocusAway();
    }

    /// <summary>把修饰键位 + 主键拼成存储用字符串，顺序固定 Ctrl→Alt→Shift→Win。</summary>
    private static string BuildHotkeyString(ModifierKeys mods, string mainKey)
    {
        var sb = new StringBuilder();
        if (mods.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (mods.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (mods.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(mainKey);
        return sb.ToString();
    }

    /// <summary>是否为单独的修饰键或应忽略的处理键（不能作为主键）。</summary>
    private static bool IsModifierOrIgnored(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System or Key.None or
        Key.DeadCharProcessed or Key.ImeProcessed or
        Key.Apps or Key.Clear;

    /// <summary>
    /// 把 <see cref="Key"/> 主键格式化为 HotkeyManager 可解析的标记：
    /// 字母 A-Z、主键盘数字 0-9、小键盘数字 NumPad0-NumPad9、功能键 F1-F24。其余返回 false。
    /// </summary>
    private static bool TryFormatMainKey(Key key, out string token)
    {
        token = string.Empty;

        // 字母 A-Z
        if (key is >= Key.A and <= Key.Z)
        {
            token = key.ToString(); // "A".."Z"
            return true;
        }

        // 主键盘数字 D0-D9
        if (key is >= Key.D0 and <= Key.D9)
        {
            token = ((char)('0' + (key - Key.D0))).ToString();
            return true;
        }

        // 小键盘数字 NumPad0-NumPad9：保留独立 token，避免和主键盘数字混淆。
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            token = "NumPad" + (key - Key.NumPad0);
            return true;
        }

        // 功能键 F1-F24
        if (key is >= Key.F1 and <= Key.F24)
        {
            int n = key - Key.F1 + 1;
            token = "F" + n;
            return true;
        }

        return false;
    }

    private void MoveFocusAway()
    {
        // 录制完成后把焦点交给下一个可聚焦元素，结束录制态。
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}
