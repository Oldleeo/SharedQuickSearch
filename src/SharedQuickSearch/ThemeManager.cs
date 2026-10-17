using Microsoft.Win32;
using System.Windows;

namespace SharedQuickSearch;

/// <summary>
/// 主题切换：替换 Application.Resources 第一个合并字典（Light/Dark），
/// 界面全部用 DynamicResource，所以切换是即时的。"System" 会跟随 Windows 应用主题变化。
/// </summary>
public static class ThemeManager
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string FollowSystem = "System";

    private static bool _listening;
    public static string Current { get; private set; } = Light;
    public static bool IsDarkActive { get; private set; }
    public static event Action? Changed;

    public static string Normalize(string? theme) =>
        theme is not null && (theme.Equals(Dark, StringComparison.OrdinalIgnoreCase)) ? Dark
        : theme is not null && theme.Equals(FollowSystem, StringComparison.OrdinalIgnoreCase) ? FollowSystem
        : theme is not null && theme.Equals(Light, StringComparison.OrdinalIgnoreCase) ? Light
        : FollowSystem;

    public static bool IsDark(string theme)
    {
        theme = Normalize(theme);
        if (theme == Dark) return true;
        if (theme == Light) return false;
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int value && value == 0;
        }
        catch { return false; }
    }

    public static void Apply(string? theme)
    {
        var app = Application.Current;
        if (app is null) return;
        Current = Normalize(theme);
        var dark = IsDark(Current);
        EnsureSystemListener();

        var dictionaries = app.Resources.MergedDictionaries;
        var source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var existing = dictionaries.Count > 0 ? dictionaries[0] : null;
        if (existing?.Source is null || existing.Source.OriginalString != source.OriginalString)
        {
            var dictionary = new ResourceDictionary { Source = source };
            if (dictionaries.Count > 0) dictionaries[0] = dictionary;
            else dictionaries.Add(dictionary);
        }

        IsDarkActive = dark;
        foreach (Window window in app.Windows) WindowEffects.SetDarkMode(window, dark);
        Changed?.Invoke();
    }

    private static void EnsureSystemListener()
    {
        if (_listening) return;
        _listening = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (Current != FollowSystem || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (IsDark(FollowSystem) != IsDarkActive) Apply(FollowSystem);
            });
        };
    }
}
