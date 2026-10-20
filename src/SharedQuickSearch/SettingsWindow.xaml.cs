using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace SharedQuickSearch;

public partial class SettingsWindow : Window
{
    /// <summary>主题卡片里迷你窗口的配色（固定值，不随当前主题变化）。</summary>
    public sealed record MiniPalette(Brush Background, Brush Surface, Brush Thumb, Brush Accent);

    public ObservableCollection<string> Roots { get; } = new();
    public ObservableCollection<string> Exclusions { get; } = new();
    public AppSettings? SavedSettings { get; private set; }

    private readonly AppSettings _source;
    private readonly ThumbnailService? _thumbnails;
    private readonly string _originalTheme;
    private string _selectedTheme;
    private bool _loading = true;

    public SettingsWindow(Window? owner, AppSettings settings, ThumbnailService? thumbnails = null)
    {
        InitializeComponent();
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _thumbnails = thumbnails;
        _source = settings.Clone();
        _originalTheme = ThemeManager.Normalize(_source.Theme);
        _selectedTheme = _originalTheme;

        foreach (var path in _source.IndexRoots) Roots.Add(path);
        foreach (var path in _source.ExcludedPaths) Exclusions.Add(path);
        Roots.CollectionChanged += (_, _) => UpdateEmptyHints();
        Exclusions.CollectionChanged += (_, _) => UpdateEmptyHints();
        UpdateEmptyHints();

        AutoStartCheck.IsChecked = StartupManager.IsEnabled();
        TrayCheck.IsChecked = _source.MinimizeToTray;
        SpotlightCheck.IsChecked = _source.LaunchMode != "Window";
        ClipboardCheck.IsChecked = _source.ClipboardPrefill;
        HotkeyBox.Text = string.IsNullOrWhiteSpace(_source.GlobalHotkey) ? "Ctrl+Alt+Space" : _source.GlobalHotkey;
        var version = "版本 " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.10.0");
        VersionText.Text = version;
        NavVersionText.Text = version;

        var light = new MiniPalette(Solid("#F2F3F5"), Solid("#FFFFFF"), Solid("#E7E9EE"), Solid("#3D5AFE"));
        var dark = new MiniPalette(Solid("#0D0E11"), Solid("#1B1C21"), Solid("#2A2C33"), Solid("#6F86FF"));
        ThemeLightPreview.Content = light;
        ThemeDarkPreview.Content = dark;
        ThemeSystemLeft.Content = light;
        ThemeSystemRight.Content = dark;
        (_originalTheme switch { ThemeManager.Light => ThemeLight, ThemeManager.Dark => ThemeDark, _ => ThemeSystem }).IsChecked = true;

        PrefetchCheck.IsChecked = _source.ThumbnailPrefetch;
        CacheFolderText.Text = thumbnails?.CacheFolder ?? ThumbnailService.DefaultFolder;
        var limitItem = CacheLimitBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag as string == _source.ThumbnailCacheLimitMb.ToString());
        CacheLimitBox.SelectedItem = limitItem ?? CacheLimitBox.Items[2];

        UpdateFeedBox.Text = _source.UpdateFeed;
        if (UpdateService.Instance is { } updates)
        {
            updates.Changed += ShowUpdateStatus;
            Closed += (_, _) => updates.Changed -= ShowUpdateStatus;
        }
        ShowUpdateStatus();

