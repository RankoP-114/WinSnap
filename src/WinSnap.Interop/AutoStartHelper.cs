using Microsoft.Win32;

namespace WinSnap.Interop;

/// <summary>
/// 通过 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 管理开机自启。
/// 仅写当前用户表，无需管理员权限；纯托管实现（Microsoft.Win32.Registry），无 P/Invoke。
/// </summary>
public static class AutoStartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 启用开机自启。写入值为带引号的可执行路径，并可附加命令行参数，
    /// 形如 <c>"C:\path\app.exe" --tray</c>。重复调用为幂等覆盖。
    /// </summary>
    /// <param name="appName">Run 表中的值名（应用唯一标识）。</param>
    /// <param name="exePath">可执行文件完整路径。</param>
    /// <param name="arguments">可选命令行参数。</param>
    public static void Enable(string appName, string exePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(appName);
        ArgumentException.ThrowIfNullOrEmpty(exePath);

        string command = BuildCommand(exePath, arguments);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(appName, command, RegistryValueKind.String);
    }

    /// <summary>禁用开机自启（删除对应值）。值不存在时静默返回。</summary>
    /// <param name="appName">Run 表中的值名。</param>
    public static void Disable(string appName)
    {
        ArgumentException.ThrowIfNullOrEmpty(appName);

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(appName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 是否已为当前 exe 启用自启：Run 表存在 <paramref name="appName"/> 值，
    /// 且其指向的可执行路径与 <paramref name="exePath"/> 一致（忽略大小写与引号/参数）。
    /// </summary>
    /// <param name="appName">Run 表中的值名。</param>
    /// <param name="exePath">期望指向的可执行文件路径。</param>
    public static bool IsEnabled(string appName, string exePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(appName);
        ArgumentException.ThrowIfNullOrEmpty(exePath);

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key?.GetValue(appName) is not string stored || string.IsNullOrWhiteSpace(stored))
            return false;

        string? storedExe = ExtractExecutablePath(stored);
        if (storedExe is null)
            return false;

        return PathsEqual(storedExe, exePath);
    }

    // ---------------------------------------------------------------------
    // 内部实现
    // ---------------------------------------------------------------------

    private static string BuildCommand(string exePath, string? arguments)
    {
        string quoted = $"\"{exePath}\"";
        return string.IsNullOrWhiteSpace(arguments) ? quoted : $"{quoted} {arguments}";
    }

    /// <summary>从存储的命令行中提取可执行路径（去掉外层引号与尾部参数）。</summary>
    private static string? ExtractExecutablePath(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.Length == 0)
            return null;

        // 带引号：取第一对引号之间的内容。
        if (trimmed[0] == '"')
        {
            int end = trimmed.IndexOf('"', 1);
            if (end <= 0)
                return null;
            return trimmed.Substring(1, end - 1);
        }

        // 无引号：取首个空格前的 token 作为路径。
        int space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed.Substring(0, space);
    }

    private static bool PathsEqual(string a, string b)
    {
        string na = NormalizePath(a);
        string nb = NormalizePath(b);
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        string p = path.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(p).TrimEnd('\\', '/');
        }
        catch
        {
            // 非法路径字符等：退回原始去引号值做最佳努力比较。
            return p.TrimEnd('\\', '/');
        }
    }
}
