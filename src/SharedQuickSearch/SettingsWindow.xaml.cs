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
    private readonly Func<int>? _previewableCount;
    private string _cacheFolderChoice;
    private int _previewable = -1;
    private string _teamMode = TeamCacheModes.Auto;
    private bool _settingTeamBox;
    private readonly string _originalTheme;
    private string _selectedTheme;
    private bool _loading = true;

    public SettingsWindow(Window? owner, AppSettings settings, ThumbnailService? thumbnails = null, Func<int>? previewableCount = null)
    {
        InitializeComponent();
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _thumbnails = thumbnails;
        _previewableCount = previewableCount;
        _source = settings.Clone();
        _cacheFolderChoice = _source.ThumbnailCacheFolder;
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
        var version = "版本 " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.12.0");
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
        CacheFolderText.Text = thumbnails?.CacheFolder ?? ThumbnailService.ResolveLocalFolder(_source.ThumbnailCacheFolder);
        DoubleCtrlCheck.IsChecked = _source.DoubleCtrlSummon;
        PublishIndexCheck.IsChecked = _source.PublishSharedIndex;
        _teamMode = TeamCacheModes.Normalize(_source.TeamCacheMode);
        SetTeamBox(_source.TeamCacheFolder);
        TeamCacheCheck.IsChecked = _teamMode != TeamCacheModes.Off;
        TeamCachePanel.Visibility = TeamCacheCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (thumbnails is not null)
        {
            thumbnails.TeamStateChanged += ShowTeamState;
            Closed += (_, _) => thumbnails.TeamStateChanged -= ShowTeamState;
        }
        ShowTeamState();
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

    // ================= 缓存位置 =================

    private void ChangeCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择本机缩略图缓存的位置（会在里面建一个“共享速搜缩略图”文件夹）", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        if (!Setup.InstallerEngine.IsLocalPath(dialog.SelectedPath))
        {
            ModernDialog.Show(this, "本机缓存要放在这台电脑的硬盘上。想全组共用，用下面的“全组共用缩略图缓存”。", "缓存位置", MessageBoxImage.Warning);
            return;
        }
        var resolved = ThumbnailService.ResolveLocalFolder(dialog.SelectedPath);
        _cacheFolderChoice = string.Equals(resolved, ThumbnailService.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? "" : dialog.SelectedPath;
        CacheFolderText.Text = resolved;
        CacheFolderHint.Text = "保存后生效：新位置从空开始，旧位置的缓存会删掉。";
        CacheFolderHint.Visibility = Visibility.Visible;
    }

    private void TeamCacheCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (TeamCachePanel is null) return;
        var on = TeamCacheCheck.IsChecked == true;
        TeamCachePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && !_loading) Ui.FadeIn(TeamCachePanel, offsetY: 6, milliseconds: 200);
        if (on && _teamMode == TeamCacheModes.Off) _teamMode = TeamCacheModes.Auto;
        ShowTeamState();
    }

    private void TeamCacheBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _settingTeamBox) return;
        _teamMode = TeamCacheBox.Text.Trim().Length == 0 ? TeamCacheModes.Auto : TeamCacheModes.Manual; // 自己填了位置 = 不再自动找
        ShowTeamState();
    }

    private void SetTeamBox(string text)
    {
        _settingTeamBox = true;
        try { TeamCacheBox.Text = text; }
        finally { _settingTeamBox = false; }
    }

    private async void AutoFindTeamCache_Click(object sender, RoutedEventArgs e)
    {
        _teamMode = TeamCacheModes.Auto;
        var roots = Roots.ToList();
        if (TeamCacheLocator.Candidates(roots).Count == 0)
        {
            SetTeamStatus("索引目录都在本机，没有共享盘可以放全组缓存。", "WarningBrush");
            return;
        }
        SetTeamStatus("正在共享盘上找 .共享速搜缓存…", "AccentBrush");
        var found = await TeamCacheLocator.FindOrCreateAsync(roots, create: false);
        if (found is not null)
        {
            SetTeamBox(found);
            SetTeamStatus("找到了：同事已经在用这个，保存后直接共用", "SuccessBrush");
        }
        else
        {
            SetTeamBox("");
            SetTeamStatus($"还没有人建过。保存后会自动在 {TeamThumbnailCache.SuggestFor(roots)} 新建（那里不能写就往下一层找）", "TextTertiaryBrush");
        }
    }

    private void BrowseTeamCache_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择共享盘上放全组缓存的位置（会在里面建一个隐藏的 .共享速搜缓存 文件夹）", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _teamMode = TeamCacheModes.Manual;
        SetTeamBox(TeamThumbnailCache.NormalizeFolder(dialog.SelectedPath));
        ShowTeamState();
    }

    private async void CheckTeamCache_Click(object sender, RoutedEventArgs e)
    {
        var path = TeamCacheBox.Text.Trim();
        if (path.Length == 0) return;
        if (Setup.InstallerEngine.IsLocalPath(path))
        {
            SetTeamStatus("这是本机硬盘上的位置，同事访问不到。要选共享盘上的文件夹。", "WarningBrush");
            return;
        }
        SetTeamStatus("正在检查…", "AccentBrush");
        var probe = new TeamThumbnailCache(path);
        var state = await probe.ProbeAsync();
        SetTeamBox(probe.Folder);
        SetTeamStatus(probe.Describe(), state switch { TeamCacheState.ReadWrite => "SuccessBrush", TeamCacheState.ReadOnly => "AccentBrush", _ => "WarningBrush" });
    }

    private void ShowTeamState()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(ShowTeamState); return; }
        if (TeamCacheCheck.IsChecked != true) return;
        var path = TeamCacheBox.Text.Trim();
        var live = _thumbnails?.Team;
        if (live is not null && path.Length > 0 && string.Equals(TeamThumbnailCache.NormalizeFolder(path), live.Folder, StringComparison.OrdinalIgnoreCase))
            SetTeamStatus(live.Describe(), live.State switch { TeamCacheState.ReadWrite => "SuccessBrush", TeamCacheState.ReadOnly or TeamCacheState.Checking => "AccentBrush", _ => "WarningBrush" });
        else SetTeamStatus(path.Length == 0 ? "自动：保存后在共享盘上找同事建好的 .共享速搜缓存，没有就新建一个" : "还没检查，点“检查”看看能不能读写", "TextTertiaryBrush");

        // 20 万条索引数一遍要几十毫秒：只数一次
        if (_previewable < 0) _previewable = _previewableCount?.Invoke() ?? 0;
        var count = _previewable;
        TeamCacheEstimate.Text = count > 0
            ? $"索引里有 {count:N0} 个能出缩略图的文件，全部有人看过一遍的话大约 {FormatBytes(count * 20L * 1024)}（每张约 20 KB；后台预热生成的不上传，只放真有人看过的）。"
            : "每张缩略图约 20 KB：1 万张约 200 MB，10 万张约 2 GB。";
    }

    private void SetTeamStatus(string text, string brush)
    {
        TeamCacheStatus.Text = text;
        TeamCacheDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
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
        StatTeam.Text = _thumbnails.Team is null ? "未开启" : $"{stats.Team:N0} / {stats.TeamWrites:N0}";
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

        var teamOn = TeamCacheCheck.IsChecked == true;
        var teamMode = !teamOn ? TeamCacheModes.Off : _teamMode == TeamCacheModes.Off ? TeamCacheModes.Auto : _teamMode;
        var teamFolder = teamOn ? TeamCacheBox.Text.Trim() : "";
        if (teamMode == TeamCacheModes.Manual && teamFolder.Length == 0)
        {
            SelectPage(4);
            ModernDialog.Show(this, "开了全组共用缓存，但没有填位置。填一个共享盘上的文件夹，或者把开关关掉。", "全组缓存", MessageBoxImage.Warning);
            return;
        }
        if (teamFolder.Length > 0 && Setup.InstallerEngine.IsLocalPath(teamFolder))
        {
            SelectPage(4);
            ModernDialog.Show(this, "全组缓存要放在共享盘上（\\\\服务器\\… 或映射的网络盘），本机硬盘上的位置同事访问不到。", "全组缓存", MessageBoxImage.Warning);
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
        SavedSettings.DoubleCtrlSummon = DoubleCtrlCheck.IsChecked == true;
        SavedSettings.ThumbnailCacheFolder = _cacheFolderChoice;
        SavedSettings.TeamCacheMode = teamMode;
        SavedSettings.PublishSharedIndex = PublishIndexCheck.IsChecked == true;
        // 关掉时记住原来的位置，再打开不用重新找
        SavedSettings.TeamCacheFolder = !teamOn ? _source.TeamCacheFolder : teamFolder.Length == 0 ? "" : TeamThumbnailCache.NormalizeFolder(teamFolder);
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
