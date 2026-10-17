using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SharedQuickSearch;

/// <summary>
/// 通过 DWM 给自绘标题栏的窗口加上 Win11 原生圆角、阴影和深色边框。
/// Win10 上这些调用会静默失败，窗口仍正常显示（直角 + 系统阴影）。
/// </summary>
internal static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int SmCxSizeFrame = 32;
    private const int SmCxPaddedBorder = 92;

    public static void Apply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var corner = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            SetDarkMode(handle, dark);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    public static void SetDarkMode(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        try { SetDarkMode(handle, dark); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static void SetDarkMode(IntPtr handle, bool dark)
    {
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
    }

    /// <summary>无边框窗口最大化时会溢出屏幕，返回需要补回的内边距（DIP）。</summary>
    public static Thickness GetMaximizedPadding(Visual visual)
    {
        var pixels = GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder);
        var dpi = VisualTreeHelper.GetDpi(visual).DpiScaleX;
        var value = pixels <= 0 ? 8 : pixels / (dpi <= 0 ? 1 : dpi);
        return new Thickness(value);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
