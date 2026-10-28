using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace SharedQuickSearch;

public enum RootState { Checking, Ok, Failed }

/// <summary>引导里添加的一个共享盘目录，加进来就检查能不能连上。</summary>
public sealed class RootCandidate : Observable
{
    private RootState _state = RootState.Checking;
    private string _stateText = "正在检查…";
    public RootCandidate(string path) { Path = path; }
    public string Path { get; }
    public RootState State { get => _state; set => Set(ref _state, value); }
    public string StateText { get => _stateText; set => Set(ref _stateText, value); }
}

/// <summary>可以排除的一个文件夹。IsExcluded = 不录入。</summary>
public sealed class ExcludeCandidate : Observable
{
    private bool _isExcluded;
    public ExcludeCandidate(string path, string reason, bool excluded)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        Parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? "";
        Reason = reason;
        _isExcluded = excluded;
    }
    public string Path { get; }
    public string Name { get; }
    public string Parent { get; }
    public string Reason { get; }
    public bool IsExcluded { get => _isExcluded; set => Set(ref _isExcluded, value); }
}

public sealed record DriveSuggestion(string Label, string Path);

/// <summary>首次引导里选好的东西，一次交给后台宿主保存。</summary>
public sealed record OnboardingChoices(
    IReadOnlyList<string> Roots, IReadOnlyList<string> Exclusions, bool AutoStart, bool ClipboardPrefill, bool SpotlightLaunch,
    bool DoubleCtrl, string LocalCacheFolder, string TeamCacheFolder, string? UpdateFeed);

