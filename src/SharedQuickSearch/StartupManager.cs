using Microsoft.Win32;

namespace SharedQuickSearch;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "共享速搜";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return !string.IsNullOrWhiteSpace(key?.GetValue(ValueName) as string);
        }
        catch { return false; }
    }

    /// <param name="exePath">开机要启动的程序；默认是当前运行的这个（安装程序会传安装后的路径）。</param>
    public static bool SetEnabled(bool enabled, string? exePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (enabled) key.SetValue(ValueName, $"\"{exePath ?? Environment.ProcessPath}\" --startup");
            else key.DeleteValue(ValueName, false);
            return true;
        }
        catch { return false; }
    }
}