        NavIndex.IsChecked = true;
        DataContext = this;
        _loading = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this, ThemeManager.IsDarkActive);
    }

    private static SolidColorBrush Solid(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // ================= 导航 =================

    public void SelectPage(int index)
    {
        var nav = Math.Clamp(index, 0, 4) switch { 0 => NavIndex, 1 => NavGeneral, 2 => NavAppearance, 3 => NavAbout, _ => NavThumbs };
        nav.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var index) || PagesHost is null) return;
        var pages = new FrameworkElement[] { PageIndex, PageGeneral, PageAppearance, PageAbout, PageThumbs };
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
        if (!_loading) Ui.FadeIn(pages[index], offsetY: 10, milliseconds: 240);
        if (index == 4) _ = RefreshThumbnailInfoAsync();
    }

    // ================= 自动更新 =================

    private void ShowUpdateStatus()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(ShowUpdateStatus); return; }
        var updates = UpdateService.Instance;
        UpdateStatusText.Text = updates is null ? "更新服务没有启动" : $"当前 {UpdateService.CurrentVersion} · {updates.StatusText}";
        UpdateCheckButton.IsEnabled = updates is not null && updates.Stage is not (UpdateStage.Checking or UpdateStage.Downloading);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Instance is not { } updates) return;
        // 手动检查用框里现在写的更新源（还没点保存也算）；检查完恢复
        var feed = UpdateFeedBox.Text.Trim();
        if (feed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ModernDialog.Show(this, "更新源必须是 https 地址或文件路径，不接受 http。", "更新源", MessageBoxImage.Warning);
            return;
        }
        UpdateCheckButton.IsEnabled = false;
        await updates.CheckAsync(manual: true, feedOverride: feed);
        ShowUpdateStatus();
    }

    // ================= 缩略图 =================

    private async Task RefreshThumbnailInfoAsync()
    {
        if (_thumbnails is null) { CacheSizeText.Text = "缓存信息不可用"; return; }
        var stats = _thumbnails.GetStats();
        StatDisk.Text = stats.Disk.ToString("N0");
        StatWindows.Text = stats.WindowsCache.ToString("N0");
        StatEmbedded.Text = stats.Embedded.ToString("N0");
        StatGenerated.Text = stats.Generated.ToString("N0");
        StatAverage.Text = stats.Disk + stats.WindowsCache + stats.Embedded + stats.Generated == 0 ? "—" : $"{stats.AverageMilliseconds:F0} ms";
        StatPrefetch.Text = stats.PrefetchTotal == 0 ? (stats.PrefetchRunning ? "准备中" : "—")
            : stats.PrefetchRunning ? $"{stats.PrefetchDone * 100.0 / stats.PrefetchTotal:F0}%" : $"{stats.PrefetchDone:N0} 张";
        CacheSizeText.Text = "正在统计…";
        var (bytes, files) = await _thumbnails.MeasureCacheAsync();
        CacheSizeText.Text = files == 0 ? "还没有缓存" : $"已缓存 {files:N0} 张，占用 {FormatBytes(bytes)}";
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:F2} GB" : $"{bytes / 1024d / 1024:F0} MB";

    private void RefreshStats_Click(object sender, RoutedEventArgs e) => _ = RefreshThumbnailInfoAsync();

    private void OpenCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _thumbnails?.CacheFolder ?? ThumbnailService.DefaultFolder;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { ModernDialog.Show(this, ex.Message, "无法打开文件夹", MessageBoxImage.Warning); }
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (_thumbnails is null) return;
        CacheSizeText.Text = "正在清空…";
        await _thumbnails.ClearCacheAsync();
        await RefreshThumbnailInfoAsync();
    }

    private void UpdateEmptyHints()
    {
        RootsEmptyText.Visibility = Roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExclusionsEmptyText.Visibility = Exclusions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= 目录 =================

    private void AddRoot_Click(object sender, RoutedEventArgs e) => AddPath(RootPathBox, Roots, "请输入要索引的文件夹路径。");
    private void AddExclude_Click(object sender, RoutedEventArgs e) => AddPath(ExcludePathBox, Exclusions, "请输入要排除的文件夹路径。");
    private void RootPathBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddRoot_Click(sender, e); e.Handled = true; } }
    private void ExcludePathBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddExclude_Click(sender, e); e.Handled = true; } }

    private void AddPath(TextBox box, ObservableCollection<string> collection, string emptyMessage)
    {
        var value = PathRules.Normalize(box.Text);
        if (value.Length == 0) { ModernDialog.Show(this, emptyMessage, "共享速搜", MessageBoxImage.Information); return; }
        if (!collection.Contains(value, StringComparer.OrdinalIgnoreCase)) collection.Add(value);
        box.Clear();
        box.Focus();
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is string path) Roots.Remove(path); }
    private void RemoveExclude_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is string path) Exclusions.Remove(path); }
    private void BrowseRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(RootPathBox);
    private void BrowseExclude_Click(object sender, RoutedEventArgs e) => BrowseInto(ExcludePathBox);

    private static void BrowseInto(TextBox box)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择文件夹（也可以直接输入 UNC 共享路径）", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) box.Text = dialog.SelectedPath;
    }

    // ================= 快捷键 =================

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyHint.Text = "现在按下新的组合键…";
        HotkeyHint.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        HotkeyBox.SelectAll();
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyHint.Text = "保存设置后生效。";
        HotkeyHint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab) { e.Handled = false; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            HotkeyHint.Text = "快捷键必须包含至少一个修饰键（Ctrl / Alt / Shift / Win）。";
            HotkeyHint.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            return;
        }
        HotkeyBox.Text = GlobalHotkeyManager.Format(key, modifiers);
        HotkeyHint.Text = "已记录，保存后生效。";
        HotkeyHint.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e) => HotkeyBox.Text = "Ctrl+Alt+Space";

    // ================= 主题（实时预览） =================

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        _selectedTheme = ReferenceEquals(sender, ThemeLight) ? ThemeManager.Light
            : ReferenceEquals(sender, ThemeDark) ? ThemeManager.Dark
            : ThemeManager.FollowSystem;
        if (!_loading) ThemeManager.Apply(_selectedTheme);
    }

    // ================= 关于 =================

    private void Author_Click(object sender, RoutedEventArgs e) => ExternalLinks.OpenOldleeProfile();
    private void Privacy_Click(object sender, RoutedEventArgs e) => new PrivacyWindow { Owner = this }.ShowDialog();
    private void Tutorial_Click(object sender, RoutedEventArgs e) => new OnboardingWindow(null, tutorialOnly: true) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();

    // ================= 保存 / 取消 =================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var roots = PathRules.NormalizeDistinct(Roots);
        var exclusions = PathRules.NormalizeDistinct(Exclusions);
        var conflicts = roots.Where(root => exclusions.Any(exclusion => PathRules.IsSameOrChild(root, exclusion))).ToArray();
        if (conflicts.Length > 0)
        {
            SelectPage(0);
            ModernDialog.Show(this, "索引目录不能位于排除目录之下：\n\n" + string.Join("\n", conflicts), "路径冲突", MessageBoxImage.Warning);
            return;
        }
        if (!GlobalHotkeyManager.TryParse(HotkeyBox.Text, out _, out _))
        {
            SelectPage(1);
            ModernDialog.Show(this, "快捷键格式无效，请重新按下组合键。", "快捷键", MessageBoxImage.Warning);
            return;
        }

        if (UpdateFeedBox.Text.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            SelectPage(3);
            ModernDialog.Show(this, "更新源必须是 https 地址或文件路径，不接受 http。", "更新源", MessageBoxImage.Warning);
            return;
        }

        var autoStart = AutoStartCheck.IsChecked == true;
        if (!StartupManager.SetEnabled(autoStart))
        {
            SelectPage(1);
            ModernDialog.Show(this, "无法修改开机启动设置，请检查当前 Windows 用户的权限。", "开机启动", MessageBoxImage.Warning);
            return;
        }

        SavedSettings = _source.Clone();
        SavedSettings.IndexRoots = roots.ToList();
        SavedSettings.ExcludedPaths = exclusions.ToList();
        SavedSettings.AutoStart = autoStart;
        SavedSettings.GlobalHotkey = HotkeyBox.Text;
        SavedSettings.MinimizeToTray = TrayCheck.IsChecked == true;
        SavedSettings.LaunchMode = SpotlightCheck.IsChecked == true ? "Spotlight" : "Window";
        SavedSettings.ClipboardPrefill = ClipboardCheck.IsChecked == true;
        SavedSettings.Theme = _selectedTheme;
        SavedSettings.ThumbnailPrefetch = PrefetchCheck.IsChecked == true;
        SavedSettings.UpdateFeed = UpdateFeedBox.Text.Trim();
        if (CacheLimitBox.SelectedItem is ComboBoxItem { Tag: string limit } && int.TryParse(limit, out var megabytes)) SavedSettings.ThumbnailCacheLimitMb = megabytes;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // 没保存就关闭：把实时预览的主题换回去。
        if (DialogResult != true && ThemeManager.Current != _originalTheme) ThemeManager.Apply(_originalTheme);
    }
}
