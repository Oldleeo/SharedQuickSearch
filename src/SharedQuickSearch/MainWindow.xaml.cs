using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SharedQuickSearch;

public partial class MainWindow : Window
{
    private enum EmptyAction { None, Settings, Rebuild }

    private readonly FileIndexService _index = new();
    private readonly ThumbnailService _thumbnailService;
    private readonly GlobalHotkeyManager _globalHotkey = new();
    private readonly IndexMonitorService _monitor;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _reconcileTimer;
    private readonly DispatcherTimer _videoTimer;
    private readonly DispatcherTimer _thumbnailTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _prefetchTimer;
    private Forms.NotifyIcon? _trayIcon;
    private CancellationTokenSource? _indexCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private CancellationTokenSource? _previewCancellation;
    private ScrollViewer? _listScroll;
    private ScrollViewer? _gridScroll;
    private IReadOnlyList<FileEntry> _results = Array.Empty<FileEntry>();
    private SearchCategory _category = SearchCategory.All;
    private EmptyAction _emptyAction;
    private FileEntry? _selectedEntry;
    private int _previewRequest;
    private int _offlineRoots;
    private bool _initialized;
    private bool _allowClose;
    private bool _syncingSelection;
    private bool _restoringView;
    private bool _videoPlaying;
    private bool _videoSliderUpdating;
    private bool _uiReady;
    private bool _silentRebuild;
    private DateTime _lastOverflowReconcile = DateTime.MinValue;

    public AppSettings SettingsData { get; private set; }
    public FileIndexService Index => _index;
    public ThumbnailService Thumbnails => _thumbnailService;
    /// <summary>文件夹缩略图：用索引里的图拼出来，不让 Windows 遍历共享盘。</summary>
    public FolderPreviewService Folders { get; }

    private SpotlightWindow? _spotlight;
    private SpotlightWindow Spotlight => _spotlight ??= new SpotlightWindow(this);
    private readonly DispatcherTimer _recentSaveTimer;
    private readonly DispatcherTimer _quickReconcileTimer;

