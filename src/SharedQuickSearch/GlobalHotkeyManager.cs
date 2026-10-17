using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SharedQuickSearch;

[Flags]
public enum HotkeyModifiers : uint
{
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

public sealed class GlobalHotkeyManager : IDisposable
{
    private const int HotkeyId = 0x5153;
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;

    public event EventHandler? Pressed;

    public bool Register(Window window, string shortcut)
    {
        Unregister();
        if (!TryParse(shortcut, out var modifiers, out var key)) return false;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowHook);
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(_handle, HotkeyId, (uint)(modifiers | HotkeyModifiers.NoRepeat), virtualKey);
        if (!_registered)
        {
            _source?.RemoveHook(WindowHook);
            _source = null;
        }
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _handle != IntPtr.Zero) UnregisterHotKey(_handle, HotkeyId);
        _source?.RemoveHook(WindowHook);
        _source = null;
        _registered = false;
        _handle = IntPtr.Zero;
    }

    public static bool TryParse(string? shortcut, out HotkeyModifiers modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(shortcut)) return false;
        foreach (var part in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "windows": modifiers |= HotkeyModifiers.Windows; break;
                default:
                    if (!Enum.TryParse(part, true, out key)) return false;
                    break;
            }
        }
        return modifiers != 0 && key is not (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin);
    }

    public static string Format(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
