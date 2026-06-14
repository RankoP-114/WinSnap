using System.Windows;

// .NET 内置 Fluent 主题的 ThemeMode 为实验性 API：在代码中引用其类型或成员会触发
// 诊断 WPF0001。本文件整体引用该 API，故在文件范围内显式抑制（官方推荐的逐处抑制方式）。
#pragma warning disable WPF0001

namespace WinSnap.App.Theming;

/// <summary>
/// 应用主题切换：把配置中的主题字符串映射到 .NET 10 WPF 内置 Fluent 主题
/// （<see cref="System.Windows.ThemeMode"/>）。在应用启动时与设置保存后调用
/// <see cref="Apply(string?)"/> 即可。
/// </summary>
/// <remarks>
/// <para>
/// .NET 9/10 通过 <c>Application.ThemeMode</c> / <c>Window.ThemeMode</c> 暴露内置
/// Fluent 主题，类型为 <c>System.Windows.ThemeMode</c>，取值 <c>{ System, Light, Dark, None }</c>：
/// <list type="bullet">
///   <item><description><c>System</c>：跟随 Windows 当前浅色/深色设置。</description></item>
///   <item><description><c>Light</c> / <c>Dark</c>：强制浅色 / 深色 Fluent 主题。</description></item>
///   <item><description><c>None</c>（默认）：不应用 Fluent，使用经典 Aero2 主题。</description></item>
/// </list>
/// </para>
/// </remarks>
public static class ThemeManager
{
    /// <summary>
    /// 应用主题到整个应用。<paramref name="theme"/> 取值（大小写不敏感）：
    /// <c>"system"</c>（跟随系统）/ <c>"light"</c>（浅色）/ <c>"dark"</c>（深色）。
    /// 未知值回退为跟随系统。<see cref="Application.Current"/> 为空时静默返回。
    /// </summary>
    public static void Apply(string? theme)
    {
        var app = Application.Current;
        if (app is null)
            return;

        app.ThemeMode = ToThemeMode(theme);
    }

    /// <summary>把主题字符串映射为 <see cref="ThemeMode"/>；未知值回退 <see cref="ThemeMode.System"/>。</summary>
    public static ThemeMode ToThemeMode(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System, // "system" 及任何未知值
    };
}

#pragma warning restore WPF0001