/// <summary>
/// 首次引导 + 使用教学。
/// 引导：欢迎 → 共享盘目录 → 不录入的文件夹 → 选项 → 开始扫描；
/// 教学：5 张带动画的卡片（扫描在后台进行，底栏显示进度）。
/// 和搜索条、安装程序同一套面板和动效。
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly MainWindow? _host;
    private readonly ObservableCollection<RootCandidate> _roots = new();
    private readonly ObservableCollection<ExcludeCandidate> _excludes = new();
    private readonly List<FrameworkElement> _setupFlow;
    private readonly List<FrameworkElement> _tutorialFlow;
    private List<FrameworkElement> _flow;
    private int _page;
    private bool _inTutorial;
    private bool _applied;
    private bool _finished;
    private bool _busy;
    private string _scannedSignature = "";
    private CancellationTokenSource? _excludeScan;
    private readonly List<(IAnimatable Target, DependencyProperty Property)> _demos = new();
    private readonly string _hotkey;
    private bool _teamCache;
    private string _localCacheChoice = "";
    private TeamCacheState? _teamProbe;

    /// <param name="host">后台宿主；只看教学时可以为 null（从设置窗口打开）。</param>
    public OnboardingWindow(MainWindow? host, bool tutorialOnly)
    {
        _host = host;
        InitializeComponent();
        _setupFlow = new List<FrameworkElement> { PageWelcome, PageRoots, PageExclude, PageOptions, PageCache, PageReady };
        _tutorialFlow = new List<FrameworkElement> { TutorialSummon, TutorialSearch, TutorialDrag, TutorialPreview, TutorialKeys };
        _flow = _setupFlow;
        RootsList.ItemsSource = _roots;
        ExcludeList.ItemsSource = _excludes;
        _roots.CollectionChanged += (_, _) => RootsEmpty.Visibility = _roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var settings = host?.SettingsData ?? SettingsStore.Load();
        _hotkey = string.IsNullOrWhiteSpace(settings.GlobalHotkey) ? "Ctrl+Alt+Space" : settings.GlobalHotkey;
        foreach (var root in settings.IndexRoots) AddRoot(root, quiet: true);
        foreach (var path in settings.ExcludedPaths) _excludes.Add(new ExcludeCandidate(path, "已设置", true));
        AutoStartCheck.IsChecked = settings.OnboardingDone ? StartupManager.IsEnabled() : true;
        SpotlightCheck.IsChecked = settings.LaunchMode != "Window";
        ClipboardCheck.IsChecked = settings.ClipboardPrefill;
        DoubleCtrlCheck.IsChecked = settings.DoubleCtrlSummon;
        _localCacheChoice = settings.ThumbnailCacheFolder;
        _teamCache = settings.TeamCacheFolder.Length > 0;
        TeamCacheBox.Text = settings.TeamCacheFolder;

        // 安装包旁边放了预设文件，而且还没设置过：按预设填好，用户照样可以改
        if (!tutorialOnly && settings.IndexRoots.Count == 0 && InstallPreset.Find() is { } preset)
        {
            _preset = preset;
            foreach (var root in preset.IndexRoots) AddRoot(root, quiet: true);
            foreach (var path in preset.ExcludedPaths) _excludes.Add(new ExcludeCandidate(PathRules.Normalize(path), "预设", true));
            if (preset.AutoStart is { } autoStart) AutoStartCheck.IsChecked = autoStart;
            if (preset.ClipboardPrefill is { } clipboard) ClipboardCheck.IsChecked = clipboard;
            if (preset.DoubleCtrlSummon is { } doubleCtrl) DoubleCtrlCheck.IsChecked = doubleCtrl;
            if (!string.IsNullOrWhiteSpace(preset.ThumbnailCacheFolder) && Setup.InstallerEngine.IsLocalPath(preset.ThumbnailCacheFolder!)) _localCacheChoice = preset.ThumbnailCacheFolder!;
            if (!string.IsNullOrWhiteSpace(preset.TeamCacheFolder))
            {
                _teamCache = true;
                TeamCacheBox.Text = TeamThumbnailCache.NormalizeFolder(preset.TeamCacheFolder!);
            }
        }
        ShowCacheChoice();
        BuildKeycaps(HotkeyCaps, _hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), small: true);
        BuildKeycaps(S1Keys, _hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), small: false);
        BuildTutorialRows();
        BuildSearchTiles();

        if (tutorialOnly)
        {
            Title = "共享速搜 · 使用教学";
            HeaderMode.Text = "使用教学";
            _inTutorial = true;
            _flow = _tutorialFlow;
        }
        Flow.BuildDots(Dots, _flow.Count, (Style)FindResource("StepDot"));
        Flow.ShowPageNow(Pages, _flow[0]);
        UpdateChrome();

        Loaded += (_, _) =>
        {
            Flow.PlayOpen(Stage);
            Flow.Stagger(ContentOf(_flow[0]), startMs: 140);
            StartAmbient();
            if (_inTutorial) StartDemo(_flow[0]);
            if (!tutorialOnly) _ = LoadNetworkDrivesAsync();
            if (_host is not null) _host.StatusChanged += OnHostStatus;
            UpdateScan();
        };
    }

    // ================= 翻页 =================

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var page = _flow[_page];

        if (_inTutorial)
        {
            if (_page == _flow.Count - 1) Finish();
            else GoTo(_page + 1);
            return;
        }

        if (ReferenceEquals(page, PageRoots))
        {
            if (_roots.Count == 0)
            {
                if (RootInput.Text.Trim().Length > 0) { AddRootFromInput(); if (_roots.Count == 0) return; }
                else { ShowRootError("至少添加一个目录。暂时不想设置，点“以后再说”。"); Shake(PageRoots); return; }
            }
            GoTo(_page + 1);
            _ = ScanExcludeCandidatesAsync();
            return;
        }
        if (ReferenceEquals(page, PageCache))
        {
            if (!await ValidateCacheAsync()) { Shake(PageCache); return; }
            UpdateReady();
        }
        if (ReferenceEquals(page, PageReady))
        {
            if (!await ApplyAsync()) return;
            EnterTutorial();
            return;
        }
        GoTo(_page + 1);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _page == 0) return;
        GoTo(_page - 1);
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inTutorial) { Finish(); return; }
        // 以后再说：不再自动弹出引导，工作台里有按钮可以重新打开
        _host?.SkipOnboarding();
        _finished = true;
        CloseAnimated(() => _host?.Summon());
    }

    private void GoTo(int index)
    {
        index = Math.Clamp(index, 0, _flow.Count - 1);
        if (index == _page) return;
        var direction = index > _page ? 1 : -1;
        _page = index;
        Flow.ShowPage(Pages, _flow[index], direction);
        Flow.Stagger(ContentOf(_flow[index]), startMs: 110);
        UpdateChrome();
        if (_inTutorial) StartDemo(_flow[index]);
        if (ReferenceEquals(_flow[index], PageRoots)) Dispatcher.InvokeAsync(() => RootInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EnterTutorial()
    {
        _inTutorial = true;
        _flow = _tutorialFlow;
        _page = -1;
        HeaderMode.Text = "使用教学";
        Flow.BuildDots(Dots, _flow.Count, (Style)FindResource("StepDot"));
        GoTo(0);
    }

    private void UpdateChrome()
    {
        Flow.UpdateDots(Dots, Math.Max(0, _page));
        var page = _flow[Math.Max(0, _page)];
        var first = _page <= 0;
        BackButton.Visibility = !first && !ReferenceEquals(page, PageWelcome) ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = _inTutorial ? Visibility.Visible : ReferenceEquals(page, PageWelcome) ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Content = _inTutorial ? "跳过教学" : "以后再说";
        NextButton.Content = _inTutorial
            ? (_page == _flow.Count - 1 ? "开始使用" : "下一个")
            : page switch
            {
                _ when ReferenceEquals(page, PageWelcome) => "开始设置",
                _ when ReferenceEquals(page, PageReady) => "开始扫描",
                _ => "下一步"
            };
        ScanStatus.Visibility = _inTutorial && (_applied || _host?.IsIndexing == true) ? Visibility.Visible : Visibility.Collapsed;
        var glow = ReferenceEquals(page, PageWelcome) || ReferenceEquals(page, PageReady) ? 1 : 0.35;
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(glow, TimeSpan.FromMilliseconds(500)));
    }

    private static IEnumerable<UIElement> ContentOf(FrameworkElement page)
    {
        if (page is not Panel grid || grid.Children.Count == 0) return Array.Empty<UIElement>();
        // 引导页：第一个子元素是内容 StackPanel；教学页：左插画 + 右文字
        if (grid.Children.Count >= 2 && grid.Children[1] is Panel text && Grid.GetColumn(grid.Children[1]) == 1)
            return new[] { grid.Children[0] }.Concat(text.Children.Cast<UIElement>()).ToList();
        if (grid.Children[0] is Panel stack) return stack.Children.Cast<UIElement>().Concat(grid.Children.Cast<UIElement>().Skip(1)).ToList();
        return grid.Children.Cast<UIElement>().ToList();
    }

    // ================= 第 1 步：共享盘目录 =================

    private void RootInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        RootPlaceholder.Visibility = RootInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RootError.Visibility = Visibility.Collapsed;
    }

    private void RootInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddRootFromInput();
        e.Handled = true;
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e) => AddRootFromInput();

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择要索引的共享盘文件夹（也可以直接在输入框粘贴 \\\\服务器\\共享 路径）", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        AddRoot(dialog.SelectedPath);
    }

    private void Suggest_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DriveSuggestion suggestion) AddRoot(suggestion.Path);
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RootCandidate root) _roots.Remove(root);
        RefreshSuggestions();
    }

    private void AddRootFromInput()
    {
        var text = RootInput.Text.Trim().Trim('"');
        if (text.Length == 0) { RootInput.Focus(); return; }
        if (!System.IO.Path.IsPathRooted(text) || text.StartsWith(@"\") && !text.StartsWith(@"\\"))
        {
            ShowRootError("要写完整路径：网络共享以 \\\\ 开头（\\\\NAS\\设计部），映射的盘以盘符开头（Z:\\素材）。");
            Shake(PageRoots);
            return;
        }
        if (AddRoot(text)) RootInput.Clear();
    }

    private bool AddRoot(string path, bool quiet = false)
    {
        var normalized = PathRules.Normalize(path);
        if (normalized.Length == 0) return false;
        if (_roots.Any(x => x.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            if (!quiet) ShowRootError("这个目录已经加过了。");
            return false;
        }
        var parent = _roots.FirstOrDefault(x => PathRules.IsSameOrChild(normalized, x.Path));
        if (parent is not null && !quiet)
        {
            ShowRootError($"它在 {parent.Path} 里面，已经会被索引到了。");
            return false;
        }
        var candidate = new RootCandidate(normalized);
        _roots.Add(candidate);
        _ = CheckRootAsync(candidate);
        RefreshSuggestions();
        return true;
    }

    /// <summary>检查能不能连上：后台列一下第一层文件夹，最多等 8 秒（离线的 NAS 可能卡很久）。</summary>
    private static async Task CheckRootAsync(RootCandidate root)
    {
        root.State = RootState.Checking;
        root.StateText = "正在检查…";
        var check = Task.Run(() =>
        {
            if (!Directory.Exists(root.Path)) return -1;
            try { return Directory.EnumerateDirectories(root.Path).Take(999).Count(); }
            catch (UnauthorizedAccessException) { return -2; }
        });
        var finished = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(8)));
        if (!ReferenceEquals(finished, check))
        {
            root.State = RootState.Failed;
            root.StateText = "连接超时";
            return;
        }
        int count;
        try { count = await check; } catch { count = -1; }
        (root.State, root.StateText) = count switch
        {
            -1 => (RootState.Failed, "连不上，检查路径或网络"),
            -2 => (RootState.Failed, "没有访问权限"),
            >= 999 => (RootState.Ok, "可以访问 · 999+ 个文件夹"),
            _ => (RootState.Ok, $"可以访问 · {count} 个文件夹")
        };
    }

    private void ShowRootError(string message)
    {
        RootError.Text = message;
        RootError.Visibility = Visibility.Visible;
    }

    private List<DriveSuggestion> _drives = new();

    /// <summary>列出这台电脑上映射好的网络盘，一键添加（优先用 UNC 路径，不依赖盘符）。</summary>
    private async Task LoadNetworkDrivesAsync()
    {
        try
        {
            _drives = await Task.Run(() =>
            {
                var list = new List<DriveSuggestion>();
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Network) continue;
                    var letter = drive.Name.TrimEnd('\\');
                    var unc = GetUncPath(letter);
                    list.Add(new DriveSuggestion(unc is null ? letter : $"{letter}  {unc}", unc ?? drive.Name));
                }
                return list;
            });
        }
        catch { _drives = new List<DriveSuggestion>(); }
        RefreshSuggestions();
    }

    private void RefreshSuggestions()
    {
        var available = _drives.Where(d => !_roots.Any(r => r.Path.Equals(PathRules.Normalize(d.Path), StringComparison.OrdinalIgnoreCase))).ToList();
        SuggestList.ItemsSource = available;
        SuggestPanel.Visibility = available.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string? GetUncPath(string driveLetter)
    {
        try
        {
            var length = 512;
            var buffer = new StringBuilder(length);
            return WNetGetConnection(driveLetter, buffer, ref length) == 0 ? buffer.ToString() : null;
        }
        catch { return null; }
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    // ================= 第 2 步：不录入的文件夹 =================

    /// <summary>NAS 常见的系统目录：默认排除。</summary>
    private static readonly Dictionary<string, string> KnownJunk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#recycle"] = "NAS 回收站", ["@Recycle"] = "NAS 回收站", ["$RECYCLE.BIN"] = "回收站", [".Trash"] = "回收站", [".Trashes"] = "回收站",
        ["@eaDir"] = "群晖缩略图缓存", ["@Recently-Snapshot"] = "快照", ["#snapshot"] = "快照", [".snapshot"] = "快照", ["~snapshot"] = "快照",
        ["System Volume Information"] = "系统文件夹", ["lost+found"] = "系统文件夹", ["@tmp"] = "临时文件", [".AppleDouble"] = "Mac 隐藏文件",
        [".@__thumb"] = "NAS 缩略图缓存", ["@__thumb"] = "NAS 缩略图缓存", [".streams"] = "系统文件夹"
    };

    /// <summary>名字像备份、旧版的：只提示，不默认排除。</summary>
    private static readonly string[] MaybeArchive = { "备份", "backup", "旧版", "旧稿", "归档", "archive", "废弃", "old" };

    private async Task ScanExcludeCandidatesAsync()
    {
        var reachable = _roots.Where(x => x.State != RootState.Failed).Select(x => x.Path).ToList();
        var signature = string.Join("|", reachable);
        if (signature == _scannedSignature && _excludes.Count > 0) return;
        _scannedSignature = signature;
        _excludeScan?.Cancel();
        var cancellation = _excludeScan = new CancellationTokenSource();

        // 保留手动加的和之前就设置的
        var keep = _excludes.Where(x => x.Reason is "手动添加" or "已设置").ToList();
        _excludes.Clear();
        foreach (var item in keep) _excludes.Add(item);

        ExcludeLoading.Visibility = Visibility.Visible;
        ExcludeLoadingText.Text = "正在读取共享盘的第一层目录…";
        LoadingDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        var found = new List<ExcludeCandidate>();
        foreach (var root in reachable)
        {
            var list = Task.Run(() =>
            {
                try { return Directory.EnumerateDirectories(root).Take(300).ToList(); }
                catch { return new List<string>(); }
            });
            if (!ReferenceEquals(await Task.WhenAny(list, Task.Delay(TimeSpan.FromSeconds(8))), list)) continue;
            if (cancellation.IsCancellationRequested) return;
            foreach (var folder in await list)
            {
                var name = System.IO.Path.GetFileName(folder);
                if (_excludes.Any(x => x.Path.Equals(folder, StringComparison.OrdinalIgnoreCase))) continue;
                if (KnownJunk.TryGetValue(name, out var reason) || name.StartsWith(".Trash", StringComparison.OrdinalIgnoreCase))
                    found.Add(new ExcludeCandidate(folder, reason ?? "回收站", true));
                else if (MaybeArchive.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new ExcludeCandidate(folder, "像是备份", false));
                else
                    found.Add(new ExcludeCandidate(folder, "", false));
            }
        }
        if (cancellation.IsCancellationRequested) return;

        // 默认排除的在前，再是像备份的，其余按名称排
        foreach (var item in found.OrderBy(x => x.IsExcluded ? 0 : x.Reason.Length > 0 ? 1 : 2).ThenBy(x => x.Name, NaturalStringComparer.Current))
            _excludes.Add(item);

        LoadingDot.BeginAnimation(OpacityProperty, null);
        if (reachable.Count == 0)
            ExcludeLoadingText.Text = "共享盘现在连不上，读不到目录列表。可以在下面手动添加要排除的文件夹，或者直接下一步。";
        else if (_excludes.Count == 0)
            ExcludeLoadingText.Text = "第一层没有文件夹。需要排除更深的子文件夹，在下面添加。";
        else
        {
            ExcludeLoading.Visibility = Visibility.Collapsed;
            var junk = _excludes.Count(x => x.IsExcluded && x.Reason is not ("手动添加" or "已设置"));
            ExcludeLead.Text = junk > 0
                ? $"打开开关的文件夹不会进索引。我在第一层找到 {_excludes.Count} 个文件夹，其中 {junk} 个是回收站、缩略图缓存这类，已经先帮你打开了。"
                : $"打开开关的文件夹不会进索引。第一层一共 {_excludes.Count} 个文件夹，没有发现回收站这类系统目录。";
        }
    }

    private void ExcludeInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ExcludePlaceholder.Visibility = ExcludeInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExcludeError.Visibility = Visibility.Collapsed;
    }

    private void ExcludeInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddExcludeFromInput();
        e.Handled = true;
    }

    private void AddExclude_Click(object sender, RoutedEventArgs e) => AddExcludeFromInput();

    private void BrowseExclude_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择不要录入的子文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            InitialDirectory = _roots.FirstOrDefault(x => x.State == RootState.Ok)?.Path ?? ""
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        ExcludeInput.Text = dialog.SelectedPath;
        AddExcludeFromInput();
    }

    private void AddExcludeFromInput()
    {
        var path = PathRules.Normalize(ExcludeInput.Text.Trim().Trim('"'));
        if (path.Length == 0) return;
        if (!_roots.Any(r => PathRules.IsSameOrChild(path, r.Path) && !path.Equals(r.Path, StringComparison.OrdinalIgnoreCase)))
        {
            ExcludeError.Text = "这个文件夹不在上一步添加的目录里面，不用排除。";
            ExcludeError.Visibility = Visibility.Visible;
            Shake(PageExclude);
            return;
        }
        var existing = _excludes.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) existing.IsExcluded = true;
        else _excludes.Insert(0, new ExcludeCandidate(path, "手动添加", true));
        ExcludeLoading.Visibility = Visibility.Collapsed;
        ExcludeInput.Clear();
    }

    // ================= 第 4 步：缩略图缓存 =================

    private InstallPreset? _preset;

    private void CacheLocalCard_Click(object sender, MouseButtonEventArgs e) => SelectCache(team: false);

    private void CacheTeamCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (_teamCache) return;
        SelectCache(team: true);
        Dispatcher.InvokeAsync(() => TeamCacheBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SelectCache(bool team)
    {
        _teamCache = team;
        // 第一次选全组共用：按第一个共享盘目录给个建议位置
        if (team && TeamCacheBox.Text.Trim().Length == 0 && TeamThumbnailCache.SuggestFor(_roots.Select(x => x.Path)) is { } suggestion)
            TeamCacheBox.Text = suggestion;
        ShowCacheChoice();
        if (team) Ui.FadeIn(CacheTeamPanel, offsetY: 6, milliseconds: 220);
    }

    private void ShowCacheChoice()
    {
        CacheLocalHint.Text = $"{ThumbnailService.ResolveLocalFolder(_localCacheChoice)} · 上限 2 GB，满了自动删最久没看的";
        CacheLocalDot.Visibility = _teamCache ? Visibility.Collapsed : Visibility.Visible;
        CacheTeamDot.Visibility = _teamCache ? Visibility.Visible : Visibility.Collapsed;
        CacheTeamPanel.Visibility = _teamCache ? Visibility.Visible : Visibility.Collapsed;
        CacheLocalCard.SetResourceReference(Border.BorderBrushProperty, _teamCache ? "SpotlightTileBrush" : "AccentBrush");
        CacheTeamCard.SetResourceReference(Border.BorderBrushProperty, _teamCache ? "AccentBrush" : "SpotlightTileBrush");
        CacheLocalRing.SetResourceReference(Shape.StrokeProperty, _teamCache ? "TextTertiaryBrush" : "AccentBrush");
        CacheTeamRing.SetResourceReference(Shape.StrokeProperty, _teamCache ? "AccentBrush" : "TextTertiaryBrush");
    }

    private void ChangeLocalCache_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择本机缩略图缓存的位置（会在里面建一个“共享速搜缩略图”文件夹）", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        if (!Setup.InstallerEngine.IsLocalPath(dialog.SelectedPath))
        {
            ModernDialog.Show(this, "本机缓存要放在这台电脑的硬盘上。想全组共用，选下面的“全组共用一份”。", "缓存位置", MessageBoxImage.Warning);
            return;
        }
        _localCacheChoice = string.Equals(ThumbnailService.ResolveLocalFolder(dialog.SelectedPath), ThumbnailService.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? "" : dialog.SelectedPath;
        SelectCache(team: false);
    }

    private void TeamCacheBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _teamProbe = null;
        SetTeamStatus("还没检查", "TextTertiaryBrush");
    }

    private async void CheckTeamCache_Click(object sender, RoutedEventArgs e) => await ProbeTeamCacheAsync();

    private async Task<TeamCacheState?> ProbeTeamCacheAsync()
    {
        var path = TeamCacheBox.Text.Trim();
        if (path.Length == 0) { SetTeamStatus("填一个共享盘上的位置，比如 \\\\NAS\\设计部", "WarningBrush"); return null; }
        if (Setup.InstallerEngine.IsLocalPath(path)) { SetTeamStatus("这是本机硬盘上的位置，同事访问不到。要选共享盘上的。", "WarningBrush"); return null; }
        SetTeamStatus("正在检查…", "AccentBrush");
        var probe = new TeamThumbnailCache(path);
        var state = await probe.ProbeAsync();
        TeamCacheBox.TextChanged -= TeamCacheBox_TextChanged;
        TeamCacheBox.Text = probe.Folder;
        TeamCacheBox.TextChanged += TeamCacheBox_TextChanged;
        _teamProbe = state;
        SetTeamStatus(probe.Describe(), state switch { TeamCacheState.ReadWrite => "SuccessBrush", TeamCacheState.ReadOnly => "AccentBrush", _ => "WarningBrush" });
        return state;
    }

    /// <summary>选了全组共用：没检查过就先检查；连不上就不往下走（可以改选“只存在这台电脑上”）。</summary>
    private async Task<bool> ValidateCacheAsync()
    {
        if (!_teamCache) return true;
        _busy = true;
        try
        {
            var state = _teamProbe ?? await ProbeTeamCacheAsync();
            if (state is TeamCacheState.ReadWrite or TeamCacheState.ReadOnly) return true;
            if (state == TeamCacheState.Offline) SetTeamStatus("连不上这个文件夹。检查路径，或者先选“只存在这台电脑上”，以后在设置里再开。", "WarningBrush");
            // NoPermission：ProbeTeamCacheAsync 已经显示了“换一个能写入的子文件夹”
            return false;
        }
        finally { _busy = false; }
    }

    private void SetTeamStatus(string text, string brush)
    {
        TeamCacheStatus.Text = text;
        TeamCacheDot.SetResourceReference(Shape.FillProperty, brush);
    }

    // ================= 第 4 步 → 开始扫描 =================

    private void UpdateReady()
    {
        var roots = _roots.Count;
        var excluded = _excludes.Count(x => x.IsExcluded);
        var online = _roots.Count(x => x.State == RootState.Ok);
        ReadyRoots.Text = roots.ToString();
        ReadyExcludes.Text = excluded.ToString();
        ReadyOnline.Text = online.ToString();
        var offline = roots - online;
        var cache = _teamCache ? "缩略图全组共用。" : "缩略图存在这台电脑上。";
        ReadySummary.Text = offline > 0
            ? $"将索引 {roots} 个目录，跳过 {excluded} 个文件夹，{cache}其中 {offline} 个目录现在连不上，这次先跳过；连上之后在工作台点刷新按钮扫描一次就好。"
            : $"将索引 {roots} 个目录，跳过 {excluded} 个文件夹，{cache}";
    }

    private async Task<bool> ApplyAsync()
    {
        if (_host is null || _applied) return true;
        _busy = true;
        NextButton.Content = "正在保存…";
        try
        {
            await _host.ApplyOnboardingAsync(new OnboardingChoices(
                _roots.Select(x => x.Path).ToList(),
                _excludes.Where(x => x.IsExcluded).Select(x => x.Path).ToList(),
                AutoStartCheck.IsChecked == true,
                ClipboardCheck.IsChecked == true,
                SpotlightCheck.IsChecked == true,
                DoubleCtrlCheck.IsChecked == true,
                _localCacheChoice,
                _teamCache ? TeamThumbnailCache.NormalizeFolder(TeamCacheBox.Text.Trim()) : "",
                _preset?.UpdateFeed));
            _applied = true;
            return true;
        }
        catch (Exception ex)
        {
            // 保存失败：留在这一页，说清楚原因，可以再点一次
            ReadySummary.Text = "没能保存设置：" + ex.Message + "。可以再点一次“开始扫描”，或者关掉后从托盘菜单重新打开。";
            Shake(PageReady);
            return false;
        }
        finally { _busy = false; UpdateChrome(); }
    }

    // ================= 扫描进度（教学页底栏） =================

    private void OnHostStatus() => Dispatcher.InvokeAsync(UpdateScan);

    private void UpdateScan()
    {
        if (_host is null) return;
        ScanStatus.Visibility = _inTutorial && (_applied || _host.IsIndexing) ? Visibility.Visible : Visibility.Collapsed;
        ScanText.Text = _host.IsIndexing ? _host.StatusMessage : $"{_host.IndexSummary}  ·  {_host.StatusMessage}";
        ScanDot.SetResourceReference(Shape.FillProperty, _host.StatusWarning ? "WarningBrush" : _host.IsIndexing ? "AccentBrush" : "SuccessBrush");
        ScanDot.BeginAnimation(OpacityProperty, _host.IsIndexing
            ? new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    // ================= 结束 =================

    private void Finish()
    {
        _finished = true;
        CloseAnimated(() => { if (_host is not null) _host.Summon(); });
    }

    private bool _closing;

    private void CloseAnimated(Action? after = null)
    {
        if (_closing) return;
        _closing = true;
        Flow.PlayClose(Stage, () =>
        {
            Close();
            after?.Invoke();
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // 关掉也告诉一声：软件还在托盘里，没设置完下次打开会接着引导
        CloseAnimated(() => _host?.ShowTrayTipOnce());
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Flow.DragWindow(this, e);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_busy) { CloseButton_Click(this, e); e.Handled = true; return; }
        // 文字输入框以外：回车 = 下一步，← → 翻教学卡片
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Enter) { NextButton_Click(this, e); e.Handled = true; }
        else if (_inTutorial && e.Key == Key.Right) { NextButton_Click(this, e); e.Handled = true; }
        else if (_inTutorial && e.Key == Key.Left) { BackButton_Click(this, e); e.Handled = true; }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        StopDemos();
        _excludeScan?.Cancel();
        if (_host is not null) _host.StatusChanged -= OnHostStatus;
    }

    // ================= 教学卡片的动画 =================

    private void StartDemo(FrameworkElement page)
    {
        StopDemos();
        if (ReferenceEquals(page, TutorialSummon)) DemoSummon();
        else if (ReferenceEquals(page, TutorialSearch)) DemoSearch();
        else if (ReferenceEquals(page, TutorialDrag)) DemoDrag();
        else if (ReferenceEquals(page, TutorialPreview)) DemoPreview();
        else if (ReferenceEquals(page, TutorialKeys)) DemoWorkbench();
    }

    /// <summary>按下快捷键 → 搜索条弹出 → 停一会 → 收起，循环。</summary>
    private void DemoSummon()
    {
        const double period = 3.6;
        var keys = S1Keys.Children.OfType<Border>().ToList();
        for (var i = 0; i < keys.Count; i++)
        {
            var shift = EnsureTranslate(keys[i]);
            var down = 0.35 + 0.13 * i;
            Loop(shift, TranslateTransform.YProperty, period, (0, 0), (down, 0), (down + 0.07, 2.5), (1.35, 2.5), (1.45, 0));
        }
        Loop(S1Bar, OpacityProperty, period, (0, 0), (0.8, 0), (1.05, 1), (2.8, 1), (3.1, 0));
        Loop(S1BarScale, ScaleTransform.ScaleXProperty, period, (0, 0.94), (0.8, 0.94), (1.15, 1), (2.8, 1), (3.1, 0.96));
        Loop(S1BarScale, ScaleTransform.ScaleYProperty, period, (0, 0.94), (0.8, 0.94), (1.15, 1), (2.8, 1), (3.1, 0.96));
    }

    /// <summary>逐字输入“14-5 psd”，识别出 PSD 胶囊，结果卡片出现；再输入“bdt 本周”。</summary>
    private void DemoSearch()
    {
        const double period = 6.2;
        LoopText(S2Typed, period,
            (0, ""), (0.3, "1"), (0.42, "14"), (0.54, "14-"), (0.66, "14-5"), (0.86, "14-5 "), (1.0, "14-5 p"), (1.12, "14-5 ps"), (1.24, "14-5 psd"),
            (3.0, ""), (3.3, "b"), (3.42, "bd"), (3.54, "bdt"), (3.74, "bdt "), (3.9, "bdt 本"), (4.06, "bdt 本周"), (6.0, ""));
        LoopText(S2ChipText, period, (0, "PSD"), (3.0, "本周"));
        Loop(S2Chip, OpacityProperty, period, (0, 0), (1.3, 0), (1.45, 1), (2.85, 1), (3.0, 0), (4.1, 0), (4.25, 1), (5.85, 1), (6.0, 0));
        Loop(S2Caret, OpacityProperty, 1.0, (0, 1), (0.5, 1), (0.52, 0), (0.98, 0), (1.0, 1));
        var tiles = S2Tiles.Children.OfType<Border>().ToList();
        for (var i = 0; i < tiles.Count; i++)
        {
            var a = 0.75 + i * 0.08;
            var b = 4.1 + i * 0.08;
            Loop(tiles[i], OpacityProperty, period, (0, 0), (a, 0), (a + 0.2, 1), (2.85, 1), (3.0, 0), (b, 0), (b + 0.2, i == 3 ? 0 : 1), (5.85, i == 3 ? 0 : 1), (6.0, 0));
            var shift = EnsureTranslate(tiles[i]);
            Loop(shift, TranslateTransform.YProperty, period, (0, 8), (a, 8), (a + 0.35, 0), (2.85, 0), (3.0, 8), (b, 8), (b + 0.35, 0), (6.0, 0));
        }
    }

    /// <summary>从卡片拖出一份到右边的文件夹：原卡片留在原地，目标处出现“+ 复制”。</summary>
    private void DemoDrag()
    {
        const double period = 3.4;
        Loop(S3Ghost, OpacityProperty, period, (0, 0), (0.35, 0), (0.45, 0.95), (1.55, 0.95), (1.8, 0));
        Loop(S3GhostMove, TranslateTransform.XProperty, period, (0, 0), (0.45, 0), (1.5, 193), (1.8, 193), (1.81, 0));
        Loop(S3GhostMove, TranslateTransform.YProperty, period, (0, 0), (0.45, 0), (1.5, 51), (1.8, 51), (1.81, 0));
        Loop(S3Badge, OpacityProperty, period, (0, 0), (1.25, 0), (1.45, 1), (2.6, 1), (2.9, 0));
        Loop(S3TargetScale, ScaleTransform.ScaleXProperty, period, (0, 1), (1.5, 1), (1.65, 1.06), (1.85, 1));
        Loop(S3TargetScale, ScaleTransform.ScaleYProperty, period, (0, 1), (1.5, 1), (1.65, 1.06), (1.85, 1));
        Loop(S3Source, OpacityProperty, period, (0, 1), (0.4, 1), (0.5, 0.55), (1.8, 0.55), (2.0, 1));
    }

    /// <summary>卡片原地放大成预览 → 滚轮放大细节 → 缩回去。</summary>
    private void DemoPreview()
    {
        const double period = 4.6;
        Loop(S4Big, OpacityProperty, period, (0, 0), (0.3, 0), (0.45, 1), (3.9, 1), (4.15, 0));
        Loop(S4BigScale, ScaleTransform.ScaleXProperty, period, (0, 0.18), (0.3, 0.18), (0.8, 1), (3.5, 1), (4.05, 0.18));
        Loop(S4BigScale, ScaleTransform.ScaleYProperty, period, (0, 0.33), (0.3, 0.33), (0.8, 1), (3.5, 1), (4.05, 0.33));
        Loop(S4ArtScale, ScaleTransform.ScaleXProperty, period, (0, 1), (1.4, 1), (1.95, 1.8), (2.9, 1.8), (3.3, 1));
        Loop(S4ArtScale, ScaleTransform.ScaleYProperty, period, (0, 1), (1.4, 1), (1.95, 1.8), (2.9, 1.8), (3.3, 1));
        Loop(S4Zoom, OpacityProperty, period, (0, 0), (1.45, 0), (1.6, 1), (2.95, 1), (3.15, 0));
    }

    /// <summary>搜索条原地变大成工作台，右边出现详情栏。</summary>
    private void DemoWorkbench()
    {
        const double period = 4.2;
        Loop(S5PanelScale, ScaleTransform.ScaleXProperty, period, (0, 0.72), (0.5, 0.72), (1.05, 1), (3.3, 1), (3.8, 0.72));
        Loop(S5PanelScale, ScaleTransform.ScaleYProperty, period, (0, 0.24), (0.5, 0.24), (1.05, 1), (3.3, 1), (3.8, 0.24));
        Loop(S5Content, OpacityProperty, period, (0, 0), (1.0, 0), (1.25, 1), (3.15, 1), (3.35, 0));
        Loop(S5Side, OpacityProperty, period, (0, 0), (1.35, 0), (1.6, 1), (3.1, 1), (3.3, 0));
    }

    private void Loop(IAnimatable target, DependencyProperty property, double periodSeconds, params (double Time, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(periodSeconds), RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (time, value) in frames)
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time)), Flow.EaseOut));
        target.BeginAnimation(property, animation);
        _demos.Add((target, property));
    }

    private void LoopText(TextBlock target, double periodSeconds, params (double Time, string Value)[] frames)
    {
        var animation = new StringAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(periodSeconds), RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (time, value) in frames)
            animation.KeyFrames.Add(new DiscreteStringKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time))));
        target.BeginAnimation(TextBlock.TextProperty, animation);
        _demos.Add((target, TextBlock.TextProperty));
    }

    private void StopDemos()
    {
        foreach (var (target, property) in _demos) target.BeginAnimation(property, null);
        _demos.Clear();
    }

    private static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } shift) return shift;
        shift = new TranslateTransform();
        element.RenderTransform = shift;
        return shift;
    }

    // ================= 搭建演示元素 =================

    private static void BuildKeycaps(Panel panel, IEnumerable<string> keys, bool small)
    {
        panel.Children.Clear();
        foreach (var key in keys)
        {
            var label = key switch { "Space" => "Space", "Shift" => "⇧ Shift", "Win" => "⊞ Win", _ => key };
            var cap = new Border
            {
                MinWidth = small ? 30 : label.Length > 3 ? 96 : 52,
                Padding = small ? new Thickness(8, 3, 8, 3) : new Thickness(12, 8, 12, 8),
                Child = new TextBlock { Text = label, FontSize = small ? 11.5 : 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }
            };
            cap.SetResourceReference(StyleProperty, "DemoKey");
            panel.Children.Add(cap);
        }
    }

    private void BuildSearchTiles()
    {
        var colors = new[] { "#2457E6", "#7B5CF0", "#E39A1C", "#22A55B" };
        var labels = new[] { "PSD", "PSD", "JPG", "AI" };
        for (var i = 0; i < 4; i++)
        {
            var tile = new Border { CornerRadius = new CornerRadius(9), Margin = new Thickness(4, 0, 4, 0), Opacity = 0 };
            tile.SetResourceReference(Border.BackgroundProperty, "SpotlightPanelBrush");
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(7),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])),
                Child = new TextBlock { Text = labels[i], FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
            };
            var art = new Border
            {
                CornerRadius = new CornerRadius(6), Width = 30, Height = 30, Opacity = 0.35,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 0)
            };
            tile.Child = new Grid { Children = { art, badge } };
            S2Tiles.Children.Add(tile);
        }
    }

    private void BuildTutorialRows()
    {
        AddKeyRow(S4Keys, new[] { "空格" }, "打开 / 关闭预览");
        AddKeyRow(S4Keys, new[] { "滚轮" }, "以鼠标位置为中心放大缩小，放大后按住拖动");
        AddKeyRow(S4Keys, new[] { "双击" }, "在适应窗口和原始尺寸之间切换");
        AddKeyRow(S4Keys, new[] { "←", "→" }, "上一张 / 下一张");
        AddKeyRow(S5Keys, new[] { "↵" }, "打开所在位置（选中文件夹就直接打开它）");
        AddKeyRow(S5Keys, new[] { "双击" }, "打开文件（键盘上是 Ctrl + ↵）");
        AddKeyRow(S5Keys, new[] { "Ctrl", "⇧", "↵" }, "展开 / 收回工作台");
        AddKeyRow(S5Keys, new[] { "Esc" }, "先清空搜索，再按一次收起");
    }

    private static void AddKeyRow(Panel panel, string[] keys, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
        var caps = new StackPanel { Orientation = Orientation.Horizontal, MinWidth = 104 };
        foreach (var key in keys)
        {
            var cap = new Border { MinWidth = 28, Padding = new Thickness(7, 2, 7, 2), Child = new TextBlock { Text = key, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center } };
            cap.SetResourceReference(StyleProperty, "DemoKey");
            caps.Children.Add(cap);
        }
        var label = new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 250 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        row.Children.Add(caps);
        row.Children.Add(label);
        panel.Children.Add(row);
    }

    // ================= 小动效 =================

    private void StartAmbient()
    {
        GlowShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-60, 40, TimeSpan.FromSeconds(12)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        GlowShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-20, 50, TimeSpan.FromSeconds(9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    private static void Shake(UIElement element)
    {
        var shift = EnsureTranslate(element);
        var shake = new DoubleAnimationUsingKeyFrames();
        foreach (var (ms, x) in new[] { (0, 0.0), (60, -8.0), (120, 7.0), (180, -5.0), (240, 3.0), (300, 0.0) })
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        shift.BeginAnimation(TranslateTransform.XProperty, shake);
    }
}