    public MainWindow(AppSettings settings)
    {
        SettingsData = settings;
        if (SettingsData.UiRevision < 5)
        {
            // 1.5 新界面：默认缩略图视图、显示预览、跟随系统主题（旧版本强制浅色，不是用户选择）。
            SettingsData.ResultView = "LargeIcons";
            SettingsData.PreviewVisible = true;
            SettingsData.Theme = ThemeManager.FollowSystem;
            SettingsData.UiRevision = 5;
            try { SettingsStore.Save(SettingsData); } catch { }
            ThemeManager.Apply(SettingsData.Theme);
        }

        _thumbnailService = new ThumbnailService(SettingsData.ThumbnailCacheLimitMb, SettingsData.ThumbnailCacheFolder) { PrefetchEnabled = SettingsData.ThumbnailPrefetch };
        _thumbnailService.SetTeamFolder(SettingsData.TeamCacheFolder);

        Folders = new FolderPreviewService(_index, _thumbnailService);
        _thumbnailService.Refined += (entry, image) => Dispatcher.InvokeAsync(() =>
        {
            // 低清内嵌图精修完成：只替换还在界面上的条目
            if (entry.Thumbnail is not null) entry.Thumbnail = image;
        });
        // 精修只做还在当前结果里的条目（后台线程调用；_results 是不可变数组，整体替换）
        _thumbnailService.RefineFilter = entry => (_mainVisible && Volatile.Read(ref _results).Contains(entry)) || (_spotlight?.Shows(entry) ?? false);

        _monitor = new IndexMonitorService(_index);
        _monitor.IndexChanged += count => Dispatcher.InvokeAsync(async () =>
        {
            UpdateIndexSummary(_index.Count);
            await RefreshResultsAsync(preserve: true);
            if (_spotlight is not null) await _spotlight.RefreshAsync();
            SetStatus($"实时同步：{count:N0} 项有变化");
        });

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RefreshResultsAsync(); };
        // 定期校准：每 2 小时（加随机错峰，避免全组同一时刻扫 NAS），用快速模式——目录没变就不列内容
        _reconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(120 + Random.Shared.Next(0, 30)) };
        _reconcileTimer.Tick += async (_, _) => { if (_indexCancellation is null && SettingsData.IndexRoots.Count > 0) await RebuildIndexAsync(silent: true); };
        // 监听丢了事件（缓冲溢出、网络闪断）：等 20 秒没有新的再快速校准一次
        _quickReconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _quickReconcileTimer.Tick += async (_, _) =>
        {
            _quickReconcileTimer.Stop();
            _quickReconcileTimer.Interval = TimeSpan.FromSeconds(20);
            if (_indexCancellation is not null) { _quickReconcileTimer.Start(); return; }
            _lastOverflowReconcile = DateTime.UtcNow;
            if (SettingsData.IndexRoots.Count > 0) await RebuildIndexAsync(silent: true);
        };
        _monitor.NeedsReconcile += _ => Dispatcher.InvokeAsync(() =>
        {
            // 开了全组缓存时，同事写缓存会让监听更容易溢出：这种校准最多 30 分钟一次。
            // 不是丢掉，而是推到 30 分钟窗口结束时再做（至少等 20 秒），变化照样会被补上
            var wait = TimeSpan.FromSeconds(20);
            if (SettingsData.TeamCacheFolder.Length > 0)
            {
                var due = _lastOverflowReconcile + TimeSpan.FromMinutes(30) - DateTime.UtcNow;
                if (due > wait) wait = due;
                // 已经排好了更晚的一次：不用再往后推
                if (_quickReconcileTimer.IsEnabled) return;
            }
            _quickReconcileTimer.Stop();
            _quickReconcileTimer.Interval = wait;
            _quickReconcileTimer.Start();
        });
        _recentSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _recentSaveTimer.Tick += (_, _) => { _recentSaveTimer.Stop(); try { SettingsStore.Save(SettingsData); } catch { } };
        _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _videoTimer.Tick += (_, _) => UpdateVideoPosition();
        _thumbnailTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(70) };
        _thumbnailTimer.Tick += (_, _) => { _thumbnailTimer.Stop(); LoadVisibleThumbnails(); };
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1900) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideToast(); };
        _prefetchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _prefetchTimer.Tick += (_, _) => UpdatePrefetchText();

        InitializeComponent();

        Width = Math.Max(MinWidth, SettingsData.WindowWidth);
        Height = Math.Max(MinHeight, SettingsData.WindowHeight);
        ApplyPreviewVisibility(SettingsData.PreviewVisible, animate: false);
        ApplyResultView(SettingsData.ResultView.Equals("LargeIcons", StringComparison.OrdinalIgnoreCase), animate: false);
        SelectSort(SettingsData.SortMode);
        UpdateThemeButton();
        UpdateHotkeyHint();
        ResetPreview("选择一个文件查看预览");
        _globalHotkey.Pressed += (_, _) => Summon(fromHotkey: true);
        ThemeManager.Changed += OnThemeChanged;
        IsVisibleChanged += (_, _) => _mainVisible = IsVisible;
        _uiReady = true;
    }

    // ================= 生命周期 =================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this, ThemeManager.IsDarkActive);
        InitializeTray();
        RegisterGlobalHotkey(showFailure: false);
        _ = InitializeDataAsync();
        _updates = new UpdateService(() => SettingsData.UpdateFeed, CanApplyUpdateNow, ApplyUpdate);
        _updates.Start();
        _doubleCtrl = new DoubleCtrlHook(Dispatcher);
        _doubleCtrl.DoubleTapped += () => { if (SettingsData.DoubleCtrlSummon) Summon(fromHotkey: true); };
        ApplyDoubleCtrl(showFailure: false);
    }

    /// <summary>开机启动：只建窗口句柄（托盘、快捷键需要），不显示窗口。</summary>
    public void InitializeHidden() => new WindowInteropHelper(this).EnsureHandle();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Ui.FadeIn(RootContent, offsetY: 10, milliseconds: 360);
        SearchBox.Focus();
    }

    private async Task InitializeDataAsync()
    {
        if (_initialized) return;
        _initialized = true;
        SetStatus("正在载入本地索引…", busy: true);
        var count = await _index.LoadAsync(SettingsData.ExcludedPaths);
        IndexLoaded = true;
        UpdateIndexSummary(count);
        await RefreshResultsAsync();
        // 聚光模式下搜索条可能比索引先出来（剪贴板预填、手快先打了字）：索引到了再搜一遍
        if (_spotlight is not null) await _spotlight.RefreshAsync();
        _spotlight?.RestoreTray();

        if (SettingsData.IndexRoots.Count == 0) SetStatus("请先添加共享盘索引目录");
        else if (count == 0) SetStatus("索引目录已配置，等待首次扫描");
        else SetStatus($"已载入 {count:N0} 条索引，可以开始搜索");

        // 共享盘在线检查放到后台：NAS 离线时 Directory.Exists 可能卡几十秒，不能堵住界面。
        await RefreshRootStateAsync();
        StartRealtimeMonitoring();
        StartThumbnailPrefetch();
    }

    // ================= 缩略图后台预热 =================

    private void StartThumbnailPrefetch()
    {
        _thumbnailService.PrefetchEnabled = SettingsData.ThumbnailPrefetch;
        if (!SettingsData.ThumbnailPrefetch || _index.Count == 0)
        {
            _thumbnailService.StopPrefetch();
            UpdatePrefetchText();
            return;
        }
        _thumbnailService.StartPrefetch(_index.SnapshotEntries());
        _prefetchTimer.Start();
    }

    private void UpdatePrefetchText()
    {
        var stats = _thumbnailService.GetStats();
        if (!stats.PrefetchRunning || stats.PrefetchTotal == 0)
        {
            PrefetchText.Visibility = Visibility.Collapsed;
            if (!stats.PrefetchRunning) _prefetchTimer.Stop();
            return;
        }
        PrefetchText.Visibility = Visibility.Visible;
        PrefetchText.Text = $"缩略图预热 {stats.PrefetchDone * 100.0 / stats.PrefetchTotal:F0}%";
        PrefetchText.ToolTip = $"空闲时提前生成缩略图并存到本机：{stats.PrefetchDone:N0} / {stats.PrefetchTotal:N0}\n你在浏览时会自动暂停，不占用带宽。可在设置 → 缩略图里关闭。";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && SettingsData.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            ShowTrayTip();
            return;
        }
        _indexCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _thumbnailCancellation?.Cancel();
        _previewCancellation?.Cancel();
        StopVideo();
        _globalHotkey.Dispose();
        _monitor.Dispose();
        _reconcileTimer.Stop();
        _quickReconcileTimer.Stop();
        _recentSaveTimer.Stop();
        _prefetchTimer.Stop();
        _updates?.Dispose();
        _doubleCtrl?.Dispose();
        _thumbnailService.Dispose();
        _index.Dispose(); // 把还没写盘的增量变化存下来
        _spotlight?.CloseForExit();
        _spotlight = null;
        ThemeManager.Changed -= OnThemeChanged;
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
        // WindowWidth / WindowHeight 现在是工作台的尺寸，由工作台拖动改大小时保存；这个窗口不显示，不能拿它的尺寸覆盖
        if (SettingsData.PreviewVisible && PreviewColumn.ActualWidth > 0) SettingsData.PreviewWidth = PreviewColumn.ActualWidth;
        try { SettingsStore.Save(SettingsData); } catch { }
        // 应用是显式退出模式：主窗口真正关闭就结束进程
        Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }

    // ================= 自动更新 =================

    private UpdateService? _updates;

    /// <summary>
    /// 什么时候可以重启换版本：搜索条收起、收集栏是空的、没在拖文件、没有设置之类的窗口开着、没在完整扫描。
    /// 用户离开电脑超过 10 分钟时，只要没在拖文件、没有对话框，就直接更新。
    /// 收集栏会存到本机、重启后恢复；但里面有东西时说明用户可能正挑到一半，要他离开 2 分钟以上才更新。
    /// </summary>
    private bool CanApplyUpdateNow()
    {
        if (_spotlight is { IsDraggingOut: true }) return false;
        if (_spotlight is { HasCollected: true } && IdleTime() < TimeSpan.FromMinutes(2)) return false;
        var otherWindows = Application.Current.Windows.OfType<Window>()
            .Any(x => x.IsVisible && !ReferenceEquals(x, this) && !ReferenceEquals(x, _spotlight));
        if (otherWindows) return false;
        if (IdleTime() >= TimeSpan.FromMinutes(10)) return true;
        return !IsVisible && _spotlight is not { IsVisible: true } && (!IsIndexing || _silentRebuild);
    }

    private void ApplyUpdate(UpdateManifest manifest, string path)
    {
        try { _trayIcon?.ShowBalloonTip(2500, $"正在更新到 {manifest.Version}", "几秒后自动重新打开，不用管它。", Forms.ToolTipIcon.None); } catch { }
        App.StartUpdater(path, show: false);
        ExitApplication();
    }

    /// <summary>刚被自动更新换上来：托盘提示一下。</summary>
    public void NotifyUpdated(string fromVersion)
    {
        // 从 1.11 之前升上来：说一下新增的、需要自己打开的功能
        var news = UpdateRules.Compare(fromVersion, "1.11.0") < 0
            ? "新增：双击 Ctrl 呼出、全组共用缩略图缓存，都在设置里打开。收集栏现在退出后也会保留。"
            : $"按 {SettingsData.GlobalHotkey} 呼出。设置 → 关于 里可以看到更新状态。";
        Dispatcher.InvokeAsync(() =>
        {
            try { _trayIcon?.ShowBalloonTip(6000, $"共享速搜已更新到 {UpdateService.CurrentVersion}", news, Forms.ToolTipIcon.None); }
            catch { }
        }, DispatcherPriority.ApplicationIdle);
    }

    // ================= 双击 Ctrl、缓存位置 =================

    private DoubleCtrlHook? _doubleCtrl;

    private void ApplyDoubleCtrl(bool showFailure)
    {
        if (_doubleCtrl is null) return;
        if (_doubleCtrl.SetEnabled(SettingsData.DoubleCtrlSummon) || !showFailure) return;
        ModernDialog.Show(DialogOwner, "双击 Ctrl 没能开启，可能被安全软件拦下了。原来的快捷键照常能用。", "双击 Ctrl", MessageBoxImage.Warning);
    }

    /// <summary>设置里改了本机缓存位置或全组缓存：马上生效。</summary>
    private void ApplyCacheLocations()
    {
        if (_thumbnailService.MoveLocalCache(SettingsData.ThumbnailCacheFolder)) StartThumbnailPrefetch();
        _thumbnailService.SetTeamFolder(SettingsData.TeamCacheFolder);
    }

    /// <summary>索引里能出缩略图的文件数（估算全组缓存大小用）。</summary>
    public int PreviewableCount() => _index.SnapshotEntries().Count(x => !x.IsDirectory && FileTypes.HasVisualPreview(x.Extension));

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>距离上一次键盘 / 鼠标操作多久了（整台电脑，不只是这个软件）。</summary>
    private static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    private bool _trayTipShown;
    private void ShowTrayTip()
    {
        if (_trayTipShown || _trayIcon is null) return;
        _trayTipShown = true;
        try { _trayIcon.ShowBalloonTip(2500, "共享速搜仍在后台运行", $"按 {SettingsData.GlobalHotkey} 随时呼出", Forms.ToolTipIcon.None); } catch { }
    }

    // ================= 搜索 =================

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = SearchBox.Text.Length == 0;
        SearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        HotkeyKeycap.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Down)) return;
        e.Handled = true;
        if (e.Key == Key.Enter || _searchTimer.IsEnabled)
        {
            _searchTimer.Stop();
            await RefreshResultsAsync();
        }
        var list = ActiveList;
        if (list.Items.Count == 0) return;
        if (list.SelectedIndex < 0) list.SelectedIndex = 0;
        FocusSelected(list);
    }

    private void SearchBox_FocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        var focused = SearchBox.IsKeyboardFocusWithin;
        SearchRing.BeginAnimation(OpacityProperty, new DoubleAnimation(focused ? 1 : 0, TimeSpan.FromMilliseconds(focused ? 160 : 220)));
        SearchShell.SetResourceReference(Border.BorderBrushProperty, focused ? "AccentBrush" : "StrokeStrongBrush");
        SearchIcon.SetResourceReference(TextBlock.ForegroundProperty, focused ? "AccentBrush" : "TextTertiaryBrush");
    }

    private void SearchShell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SearchBox.IsKeyboardFocusWithin) return;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }

    private async Task RefreshResultsAsync(bool preserve = false)
    {
        // 这个窗口不显示时不做搜索：省一次全量搜索，也不替它的 500 条结果占着缩略图
        if (!_mainVisible) return;
        _searchCancellation?.Cancel();
        var cancellation = _searchCancellation = new CancellationTokenSource();
        var query = SearchBox.Text;
        var category = _category;
        var sort = Enum.TryParse<SearchSort>(SettingsData.SortMode, out var parsedSort) ? parsedSort : SearchSort.Relevance;
        var limit = SettingsData.ResultLimit;

        IReadOnlyList<FileEntry> results;
        try { results = await Task.Run(() => _index.Search(query, category, limit, sort, sort == SearchSort.Modified), cancellation.Token); }
        catch (OperationCanceledException) { return; }
        if (cancellation.IsCancellationRequested) return;

        // 实时同步触发的刷新：结果没变就什么都不动，不打断正在看的人。
        if (preserve && SameResults(_results, results)) return;
        await ApplyResultsAsync(results, preserve, query, limit);
    }

    private static bool SameResults(IReadOnlyList<FileEntry> a, IReadOnlyList<FileEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (ReferenceEquals(a[i], b[i])) continue;
            if (!a[i].FullPath.Equals(b[i].FullPath, StringComparison.OrdinalIgnoreCase) || a[i].LastWriteUtc != b[i].LastWriteUtc || a[i].Size != b[i].Size) return false;
        }
        return true;
    }

    private async Task ApplyResultsAsync(IReadOnlyList<FileEntry> results, bool preserve, string query, int limit)
    {
        var list = ActiveList;
        var keepPaths = preserve ? new HashSet<string>(SelectedEntries.Select(x => x.FullPath), StringComparer.OrdinalIgnoreCase) : null;
        var primaryPath = preserve ? _selectedEntry?.FullPath : null;
        var scrollOffset = preserve ? GetScrollViewer(list)?.VerticalOffset : null;

        // 离开结果集的条目释放缩略图引用（缓存里还有，回来时瞬间显示），内存不会随浏览一直涨。
        var incoming = new HashSet<FileEntry>(results, ReferenceEqualityComparer.Instance);
        foreach (var entry in _results) if (!incoming.Contains(entry) && !(_spotlight?.Shows(entry) ?? false)) entry.Thumbnail = null;
        foreach (var entry in results)
            if (entry.Thumbnail is null && _thumbnailService.TryGetCached(entry, out var cached)) entry.Thumbnail = cached;

        _results = results;
        FileEntry? newPrimary = null;
        _syncingSelection = true;
        try
        {
            ResultsList.ItemsSource = results;
            LargeResultsList.ItemsSource = results;
            if (keepPaths is { Count: > 0 })
            {
                foreach (var entry in results)
                {
                    if (!keepPaths.Contains(entry.FullPath)) continue;
                    list.SelectedItems.Add(entry);
                    if (primaryPath is not null && entry.FullPath.Equals(primaryPath, StringComparison.OrdinalIgnoreCase)) newPrimary = entry;
                }
                var other = ReferenceEquals(list, ResultsList) ? (ListBox)LargeResultsList : ResultsList;
                other.SelectedItem = newPrimary;
            }
        }
        finally { _syncingSelection = false; }

        if (newPrimary is not null && _selectedEntry is not null)
        {
            var changed = newPrimary.LastWriteUtc != _selectedEntry.LastWriteUtc || newPrimary.Size != _selectedEntry.Size;
            SetSelectedEntry(newPrimary);
            if (changed && SettingsData.PreviewVisible) await ShowPreviewAsync(newPrimary);
            else UpdatePreviewMeta(newPrimary);
        }
        else if (_selectedEntry is not null)
        {
            SetSelectedEntry(null);
            ++_previewRequest;
            ResetPreview("选择一个文件查看预览");
        }

        var trimmed = results.Count >= limit && !string.IsNullOrWhiteSpace(query);
        ResultSummaryText.Text = string.IsNullOrWhiteSpace(query)
            ? $"最近更新 · {results.Count:N0} 项"
            : trimmed ? $"前 {results.Count:N0} 项" : $"{results.Count:N0} 项";
        UpdateEmptyState(results.Count, query);

        if (scrollOffset is { } offset)
        {
            _ = Dispatcher.InvokeAsync(() => GetScrollViewer(list)?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        }
        else if (results.Count > 0)
        {
            GetScrollViewer(list)?.ScrollToTop();
            Ui.FadeIn(list, offsetY: 5, milliseconds: 180);
        }
        ScheduleThumbnails();
    }

    private void UpdateEmptyState(int count, string query)
    {
        if (count > 0) { EmptyState.Visibility = Visibility.Collapsed; return; }
        var wasHidden = EmptyState.Visibility != Visibility.Visible;
        EmptyState.Visibility = Visibility.Visible;

        if (SettingsData.IndexRoots.Count == 0)
            SetEmpty("", "添加第一个共享盘目录", "共享速搜会把目录建成本机索引，之后每次搜索都只查本机，不再遍历共享盘。", "打开设置", EmptyAction.Settings);
        else if (_index.Count == 0 && _indexCancellation is not null)
            SetEmpty("", "正在建立索引", "首次扫描的时间取决于文件数量和网络速度，扫描完成前可以随时停止。", null, EmptyAction.None);
        else if (_index.Count == 0)
            SetEmpty("", "索引还没建立", "目录已经配置好，开始扫描后就能搜索。扫描只读取文件信息，不会改动任何文件。", "开始扫描", EmptyAction.Rebuild);
        else if (string.IsNullOrWhiteSpace(query))
            SetEmpty("", "这个分类下还没有文件", "换一个分类，或者直接输入 SKU 搜索。", null, EmptyAction.None);
        else
            SetEmpty("", $"没有找到“{query.Trim()}”", "试试只输入 SKU 主体，比如 14-5。连字符、下划线和空格可以互相匹配。", null, EmptyAction.None);

        if (wasHidden) Ui.FadeIn(EmptyState, offsetY: 8, milliseconds: 260);
    }

    private void SetEmpty(string glyph, string title, string description, string? action, EmptyAction kind)
    {
        EmptyGlyph.Text = glyph;
        EmptyTitle.Text = title;
        EmptyDescription.Text = description;
        _emptyAction = kind;
        EmptyActionButton.Content = action ?? "";
        EmptyActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void EmptyAction_Click(object sender, RoutedEventArgs e)
    {
        if (_emptyAction == EmptyAction.Settings) OpenSettings();
        else if (_emptyAction == EmptyAction.Rebuild) await RebuildIndexAsync();
    }

    private async void Category_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string value } || !Enum.TryParse<SearchCategory>(value, out var category)) return;
        _category = category;
        if (_initialized) await RefreshResultsAsync();
    }

    private void SelectSort(string value)
    {
        foreach (var item in SortCombo.Items.OfType<ComboBoxItem>())
            if ((item.Tag as string)?.Equals(value, StringComparison.OrdinalIgnoreCase) == true) { SortCombo.SelectedItem = item; return; }
    }

    private async void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return; // InitializeComponent 期间的默认选中不能覆盖已保存的排序
        if (SortCombo.SelectedItem is ComboBoxItem { Tag: string sort }) SettingsData.SortMode = sort;
        if (_initialized) await RefreshResultsAsync();
    }

    // ================= 视图 =================

    private ListBox ActiveList => LargeResultsList.Visibility == Visibility.Visible ? LargeResultsList : ResultsList;

    private IReadOnlyList<FileEntry> SelectedEntries
    {
        get
        {
            var selected = ActiveList.SelectedItems.OfType<FileEntry>().ToArray();
            return selected.Length > 0 ? selected : _selectedEntry is null ? Array.Empty<FileEntry>() : new[] { _selectedEntry };
        }
    }

    private ScrollViewer? GetScrollViewer(ListBox list)
    {
        if (ReferenceEquals(list, ResultsList)) return _listScroll ??= Ui.FindDescendant<ScrollViewer>(ResultsList);
        return _gridScroll ??= Ui.FindDescendant<ScrollViewer>(LargeResultsList);
    }

    private void ViewToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_restoringView) return;
        ApplyResultView(GridViewToggle.IsChecked == true, animate: true);
    }

    private void ApplyResultView(bool grid, bool animate)
    {
        SettingsData.ResultView = grid ? "LargeIcons" : "List";
        _restoringView = true;
        GridViewToggle.IsChecked = grid;
        ListViewToggle.IsChecked = !grid;
        _restoringView = false;

        LargeResultsList.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        var target = grid ? (ListBox)LargeResultsList : ResultsList;
        if (_selectedEntry is not null)
        {
            _syncingSelection = true;
            try { target.SelectedItem = _selectedEntry; target.ScrollIntoView(_selectedEntry); }
            finally { _syncingSelection = false; }
        }
        if (animate) Ui.FadeIn(target, offsetY: 6, milliseconds: 220);
        ScheduleThumbnails();
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e) => ApplyPreviewVisibility(PreviewToggleButton.IsChecked == true, animate: true);

    private void ApplyPreviewVisibility(bool visible, bool animate)
    {
        if (!visible && PreviewColumn.ActualWidth > 0) SettingsData.PreviewWidth = PreviewColumn.ActualWidth;
        SettingsData.PreviewVisible = visible;
        PreviewToggleButton.IsChecked = visible;
        PreviewColumn.Width = visible ? new GridLength(Math.Clamp(SettingsData.PreviewWidth, 300, 760)) : new GridLength(0);
        PreviewPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible && animate) Ui.FadeIn(PreviewPane, offsetY: 0, milliseconds: 220);
        if (!visible) StopVideo();
        ScheduleThumbnails();
    }

    private void PreviewSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SettingsData.PreviewWidth = PreviewColumn.ActualWidth;
        ScheduleThumbnails();
    }

    // ================= 缩略图：只加载看得见的 =================

    private void Results_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0 || e.ViewportWidthChange != 0) ScheduleThumbnails();
    }

    private void ScheduleThumbnails()
    {
        _thumbnailTimer.Stop();
        _thumbnailTimer.Start();
    }

    private void LoadVisibleThumbnails()
    {
        if (_results.Count == 0 || !IsVisible) return;
        var list = ActiveList;
        var scroll = GetScrollViewer(list);
        if (scroll is null || scroll.ActualHeight <= 0) return;

        // 可视区 + 下方多预取 0.75 屏，向下滚动时图已经在路上了。
        var ahead = scroll.ActualHeight * 0.75;
        var window = new Rect(0, -ahead * 0.25, scroll.ActualWidth, scroll.ActualHeight + ahead);
        var wanted = new List<FileEntry>();
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container || !container.IsVisible) continue;
            Rect bounds;
            try { bounds = container.TransformToAncestor(scroll).TransformBounds(new Rect(container.RenderSize)); }
            catch (InvalidOperationException) { continue; }
            if (bounds.Top > window.Bottom) break;
            if (bounds.IntersectsWith(window) && list.Items[i] is FileEntry { Thumbnail: null } entry) wanted.Add(entry);
        }

        _thumbnailCancellation?.Cancel();
        if (wanted.Count == 0) return;
        var cancellation = _thumbnailCancellation = new CancellationTokenSource();
        foreach (var entry in wanted) _ = LoadThumbnailAsync(entry, cancellation.Token);
    }

    private async Task LoadThumbnailAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var image = await _thumbnailService.LoadAsync(entry, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && image is not null) entry.Thumbnail = image;
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ================= 选择与预览 =================

    private async void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        await SelectEntryAsync(ResultsList.SelectedItem as FileEntry, fromGrid: false);
    }

    private async void LargeResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        await SelectEntryAsync(LargeResultsList.SelectedItem as FileEntry, fromGrid: true);
    }

    private async Task SelectEntryAsync(FileEntry? entry, bool fromGrid)
    {
        _syncingSelection = true;
        try
        {
            if (fromGrid) ResultsList.SelectedItem = entry;
            else LargeResultsList.SelectedItem = entry;
        }
        finally { _syncingSelection = false; }

        if (entry is not null && ReferenceEquals(entry, _selectedEntry)) { UpdateActionState(); return; }
        SetSelectedEntry(entry);
        if (entry is null)
        {
            ++_previewRequest;
            ResetPreview("选择一个文件查看预览");
            return;
        }
        if (SettingsData.PreviewVisible) await ShowPreviewAsync(entry);
        else UpdatePreviewMeta(entry);
    }

    private void SetSelectedEntry(FileEntry? entry)
    {
        if (_selectedEntry is not null) _selectedEntry.PropertyChanged -= SelectedEntry_PropertyChanged;
        _selectedEntry = entry;
        if (entry is not null) entry.PropertyChanged += SelectedEntry_PropertyChanged;
        UpdateActionState();
    }

    private void SelectedEntry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileEntry.ResolutionLabel) || sender is not FileEntry entry) return;
        Dispatcher.InvokeAsync(() => { if (ReferenceEquals(entry, _selectedEntry) && !FileTypes.Videos.Contains(entry.Extension)) PreviewResolution.Text = entry.ResolutionLabel; });
    }

    private void UpdateActionState()
    {
        var enabled = _selectedEntry is not null;
        OpenButton.IsEnabled = enabled;
        RevealButton.IsEnabled = enabled;
        CopyItemButton.IsEnabled = enabled;
        CopyPathButton.IsEnabled = enabled;
    }

    private async Task ShowPreviewAsync(FileEntry entry)
    {
        var request = ++_previewRequest;
        _previewCancellation?.Cancel();
        var cancellation = _previewCancellation = new CancellationTokenSource();
        StopVideo();
        ClearPreviewSurface();
        UpdatePreviewMeta(entry);
        Ui.FadeIn(PreviewInfo, offsetY: 4, milliseconds: 200);
        PreviewGlyph.Text = entry.IconGlyph;
        PreviewGlyph.Foreground = entry.TypeBrush;
        PreviewMessage.Text = "正在载入预览…";

        var isVideo = FileTypes.Videos.Contains(entry.Extension);
        var shownQuick = false;
        if (entry.Thumbnail is not null && !isVideo)
        {
            // 先用已有缩略图垫底，大图到了再无缝换清晰版。
            ShowImage(entry.Thumbnail, animate: true);
            shownQuick = true;
        }

        try
        {
            if (entry.IsDirectory)
            {
                var folder = await _thumbnailService.LoadShellPreviewAsync(entry.FullPath, 512, cancellation.Token);
                if (request != _previewRequest) return;
                if (folder is not null) ShowImage(folder, animate: !shownQuick);
                else ShowPlaceholder("文件夹\n双击打开查看内容");
                return;
            }

            if (isVideo)
            {
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                PreviewVideo.Visibility = Visibility.Visible;
                VideoControlPanel.Visibility = Visibility.Visible;
                PreviewVideo.Source = new Uri(entry.FullPath, UriKind.Absolute);
                PreviewVideo.Play();
                PreviewVideo.Pause();
                Ui.FadeIn(PreviewVideo, offsetY: 0, milliseconds: 220);
                return;
            }

            if (FileTypes.Text.Contains(entry.Extension))
            {
                var text = await ReadTextPreviewAsync(entry.FullPath, cancellation.Token);
                if (request != _previewRequest) return;
                PreviewText.Text = text;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                PreviewTextScroll.Visibility = Visibility.Visible;
                PreviewTextScroll.ScrollToTop();
                Ui.FadeIn(PreviewTextScroll, offsetY: 4, milliseconds: 200);
                return;
            }

            if (FileTypes.Images.Contains(entry.Extension))
            {
                var loaded = await _thumbnailService.LoadPreviewImageAsync(entry.FullPath, 1800, cancellation.Token);
                if (request != _previewRequest) return;
                if (loaded.Image is not null)
                {
                    if (entry.Width == 0 && loaded.Width > 0)
                    {
                        entry.Width = loaded.Width;
                        entry.Height = loaded.Height;
                        entry.Notify(nameof(FileEntry.ResolutionLabel));
                    }
                    ShowImage(loaded.Image, animate: !shownQuick);
                    return;
                }
            }

            var shell = await _thumbnailService.LoadShellPreviewAsync(entry.FullPath, 900, cancellation.Token);
            if (request != _previewRequest) return;
            if (shell is not null) ShowImage(shell, animate: !shownQuick);
            else if (!shownQuick) ShowPlaceholder("系统没有这个格式的预览组件\n双击用默认软件打开");
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (request == _previewRequest && !shownQuick) ShowPlaceholder("无法读取这个文件\n可能已被移动，或共享盘暂时不可访问");
        }
    }

    private static async Task<string> ReadTextPreviewAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32 * 1024, true);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 32 * 1024, leaveOpen: true);
        var buffer = new char[120_000];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return new string(buffer, 0, count) + (reader.EndOfStream ? "" : "\n\n—— 预览已截断 ——");
    }

    private void UpdatePreviewMeta(FileEntry entry)
    {
        PreviewName.Text = entry.Name;
        PreviewPath.Text = entry.FullPath;
        var badge = entry.IsDirectory ? "文件夹"
            : entry.TypeLabel.Equals(entry.ExtensionLabel, StringComparison.OrdinalIgnoreCase) ? entry.ExtensionLabel
            : $"{entry.ExtensionLabel} · {entry.TypeLabel}";
        PreviewTypeText.Text = badge;
        PreviewTypeBadge.Background = entry.TypeBrush;
        PreviewTypeText.Foreground = Brushes.White;
        PreviewSize.Text = entry.SizeLabel;
        PreviewModified.Text = entry.ModifiedLabel;
        PreviewResolution.Text = entry.ResolutionLabel;
        PreviewOfflineChip.Visibility = entry.IsOffline ? Visibility.Visible : Visibility.Collapsed;
        var sku = SkuDetector.Find(entry.Name);
        RelatedSkuButton.Tag = sku;
        RelatedSkuText.Text = sku is null ? "" : $"查看同 SKU · {sku}";
        RelatedSkuButton.Visibility = sku is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowImage(BitmapSource image, bool animate)
    {
        PreviewImage.Source = image;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        if (animate) Ui.ZoomIn(PreviewImage);
    }

    private void ShowPlaceholder(string message)
    {
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewMessage.Text = message;
        Ui.FadeIn(PreviewPlaceholder, offsetY: 4, milliseconds: 200);
    }

    private void ClearPreviewSurface()
    {
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewTextScroll.Visibility = Visibility.Collapsed;
        PreviewText.Text = "";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        VideoControlPanel.Visibility = Visibility.Collapsed;
    }

    private void ResetPreview(string message)
    {
        _previewCancellation?.Cancel();
        StopVideo();
        ClearPreviewSurface();
        PreviewMessage.Text = message;
        PreviewGlyph.Text = "";
        PreviewGlyph.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        PreviewName.Text = "未选择文件";
        PreviewPath.Text = "在左侧点选文件，这里会显示大图和文件信息";
        PreviewTypeText.Text = "未选择";
        PreviewTypeBadge.SetResourceReference(Border.BackgroundProperty, "ControlAltBrush");
        PreviewTypeText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        PreviewSize.Text = "—";
        PreviewModified.Text = "—";
        PreviewResolution.Text = "—";
        PreviewOfflineChip.Visibility = Visibility.Collapsed;
        RelatedSkuButton.Visibility = Visibility.Collapsed;
    }

    private void RelatedSku_Click(object sender, RoutedEventArgs e)
    {
        if (RelatedSkuButton.Tag is not string sku) return;
        SearchBox.Text = sku;
        SearchBox.CaretIndex = sku.Length;
    }

    private void PreviousPreview_Click(object sender, RoutedEventArgs e) => MoveSelection(-1);
    private void NextPreview_Click(object sender, RoutedEventArgs e) => MoveSelection(1);

    private void MoveSelection(int delta)
    {
        var list = ActiveList;
        if (list.Items.Count == 0) return;
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + delta, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.SelectedItem);
    }

    private static void FocusSelected(ListBox list)
    {
        if (list.SelectedItem is null) { list.Focus(); return; }
        list.ScrollIntoView(list.SelectedItem);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is UIElement container) container.Focus();
        else list.Focus();
    }

    // ================= 视频 =================

    private void VideoPlayButton_Click(object sender, RoutedEventArgs e) => ToggleVideo();

    private void ToggleVideo()
    {
        if (PreviewVideo.Source is null) return;
        if (_videoPlaying) PreviewVideo.Pause(); else PreviewVideo.Play();
        _videoPlaying = !_videoPlaying;
        VideoPlayButton.Content = _videoPlaying ? "" : "";
        if (_videoPlaying) _videoTimer.Start(); else _videoTimer.Stop();
    }

    private void PreviewVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        StopVideo();
        ShowPlaceholder("Windows 解码器无法播放这个视频\n双击用默认播放器打开");
    }

    private void PreviewVideo_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (PreviewVideo.NaturalDuration.HasTimeSpan)
        {
            var seconds = PreviewVideo.NaturalDuration.TimeSpan.TotalSeconds;
            VideoPositionSlider.Maximum = Math.Max(0.1, seconds);
            if (_selectedEntry is not null) { _selectedEntry.DurationSeconds = seconds; _selectedEntry.Notify(nameof(FileEntry.DurationLabel)); }
        }
        if (_selectedEntry is not null && PreviewVideo.NaturalVideoWidth > 0)
            PreviewResolution.Text = $"{PreviewVideo.NaturalVideoWidth} × {PreviewVideo.NaturalVideoHeight} · {_selectedEntry.DurationLabel}";
        UpdateVideoPosition();
    }

    private void PreviewVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        PreviewVideo.Pause();
        PreviewVideo.Position = TimeSpan.Zero;
        _videoPlaying = false;
        _videoTimer.Stop();
        VideoPlayButton.Content = "";
        UpdateVideoPosition();
    }

    private void StopVideo()
    {
        try { PreviewVideo.Stop(); } catch { }
        PreviewVideo.Source = null;
        PreviewVideo.Visibility = Visibility.Collapsed;
        _videoPlaying = false;
        _videoTimer.Stop();
        VideoPlayButton.Content = "";
        VideoControlPanel.Visibility = Visibility.Collapsed;
        _videoSliderUpdating = true;
        VideoPositionSlider.Value = 0;
        VideoPositionSlider.Maximum = 1;
        VideoTimeText.Text = "00:00 / 00:00";
        _videoSliderUpdating = false;
    }

    private void UpdateVideoPosition()
    {
        if (!PreviewVideo.NaturalDuration.HasTimeSpan) return;
        _videoSliderUpdating = true;
        VideoPositionSlider.Value = PreviewVideo.Position.TotalSeconds;
        VideoTimeText.Text = $"{PreviewVideo.Position:mm\\:ss} / {PreviewVideo.NaturalDuration.TimeSpan:mm\\:ss}";
        _videoSliderUpdating = false;
    }

    private void VideoPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_videoSliderUpdating && PreviewVideo.Source is not null) PreviewVideo.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    // ================= 文件操作 =================

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement((ItemsControl)sender, source) is ListBoxItem)
            RunAction(FileActions.Open, "打开");
    }

    private void Results_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(list, source) is ListBoxItem { IsSelected: false } container)
            list.SelectedItem = list.ItemContainerGenerator.ItemFromContainer(container);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => RunAction(FileActions.Open, "打开");
    private void RevealButton_Click(object sender, RoutedEventArgs e) => RunAction(FileActions.Reveal, "定位");
    private void CopyItemButton_Click(object sender, RoutedEventArgs e) => RunBatchAction(FileActions.CopyItems, "复制文件", n => n == 1 ? "文件已复制，可直接粘贴" : $"已复制 {n} 个文件");
    private void CopyPathButton_Click(object sender, RoutedEventArgs e) => RunBatchAction(FileActions.CopyPaths, "复制路径", n => n == 1 ? "路径已复制" : $"已复制 {n} 条路径");
    private void OpenMenu_Click(object sender, RoutedEventArgs e) => RunAction(FileActions.Open, "打开");
    private void RevealMenu_Click(object sender, RoutedEventArgs e) => RunAction(FileActions.Reveal, "定位");
    private void CopyItemMenu_Click(object sender, RoutedEventArgs e) => CopyItemButton_Click(sender, e);
    private void CopyPathMenu_Click(object sender, RoutedEventArgs e) => CopyPathButton_Click(sender, e);
    private void CopyNameMenu_Click(object sender, RoutedEventArgs e) => RunBatchAction(FileActions.CopyNames, "复制名称", n => n == 1 ? "名称已复制" : $"已复制 {n} 个名称");

    private void RunAction(Action<FileEntry> action, string label)
    {
        if (_selectedEntry is not { } entry) return;
        try
        {
            action(entry);
            SetStatus($"{label}：{entry.Name}");
        }
        catch (Exception ex)
        {
            SetStatus($"{label}失败：{ex.Message}", warning: true);
            ModernDialog.Show(DialogOwner, ex.Message, $"{label}失败", MessageBoxImage.Warning);
        }
    }

    private void RunBatchAction(Action<IEnumerable<FileEntry>> action, string label, Func<int, string> toast)
    {
        var entries = SelectedEntries;
        if (entries.Count == 0) return;
        try
        {
            action(entries);
            SetStatus($"{label}：{entries.Count:N0} 项");
            ShowToast(toast(entries.Count));
        }
        catch (Exception ex)
        {
            SetStatus($"{label}失败：{ex.Message}", warning: true);
            ShowToast($"{label}失败", warning: true);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var typing = Keyboard.FocusedElement is TextBox;
        if (modifiers == ModifierKeys.Control && e.Key is Key.L or Key.F)
        {
            SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            OpenSettings(); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.None && e.Key == Key.F5)
        {
            _ = RebuildIndexAsync(); e.Handled = true;
        }
        else if (typing)
        {
            if (e.Key == Key.Escape && ReferenceEquals(Keyboard.FocusedElement, SearchBox) && SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; }
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C && _selectedEntry is not null)
        {
            CopyItemButton_Click(this, e); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.C && _selectedEntry is not null)
        {
            CopyPathButton_Click(this, e); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.Enter && _selectedEntry is not null)
        {
            RunAction(FileActions.Reveal, "定位"); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.None && e.Key == Key.Enter && _selectedEntry is not null)
        {
            RunAction(FileActions.Open, "打开"); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.None && e.Key == Key.Space && PreviewVideo.Source is not null)
        {
            ToggleVideo(); e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    // ================= 索引 =================

    private async void RebuildButton_Click(object sender, RoutedEventArgs e) => await RebuildIndexAsync();

    private async Task RebuildIndexAsync(bool silent = false)
    {
        if (SettingsData.IndexRoots.Count == 0)
        {
            if (!silent) OpenSettings();
            return;
        }
        if (_indexCancellation is not null)
        {
            if (!silent) _indexCancellation.Cancel();
            return;
        }

        _indexCancellation = new CancellationTokenSource();
        _silentRebuild = silent;
        if (!silent)
        {
            RebuildText.Text = "停止";
            RebuildGlyph.Text = "";
            IndexProgressBar.Visibility = Visibility.Visible;
            IndexProgressBar.IsIndeterminate = true;
            IndexEtaText.Visibility = Visibility.Visible;
            IndexEtaText.Text = "正在估算…";
            UpdateEmptyState(_results.Count, SearchBox.Text);
        }
        SetStatus(silent ? "后台校准索引中…" : "正在扫描共享目录…", busy: true);

        var progress = new Progress<IndexProgress>(value =>
        {
            if (silent) return;
            IndexProgressBar.IsIndeterminate = value.Percent is null;
            if (value.Percent is not null) IndexProgressBar.Value = value.Percent.Value;
            var eta = value.EstimatedRemaining is null ? "估算中" : $"剩余 {FormatCountdown(value.EstimatedRemaining.Value)}";
            var speed = value.ItemsPerSecond <= 0 ? "" : $" · {value.ItemsPerSecond:N0} 项/秒";
            IndexEtaText.Text = $"{(value.Percent is null ? "--" : value.Percent.Value.ToString("F0"))}% · {eta}{speed}";
            SetStatus($"已扫描 {value.ScannedCount:N0} 项 · {value.CurrentPath}", busy: true);
        });

        try
        {
            // 后台校准用快速模式（目录修改时间没变就沿用上次的内容），手动刷新做完整扫描
            var result = await _index.RebuildAsync(SettingsData.IndexRoots, SettingsData.ExcludedPaths, progress, _indexCancellation.Token, quick: silent);
            UpdateIndexSummary(result.EntryCount);
            await RefreshResultsAsync(preserve: true);
            if (_spotlight is not null) await _spotlight.RefreshAsync();
            // 收集栏里上次没找到的文件再找一遍；扫描完全没出错还找不到，才算真的没了
            _spotlight?.ResolvePendingTray(dropMissing: result.Errors.Count == 0);
            if (result.Errors.Count == 0)
            {
                SetStatus($"索引完成：{result.EntryCount:N0} 项，用时 {FormatDuration(result.Elapsed)}");
                if (!silent) ShowToast($"索引完成 · {result.EntryCount:N0} 项");
            }
            else
            {
                SetStatus($"索引完成：{result.EntryCount:N0} 项；{result.Errors.Count} 个位置无法访问", warning: true);
                if (!silent)
                    ModernDialog.Show(DialogOwner, string.Join("\n", result.Errors.Take(8)) + (result.Errors.Count > 8 ? $"\n…另有 {result.Errors.Count - 8} 项" : ""),
                        "部分目录未能索引", MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("扫描已停止，继续使用上一次的完整索引", warning: true);
        }
        catch (Exception ex)
        {
            SetStatus("索引失败：" + ex.Message, warning: true);
            if (!silent) ModernDialog.Show(DialogOwner, ex.Message, "索引失败", MessageBoxImage.Error);
        }
        finally
        {
            _indexCancellation?.Dispose();
            _indexCancellation = null;
            _silentRebuild = false;
            RebuildText.Text = "刷新索引";
            RebuildGlyph.Text = "";
            IndexProgressBar.Visibility = Visibility.Collapsed;
            IndexEtaText.Visibility = Visibility.Collapsed;
            UpdateEmptyState(_results.Count, SearchBox.Text);
            await RefreshRootStateAsync();
            StartRealtimeMonitoring();
            StartThumbnailPrefetch();
        }
    }

    private async Task RefreshRootStateAsync()
    {
        var roots = SettingsData.IndexRoots.ToArray();
        if (roots.Length == 0) { _offlineRoots = 0; UpdateIndexSummary(_index.Count); return; }
        var previousOffline = _offlineRoots;
        _offlineRoots = await Task.Run(() =>
        {
            _index.UpdateRootAvailability(roots);
            return roots.Count(root => { try { return !Directory.Exists(root); } catch { return true; } });
        });
        UpdateIndexSummary(_index.Count);
        // 共享盘上线/掉线：之前因为连不上而没出图的文件重新给机会
        if (previousOffline != _offlineRoots) _thumbnailService.ResetFailures();
        if (_offlineRoots > 0) SetStatus($"{_offlineRoots} 个索引目录当前离线，正在使用上次的快照", warning: true);
    }

    private void StartRealtimeMonitoring()
    {
        _reconcileTimer.Stop();
        var roots = SettingsData.IndexRoots.ToArray();
        var exclusions = SettingsData.ExcludedPaths.ToArray();
        // 在共享盘上建 FileSystemWatcher 也可能阻塞，同样放后台。
        _ = Task.Run(() => _monitor.Start(roots, exclusions));
        _reconcileTimer.Start();
    }

    private void UpdateIndexSummary(int count)
    {
        IndexSummary = count == 0
            ? "尚未建立索引"
            : $"{count:N0} 个文件" + (_offlineRoots > 0 ? $" · {_offlineRoots} 个目录离线" : " · 实时同步");
        IndexSummaryText.Text = IndexSummary;
        StatusChanged?.Invoke();
    }

    // ================= 状态栏 / Toast =================

    private void SetStatus(string message, bool busy = false, bool warning = false)
    {
        StatusMessage = message;
        StatusBusy = busy;
        StatusWarning = warning;
        StatusChanged?.Invoke();
        StatusText.Text = message;
        StatusDot.SetResourceReference(Shape.FillProperty, warning ? "WarningBrush" : busy ? "AccentBrush" : "SuccessBrush");
        StatusDot.BeginAnimation(OpacityProperty, busy
            ? new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private void ShowToast(string message, bool warning = false)
    {
        ToastText.Text = message;
        ToastIcon.Text = warning ? "" : "";
        Toast.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (!_toastTimer.IsEnabled) Toast.Visibility = Visibility.Collapsed; };
        Toast.BeginAnimation(OpacityProperty, fade);
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 6, TimeSpan.FromMilliseconds(220)));
    }

    private static string FormatDuration(TimeSpan value) => value.TotalMinutes >= 1
        ? $"{value.TotalMinutes:F1} 分钟" : $"{Math.Max(0.1, value.TotalSeconds):F1} 秒";

    private static string FormatCountdown(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes:00}:{value.Seconds:00}";

    // ================= 主题 / 设置 =================

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.Current switch
        {
            ThemeManager.Light => ThemeManager.Dark,
            ThemeManager.Dark => ThemeManager.FollowSystem,
            _ => ThemeManager.Light
        };
        SettingsData.Theme = next;
        ThemeManager.Apply(next);
        try { SettingsStore.Save(SettingsData); } catch { }
        ShowToast(next == ThemeManager.Light ? "浅色模式" : next == ThemeManager.Dark ? "深色模式" : "跟随系统主题");
    }

    private void OnThemeChanged()
    {
        UpdateThemeButton();
        WindowEffects.SetDarkMode(this, ThemeManager.IsDarkActive);
    }

    private void UpdateThemeButton()
    {
        if (ThemeManager.Current == ThemeManager.Light) { ThemeButton.Content = "\uE706"; ThemeButton.ToolTip = "浅色 · 点击切换到深色"; }
        else if (ThemeManager.Current == ThemeManager.Dark) { ThemeButton.Content = "\uE708"; ThemeButton.ToolTip = "深色 · 点击切换到跟随系统"; }
        else { ThemeButton.Content = "\uE7F4"; ThemeButton.ToolTip = "跟随系统 · 点击切换到浅色"; }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -150;
        menu.IsOpen = true;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void AboutMenu_Click(object sender, RoutedEventArgs e) => OpenSettings(3);
    private void PrivacyMenu_Click(object sender, RoutedEventArgs e) => new PrivacyWindow { Owner = this }.ShowDialog();
    private void ExitMenu_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private async void OpenSettings(int page = 0, Window? owner = null)
    {
        if (_indexCancellation is not null && _silentRebuild)
        {
            // 后台校准不是用户发起的：打开设置时直接取消，下个周期再校准。
            _indexCancellation.Cancel();
            while (_indexCancellation is not null) await Task.Delay(50);
        }
        if (_indexCancellation is not null)
        {
            ModernDialog.Show(DialogOwner, "请先停止当前的索引扫描，再修改目录设置。", "正在扫描", MessageBoxImage.Information);
            return;
        }

        var oldRoots = SettingsData.IndexRoots.ToArray();
        var oldExclusions = SettingsData.ExcludedPaths.ToArray();
        var window = new SettingsWindow(owner ?? DialogOwner, SettingsData, _thumbnailService, PreviewableCount);
        window.SelectPage(page);
        if (window.ShowDialog() != true || window.SavedSettings is null)
        {
            UpdateThemeButton();
            return;
        }

        SettingsData = window.SavedSettings;
        try { SettingsStore.Save(SettingsData); } catch { }
        ThemeManager.Apply(SettingsData.Theme);
        _thumbnailService.CacheLimitMegabytes = SettingsData.ThumbnailCacheLimitMb;
        RegisterGlobalHotkey(showFailure: true);
        ApplyDoubleCtrl(showFailure: true);
        ApplyCacheLocations();
        UpdateHotkeyHint();
        var removed = await _index.ApplyExclusionsAsync(SettingsData.ExcludedPaths);
        UpdateIndexSummary(_index.Count);
        await RefreshResultsAsync();

        var rootsChanged = !oldRoots.SequenceEqual(SettingsData.IndexRoots, StringComparer.OrdinalIgnoreCase);
        var exclusionsChanged = !oldExclusions.SequenceEqual(SettingsData.ExcludedPaths, StringComparer.OrdinalIgnoreCase);
        SetStatus(removed > 0 ? $"排除规则已生效，从索引清除 {removed:N0} 项" : "设置已保存");
        ShowToast("设置已保存");
        if (rootsChanged || exclusionsChanged) await RebuildIndexAsync();
        else StartThumbnailPrefetch();
        if (_spotlight is not null) await _spotlight.RefreshAsync();
    }

    private void RegisterGlobalHotkey(bool showFailure)
    {
        if (_globalHotkey.Register(this, SettingsData.GlobalHotkey)) return;
        SetStatus($"全局快捷键 {SettingsData.GlobalHotkey} 注册失败，可能被其他软件占用", warning: true);
        if (showFailure)
            ModernDialog.Show(DialogOwner, $"快捷键 {SettingsData.GlobalHotkey} 已被其他软件占用。\n请在设置里换一个组合键。", "快捷键冲突", MessageBoxImage.Warning);
    }

    private void UpdateHotkeyHint()
    {
        HotkeyKeycapText.Text = SettingsData.GlobalHotkey.Replace("+", " ");
        HotkeyKeycap.ToolTip = $"在任何地方按 {SettingsData.GlobalHotkey} 呼出共享速搜";
    }

    // ================= 窗口 / 托盘 =================

    private void InitializeTray()
    {
        if (_trayIcon is not null) return;
        System.Drawing.Icon? icon = null;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("搜索", null, (_, _) => Dispatcher.Invoke(() => Spotlight.Summon()));
        menu.Items.Add("打开工作台", null, (_, _) => Dispatcher.Invoke(() => Spotlight.Summon(workbench: true)));
        menu.Items.Add("刷新索引", null, (_, _) => Dispatcher.InvokeAsync(async () => await RebuildIndexAsync()));
        menu.Items.Add("使用教学", null, (_, _) => Dispatcher.Invoke(ShowTutorial));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon = new Forms.NotifyIcon { Icon = icon, Text = "共享速搜", Visible = true, ContextMenuStrip = menu };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => Spotlight.Summon(workbench: true));
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && SettingsData.MinimizeToTray) { Hide(); return; }
        var maximized = WindowState == WindowState.Maximized;
        RootBorder.Margin = maximized ? WindowEffects.GetMaximizedPadding(this) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        ScheduleThumbnails();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    public void ShowSearch()
    {
        var wasHidden = !IsVisible;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        SearchBox.Focus();
        SearchBox.SelectAll();
        if (wasHidden && IsLoaded) Ui.FadeIn(RootContent, offsetY: 8, milliseconds: 240);
        _mainVisible = true;
        _ = RefreshResultsAsync();
        ScheduleThumbnails();
    }

    // ================= 聚光模式 =================

    /// <summary>快捷键 / 再次打开软件：按设置弹出搜索条或完整窗口。快捷键在搜索条已显示时再按一次会收起。</summary>
    public void Summon() => Summon(fromHotkey: false);

    private void Summon(bool fromHotkey)
    {
        // 搜索条和工作台是同一个窗口；“直接打开工作台”的设置由它自己处理
        if (fromHotkey) Spotlight.Toggle(); else Spotlight.Summon();
    }

    // ================= 给工作台用的接口 =================

    public event Action? StatusChanged;
    public string StatusMessage { get; private set; } = "";
    public bool StatusBusy { get; private set; }
    public bool StatusWarning { get; private set; }
    public string IndexSummary { get; private set; } = "尚未建立索引";
    public bool IsIndexing => _indexCancellation is not null;
    public bool IndexLoaded { get; private set; }

    /// <summary>刷新索引；正在扫描时再按一次是停止。</summary>
    public Task RebuildFromUiAsync() => RebuildIndexAsync();

    public void OpenSettingsFrom(Window owner) => OpenSettings(0, owner);

    public string CycleTheme()
    {
        var next = ThemeManager.Current switch
        {
            ThemeManager.Light => ThemeManager.Dark,
            ThemeManager.Dark => ThemeManager.FollowSystem,
            _ => ThemeManager.Light
        };
        SettingsData.Theme = next;
        ThemeManager.Apply(next);
        SaveSettingsSoon();
        return next == ThemeManager.Light ? "浅色模式" : next == ThemeManager.Dark ? "深色模式" : "跟随系统主题";
    }

    public void SaveSettingsSoon()
    {
        _recentSaveTimer.Stop();
        _recentSaveTimer.Start();
    }

    public void ShowTrayTipOnce() => ShowTrayTip();

    // ================= 首次引导 / 使用教学 =================

    private OnboardingWindow? _onboarding;

    public void ShowOnboarding() => ShowFlow(tutorialOnly: false);
    public void ShowTutorial() => ShowFlow(tutorialOnly: true);

    private void ShowFlow(bool tutorialOnly)
    {
        if (_onboarding is { IsVisible: true }) { _onboarding.Activate(); return; }
        _spotlight?.HideAnimated();
        _onboarding = new OnboardingWindow(this, tutorialOnly);
        _onboarding.Closed += (_, _) => _onboarding = null;
        _onboarding.Show();
        _onboarding.Activate();
    }

    /// <summary>“以后再说”：不再自动弹出引导。</summary>
    public void SkipOnboarding()
    {
        SettingsData.OnboardingDone = true;
        try { SettingsStore.Save(SettingsData); } catch { }
    }

    /// <summary>引导完成：保存目录、排除和选项，然后在后台开始第一次完整扫描。</summary>
    public async Task ApplyOnboardingAsync(OnboardingChoices choices)
    {
        var (roots, exclusions, autoStart, clipboardPrefill, spotlightLaunch) = (choices.Roots, choices.Exclusions, choices.AutoStart, choices.ClipboardPrefill, choices.SpotlightLaunch);
        if (_indexCancellation is not null)
        {
            _indexCancellation.Cancel();
            // 最多等 10 秒：扫描卡在离线的共享盘上时也不会一直转圈
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_indexCancellation is not null && DateTime.UtcNow < deadline) await Task.Delay(50);
        }
        SettingsData.IndexRoots = PathRules.NormalizeDistinct(roots).ToList();
        SettingsData.ExcludedPaths = PathRules.NormalizeDistinct(exclusions).ToList();
        SettingsData.ClipboardPrefill = clipboardPrefill;
        SettingsData.LaunchMode = spotlightLaunch ? "Spotlight" : "Window";
        SettingsData.AutoStart = autoStart && StartupManager.SetEnabled(true);
        if (!autoStart) StartupManager.SetEnabled(false);
        SettingsData.DoubleCtrlSummon = choices.DoubleCtrl;
        SettingsData.ThumbnailCacheFolder = choices.LocalCacheFolder;
        SettingsData.TeamCacheFolder = choices.TeamCacheFolder;
        if (!string.IsNullOrWhiteSpace(choices.UpdateFeed) && string.IsNullOrWhiteSpace(SettingsData.UpdateFeed)) SettingsData.UpdateFeed = choices.UpdateFeed!.Trim();
        SettingsData.OnboardingDone = true;
        try { SettingsStore.Save(SettingsData); } catch { }
        ApplyDoubleCtrl(showFailure: true);
        ApplyCacheLocations();
        await _index.ApplyExclusionsAsync(SettingsData.ExcludedPaths);
        UpdateIndexSummary(_index.Count);
        _ = RebuildWhenIdleAsync();
    }

    /// <summary>上一次扫描还没停下来时，等它停了再开始（不阻塞界面）。</summary>
    private async Task RebuildWhenIdleAsync()
    {
        while (_indexCancellation is not null) await Task.Delay(200);
        await RebuildIndexAsync();
    }

    public void ExitApp() => ExitApplication();

    /// <summary>对话框挂在当前看得见的窗口上（旧主窗口不再显示）。</summary>
    private Window? DialogOwner => IsVisible ? this : _spotlight is { IsVisible: true } spotlight ? spotlight : null;

    // 1.7.1 起旧主窗口不再显示（只做后台宿主），它的结果列表不再占着缩略图
    private volatile bool _mainVisible;
    public bool IsShownInMainWindow(FileEntry entry) => _mainVisible && Volatile.Read(ref _results).Contains(entry);

    /// <summary>记录最近用过的文件和关键词（只存在本机设置里）。</summary>
    public void RecordRecent(string query, IEnumerable<FileEntry> entries)
    {
        query = query.Trim();
        if (query.Length > 0)
        {
            SettingsData.RecentQueries.RemoveAll(x => x.Equals(query, StringComparison.OrdinalIgnoreCase));
            SettingsData.RecentQueries.Insert(0, query);
            if (SettingsData.RecentQueries.Count > 12) SettingsData.RecentQueries.RemoveRange(12, SettingsData.RecentQueries.Count - 12);
        }
        foreach (var entry in entries.Reverse())
        {
            SettingsData.RecentFiles.RemoveAll(x => x.Equals(entry.FullPath, StringComparison.OrdinalIgnoreCase));
            SettingsData.RecentFiles.Insert(0, entry.FullPath);
        }
        if (SettingsData.RecentFiles.Count > 30) SettingsData.RecentFiles.RemoveRange(30, SettingsData.RecentFiles.Count - 30);
        _recentSaveTimer.Stop();
        _recentSaveTimer.Start();
    }

    /// <summary>最近使用的文件（已经不在索引里的跳过）。</summary>
    public IReadOnlyList<FileEntry> RecentEntries()
    {
        var list = new List<FileEntry>();
        foreach (var path in SettingsData.RecentFiles)
            if (_index.TryGet(path, out var entry) && entry is not null) list.Add(entry);
        return list;
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
        Application.Current.Shutdown();
    }
}
