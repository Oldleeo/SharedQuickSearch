using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SharedQuickSearch;

/// <summary>
/// 聚光模式：按快捷键或打开软件时只出现一条搜索框；输入后向下展开成缩略图网格。
/// 动画原则：展开/收起只改裁剪区域，卡片只动透明度和位移，滚动用动画驱动偏移量，
/// 都不触发重新排版，帧率跟随显示器刷新率（见 FrameRate）。
/// </summary>
public partial class SpotlightWindow : Window
{
    private const double RowHeight = 208;
    private const double CompactWidth = 960;
    private const double CollapsedHeight = 64;
    private const double EmptyHeight = 236;
    private const double MaxExpandedHeight = 680;
    private const int ResultLimit = 600;

    private readonly MainWindow _host;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _thumbTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly ObservableCollection<ChipItem> _chips = new()
    {
        new ChipItem("全部", SearchCategory.All) { IsActive = true },
        new ChipItem("图片", SearchCategory.Images),
        new ChipItem("PSD · AI", SearchCategory.Design),
        new ChipItem("文档", SearchCategory.Documents),
        new ChipItem("视频", SearchCategory.Videos),
        new ChipItem("文件夹", SearchCategory.Folders)
    };
    private readonly Dictionary<TileItem, FrameworkElement> _tiles = new();
    private ScrollViewer? _scroll;
    private List<TileItem> _items = new();
    private FileEntry[] _shown = Array.Empty<FileEntry>(); // 给后台线程看的只读快照
    private int _generation;
    private int _current = -1;
    private int _anchor = -1;
    private SearchCategory _category = SearchCategory.All;
    private SearchSort _sort = SearchSort.Relevance;
    private ParsedQuery _query = QueryParser.Parse("");
    private bool _gridMode;
    private bool _showingRecents;
    private bool _dragging;
    private bool _menuOpen;
    private bool _hiding;
    private bool _lookOpen;
    private bool _fromClipboard;
    private string? _lastClipboard;
    private double _panelHeight = CollapsedHeight;
    private double _panelWidth = CompactWidth;
    private int _columns = 5;
    private double _expandedHeight = MaxExpandedHeight; // 按当前显示器的可用高度收缩（150% 缩放的 1080p 屏放不下 680）
    private readonly DispatcherTimer _idleTimer;
    private readonly List<FileEntry> _pendingRelease = new();
    private bool _scrollAnimating;
    private bool _allowClose;
    private DateTime _freshUntil;
    private DateTime _hiddenAt = DateTime.MinValue;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _lookCts;
    private TileItem? _pressed;
    private bool _pressedWasSelected;
    private Point _pressPoint;
    private TileItem? _lookItem;
    private TimeSpan _lastSearchTime;

    private static readonly IEasingFunction Smooth = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    public SpotlightWindow(MainWindow host)
    {
        _host = host;
        InitializeComponent();
        ChipsList.ItemsSource = _chips;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RunSearchAsync(); };
        _thumbTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _thumbTimer.Tick += (_, _) => { _thumbTimer.Stop(); LoadVisibleThumbnails(); };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); HideStatus(); };
        // 收起两分钟后清掉结果：放掉缩略图引用，主窗口和后台精修也不再惦记这些条目
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); if (!IsVisible) ResetState(); };
        _detailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _detailTimer.Tick += (_, _) => { _detailTimer.Stop(); _ = LoadDetailImageAsync(); };
        _columnsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _columnsTimer.Tick += (_, _) => { _columnsTimer.Stop(); UpdateColumns(); };
        Loaded += (_, _) => _scroll = Rows.Template.FindName("PART_Scroll", Rows) as ScrollViewer;
        _host.StatusChanged += () => Dispatcher.InvokeAsync(UpdateIndexStatus);
        UpdateIndexStatus();
        UpdateThemeGlyph();
        ThemeManager.Changed += UpdateThemeGlyph;
        UpdateFooter();
    }

    /// <summary>这个条目当前是否显示在搜索条的网格里（主窗口释放缩略图、后台精修时会问）。线程安全。</summary>
    public bool Shows(FileEntry entry) => Array.IndexOf(Volatile.Read(ref _shown), entry) >= 0;

    /// <summary>索引有变化：搜索条正在显示结果就静默刷新，结果没变什么都不动。</summary>
    public async Task RefreshAsync()
    {
        // 工作台空着（最近使用 / 首次使用的提示）：索引载入或扫描完成后重新显示
        if (_workbench && IsVisible && !_hiding && !_lookOpen && !_dragging && SearchBox.Text.Trim().Length == 0) { ShowRecents(preserve: true); return; }
        if (!IsVisible || _hiding || _showingRecents || _lookOpen || _dragging || SearchBox.Text.Trim().Length == 0) return;
        var text = SearchBox.Text;
        var category = _category;
        var sort = _sort;
        SearchResponse response;
        try { response = await Task.Run(() => _host.Index.SearchDetailed(text, category, ResultLimit, sort, sort == SearchSort.Modified)); }
        catch { return; }
        if (!IsVisible || text != SearchBox.Text || category != _category || sort != _sort || _showingRecents) return;
        if (response.Items.Count == _items.Count && response.Items.Select((x, i) => ReferenceEquals(x, _items[i].Entry)).All(x => x)) return;
        ApplyResults(response.Items, response.Query, response, recentTitle: null, preserve: true);
    }

    /// <summary>主窗口真正退出时调用；其他时候（比如 Alt+F4）只是收起。</summary>
    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideAnimated();
            return;
        }
        _searchCts?.Cancel();
        _thumbCts?.Cancel();
        _lookCts?.Cancel();
        _detailCts?.Cancel();
        _zoomCts?.Cancel();
        _idleTimer.Stop();
        ThemeManager.Changed -= UpdateThemeGlyph;
        base.OnClosing(e);
    }

    // 呼出 / 收起 / 工作台模式：见 SpotlightWindow.Workbench.cs

    // ================= 剪贴板预填 =================

    private static readonly Regex SkuLike = new(@"^[A-Za-z]{0,6}[\s_-]?\d{1,8}(?:[\s._-]\d{1,8}){0,4}[A-Za-z]{0,4}$", RegexOptions.Compiled);

    private void TryPrefillFromClipboard()
    {
        if (!_host.SettingsData.ClipboardPrefill || SearchBox.Text.Length > 0) return;
        string? text = null;
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText()?.Trim(); } catch { }
        if (string.IsNullOrEmpty(text) || text.Length > 40 || text.Contains('\n') || text == _lastClipboard) return;
        if (!SkuLike.IsMatch(text)) return;
        _lastClipboard = text;
        SearchBox.Text = text;
        _fromClipboard = true;
        SearchBox.SelectAll();
    }

    // ================= 搜索 =================

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_fromClipboard && SearchBox.Text != _lastClipboard) _fromClipboard = false;
        _gridMode = false;
        _showingRecents = false;
        _searchTimer.Stop();
        if (SearchBox.Text.Trim().Length == 0)
        {
            _searchCts?.Cancel();
            Collapse(animate: true);
            return;
        }
        _searchTimer.Start();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 退格、Delete 是在改搜索词：离开网格模式，方向键重新归搜索框
        if (e.Key is Key.Back or Key.Delete) _gridMode = false;
    }

    private void SearchBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // 在网格里直接打字：回到搜索框继续输入
        _gridMode = false;
    }

    private async Task RunSearchAsync()
    {
        var text = SearchBox.Text;
        if (text.Trim().Length == 0) { Collapse(animate: true); return; }
        _searchCts?.Cancel();
        var cancellation = _searchCts = new CancellationTokenSource();
        var category = _category;
        var sort = _sort;
        SearchResponse response;
        try { response = await Task.Run(() => _host.Index.SearchDetailed(text, category, ResultLimit, sort, sort == SearchSort.Modified), cancellation.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { ShowStatus("搜索出错：" + ex.Message); return; }
        if (cancellation.IsCancellationRequested || text != SearchBox.Text) return;
        _lastSearchTime = response.Elapsed;
        try { ApplyResults(response.Items, response.Query, response, recentTitle: null); }
        catch (Exception ex) { ShowStatus("显示结果出错：" + ex.Message); }
    }

    private void ApplyResults(IReadOnlyList<FileEntry> entries, ParsedQuery query, SearchResponse? response, string? recentTitle, bool preserve = false)
    {
        _query = query;
        var previousPath = _current >= 0 && _current < _items.Count ? _items[_current].Entry.FullPath : null;
        var generation = ++_generation;
        _thumbCts?.Cancel();

        // 离开结果的条目释放缩略图（内存缓存还在，回来时瞬间显示）
        var incoming = new HashSet<FileEntry>(entries, ReferenceEqualityComparer.Instance);
        foreach (var old in _items.Select(x => x.Entry).Concat(_pendingRelease))
            if (!incoming.Contains(old) && !_host.IsShownInMainWindow(old)) old.Thumbnail = null;
        _pendingRelease.Clear();
        foreach (var entry in entries)
            if (entry.Thumbnail is null && _host.Thumbnails.TryGetCached(entry, out var cached)) entry.Thumbnail = cached;

        var keepSelected = preserve ? new HashSet<string>(_items.Where(x => x.IsSelected).Select(x => x.Entry.FullPath), StringComparer.OrdinalIgnoreCase) : null;
        var keepOffset = preserve ? _scroll?.VerticalOffset ?? 0 : 0;
        _items = entries.Select((entry, i) => new TileItem(entry, i, generation, query)).ToList();
        Volatile.Write(ref _shown, entries.ToArray());

        _tiles.Clear();
        _freshUntil = preserve ? DateTime.MinValue : DateTime.Now.AddMilliseconds(450);
        Rows.BeginAnimation(OpacityProperty, null);
        Rows.Opacity = 1;
        Rows.Padding = new Thickness(11, recentTitle is null ? 4 : 24, 11, 12);
        Rows.ItemsSource = BuildRows();
        if (preserve) Dispatcher.InvokeAsync(() => ScrollTo(keepOffset, animate: false), DispatcherPriority.Loaded);
        else ScrollTo(0, animate: false);

        SectionTitle.Text = recentTitle ?? "";
        SectionTitle.Visibility = recentTitle is null ? Visibility.Collapsed : Visibility.Visible;
        NoResults.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count == 0)
        {
            NoResultsTitle.Text = $"没有找到“{SearchBox.Text.Trim()}”";
            NoResultsHint.Text = query.HasFilters ? "试试去掉一个条件，或换个分类" : "试试只输入 SKU 主体，或用拼音首字母，比如 bdt = 白底图";
        }

        // 分类计数 + 识别出的条件
        var active = query.Category ?? _category;
        foreach (var chip in _chips)
        {
            chip.Count = response?.CategoryCounts.TryGetValue(chip.Category, out var n) == true ? n : 0;
            chip.IsActive = chip.Category == active;
        }
        var tokens = new List<TokenChip>();
        if (_fromClipboard) tokens.Add(new TokenChip("来自剪贴板", null, isClipboard: true));
        tokens.AddRange(query.Tokens.Where(t => t.Kind != QueryTokenKind.Category).Select(t => new TokenChip(t.Label, t)));
        TokensList.ItemsSource = tokens;

        // 结果数滚动到新值
        var total = response?.TotalMatches ?? _items.Count;
        BeginAnimation(CountValueProperty, new DoubleAnimation(CountValue, total, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });

        // 选中：尽量保持原来那个，否则第一个（这样直接回车就能打开）
        var keep = previousPath is null ? -1 : _items.FindIndex(x => x.Entry.FullPath.Equals(previousPath, StringComparison.OrdinalIgnoreCase));
        _current = -1;
        _anchor = -1;
        if (_items.Count > 0) Select(keep >= 0 ? keep : 0, scroll: false);
        if (keepSelected is { Count: > 1 })
            foreach (var item in _items) item.IsSelected = keepSelected.Contains(item.Entry.FullPath) || item.Index == _current;
        UpdateFooter();

        NoResultsButton.Visibility = Visibility.Collapsed;
        _setupHintShown = false;
        if (!_workbench) AnimatePanel(_items.Count == 0 ? EmptyHeight : _expandedHeight);
        ScheduleThumbnails();
    }

    private List<TileRow> BuildRows()
    {
        var rows = new List<TileRow>((_items.Count + _columns - 1) / _columns);
        for (var i = 0; i < _items.Count; i += _columns) rows.Add(new TileRow(i / _columns, _items.Skip(i).Take(_columns).ToList()));
        return rows;
    }

    private void Collapse(bool animate)
    {
        if (_workbench && IsVisible)
        {
            // 工作台不收起：搜索词清空就回到“最近使用”
            ShowRecents();
            return;
        }
        _thumbCts?.Cancel();
        _pendingRelease.AddRange(_items.Select(x => x.Entry));
        _items = new List<TileItem>();
        Volatile.Write(ref _shown, Array.Empty<FileEntry>());
        _current = -1;
        _anchor = -1;
        _tiles.Clear();
        TokensList.ItemsSource = null;
        StatsText.Text = "";
        NoResults.Visibility = Visibility.Collapsed;
        SectionTitle.Visibility = Visibility.Collapsed;
        UpdateFooter();
        void Release()
        {
            // 淡出期间又有了新结果：ApplyResults 已经接手处理，这里不重复
            foreach (var old in _pendingRelease) if (!Shows(old) && !_host.IsShownInMainWindow(old)) old.Thumbnail = null;
            _pendingRelease.Clear();
        }
        if (!animate)
        {
            Release();
            Rows.ItemsSource = null;
            PanelClip.BeginAnimation(RectangleGeometry.RectProperty, null);
            PanelShadow.BeginAnimation(HeightProperty, null);
            PanelClip.Rect = new Rect(0, 0, _panelWidth, CollapsedHeight);
            PanelShadow.Height = CollapsedHeight;
            _panelHeight = CollapsedHeight;
            return;
        }
        // 先淡出网格再收起，避免内容在收缩时突然消失
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            if (_items.Count > 0) return;
            Release();
            Rows.ItemsSource = null;
            Rows.BeginAnimation(OpacityProperty, null);
            Rows.Opacity = 1;
        };
        Rows.BeginAnimation(OpacityProperty, fade);
        AnimatePanel(CollapsedHeight);
    }

    private void AnimatePanel(double target)
    {
        if (Math.Abs(target - _panelHeight) < 0.5) return;
        var duration = TimeSpan.FromMilliseconds(target > _panelHeight ? 320 : 220);
        PanelClip.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(new Rect(0, 0, _panelWidth, target), duration) { EasingFunction = Smooth });
        PanelShadow.BeginAnimation(HeightProperty, new DoubleAnimation(target, duration) { EasingFunction = Smooth });
        _panelHeight = target;
    }

    /// <param name="preserve">索引变化触发的刷新：列表没变就什么都不动，变了也保持滚动位置、不重播动画。</param>
    private async void ShowRecents(bool preserve = false)
    {
        var recent = _host.RecentEntries();
        var title = "最近使用";
        if (recent.Count == 0)
        {
            // 全索引按时间排序，放后台
            try { recent = await Task.Run(() => _host.Index.Search("", SearchCategory.All, 60, SearchSort.Modified, true)); }
            catch { return; }
            title = "最近更新";
        }
        if (SearchBox.Text.Trim().Length > 0 || !IsVisible) return;
        if (recent.Count == 0)
        {
            if (_workbench) ShowSetupHint();
            return;
        }
        if (preserve && _showingRecents && recent.Count == _items.Count && recent.Select((x, i) => ReferenceEquals(x, _items[i].Entry)).All(x => x)) return;
        preserve &= _showingRecents && _items.Count > 0;
        _showingRecents = true;
        ApplyResults(recent, QueryParser.Parse(""), null, title, preserve);
        StatsText.Text = "";
        _gridMode = true;
    }

    // ================= 结果数字滚动 =================

    public static readonly DependencyProperty CountValueProperty = DependencyProperty.Register(
        nameof(CountValue), typeof(double), typeof(SpotlightWindow), new PropertyMetadata(0d, (d, _) => ((SpotlightWindow)d).UpdateStats()));
    public double CountValue { get => (double)GetValue(CountValueProperty); set => SetValue(CountValueProperty, value); }

    private void UpdateStats()
    {
        if (_showingRecents || SearchBox.Text.Trim().Length == 0) { StatsText.Text = ""; return; }
        StatsText.Text = $"{(int)Math.Round(CountValue):N0} 项 · {Math.Max(1, (int)_lastSearchTime.TotalMilliseconds)} ms";
    }

    // ================= 分类 / 条件 / 排序 =================

    private async void Chip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChipItem chip) return;
        _category = chip.Category;
        foreach (var item in _chips) item.IsActive = item.Category == chip.Category;
        SearchBox.Focus();
        if (SearchBox.Text.Trim().Length > 0) await RunSearchAsync();
    }

    private void Token_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TokenChip chip) return;
        if (chip.IsClipboard) { SearchBox.Clear(); SearchBox.Focus(); return; }
        if (chip.Token is null) return;
        SearchBox.Text = QueryParser.RemoveToken(SearchBox.Text, chip.Token);
        SearchBox.CaretIndex = SearchBox.Text.Length;
        SearchBox.Focus();
    }

    private async void SortButton_Click(object sender, RoutedEventArgs e)
    {
        _sort = _sort switch { SearchSort.Relevance => SearchSort.Modified, SearchSort.Modified => SearchSort.Name, _ => SearchSort.Relevance };
        SortButton.Content = _sort switch { SearchSort.Modified => "最近修改 ↓", SearchSort.Name => "名称", _ => "相关度" };
        SearchBox.Focus();
        if (SearchBox.Text.Trim().Length > 0) await RunSearchAsync();
    }

    // ================= 选择 =================

    private void Select(int index, bool extend = false, bool toggle = false, bool scroll = true)
    {
        if (_items.Count == 0) return;
        index = Math.Clamp(index, 0, _items.Count - 1);
        if (toggle)
        {
            _items[index].IsSelected = !_items[index].IsSelected;
            _anchor = index;
        }
        else if (extend && _anchor >= 0)
        {
            var (from, to) = _anchor <= index ? (_anchor, index) : (index, _anchor);
            for (var i = 0; i < _items.Count; i++) _items[i].IsSelected = i >= from && i <= to;
        }
        else
        {
            foreach (var item in _items) if (item.IsSelected) item.IsSelected = false;
            _items[index].IsSelected = true;
            _anchor = index;
        }
        _current = index;
        UpdateFooter();
        if (scroll) EnsureVisible(index);
    }

    private IReadOnlyList<FileEntry> SelectedEntries()
    {
        var selected = _items.Where(x => x.IsSelected).Select(x => x.Entry).ToList();
        if (selected.Count == 0 && _current >= 0 && _current < _items.Count) selected.Add(_items[_current].Entry);
        return selected;
    }

    private TileItem? Current => _current >= 0 && _current < _items.Count ? _items[_current] : null;

    private void UpdateFooter()
    {
        UpdateDetail();
        var count = _items.Count(x => x.IsSelected);
        var current = Current;
        if (current is null)
        {
            FooterThumb.Source = null;
            FooterName.Text = _items.Count == 0 ? "输入 SKU、文件名或拼音首字母" : "";
            FooterPath.Text = _items.Count == 0 ? "按 ↓ 查看最近使用的文件" : "";
            return;
        }
        FooterThumb.Source = current.Entry.Thumbnail;
        if (count > 1)
        {
            FooterName.Text = $"已选 {count} 个文件";
            FooterPath.Text = "可以一起拖出，或按 Ctrl+Shift+C 一起复制";
        }
        else
        {
            var entry = current.Entry;
            FooterName.Text = entry.Width > 0 ? $"{entry.Name}  ·  {entry.ResolutionLabel}  ·  {entry.SizeLabel}" : $"{entry.Name}  ·  {entry.SizeLabel}";
            FooterPath.Text = entry.ParentPath + (entry.IsOffline ? "  ·  离线快照" : "");
        }
    }

    // ================= 键盘 =================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_lookOpen)
        {
            switch (key)
            {
                case Key.Space or Key.Escape: CloseLook(animate: true); e.Handled = true; return;
                case Key.Left: LookStep(-1); e.Handled = true; return;
                case Key.Right: LookStep(1); e.Handled = true; return;
                case Key.Up: LookStep(-_columns); e.Handled = true; return;
                case Key.Down: LookStep(_columns); e.Handled = true; return;
                case Key.Enter:
                    CloseLook(animate: false);
                    if (ctrl) OpenSelected(); else RevealSelected();
                    e.Handled = true; return;
                case Key.OemPlus or Key.Add: ZoomStep(1.25); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: ZoomStep(1 / 1.25); e.Handled = true; return;
                case Key.D0 or Key.NumPad0: ResetZoom(animate: true); e.Handled = true; return;
            }
        }

        switch (key)
        {
            case Key.Escape:
                if (SearchBox.Text.Length > 0) SearchBox.Clear(); else HideAnimated();
                e.Handled = true;
                break;
            case Key.OemComma when ctrl:
                OpenSettings();
                e.Handled = true;
                break;
            case Key.Down:
                if (_items.Count == 0) { if (SearchBox.Text.Trim().Length == 0) ShowRecents(); }
                else if (!_gridMode) { _gridMode = true; Select(Math.Max(0, _current)); }
                else Select(_current + _columns, extend: shift);
                e.Handled = true;
                break;
            case Key.Up:
                if (_gridMode && _items.Count > 0)
                {
                    if (_current < _columns) _gridMode = false;
                    else Select(_current - _columns, extend: shift);
                    e.Handled = true;
                }
                break;
            case Key.Left or Key.Right when _gridMode && _items.Count > 0:
                Select(_current + (key == Key.Left ? -1 : 1), extend: shift);
                e.Handled = true;
                break;
            case Key.Home or Key.End when _gridMode && _items.Count > 0:
                Select(key == Key.Home ? 0 : _items.Count - 1, extend: shift);
                e.Handled = true;
                break;
            case Key.PageDown or Key.PageUp when _items.Count > 0:
                _gridMode = true;
                Select(_current + (key == Key.PageDown ? 2 : -2) * _columns, extend: shift);
                e.Handled = true;
                break;
            case Key.Enter:
                // 回车 = 打开所在位置（选中的是文件夹就直接打开文件夹）；Ctrl+回车 = 打开文件；双击卡片也是打开文件
                if (ctrl && shift) ToggleWorkbench();
                else if (ctrl) OpenSelected();
                else RevealSelected();
                e.Handled = true;
                break;
            case Key.Space when _gridMode && _items.Count > 0 && !ctrl:
                OpenLook();
                e.Handled = true;
                break;
            case Key.C when ctrl && SearchBox.SelectionLength == 0 && _items.Count > 0:
                if (shift) CopyFiles(); else CopyPaths();
                e.Handled = true;
                break;
            case Key.A when ctrl && _gridMode && _items.Count > 0:
                foreach (var item in _items) item.IsSelected = true;
                UpdateFooter();
                e.Handled = true;
                break;
            case Key.Tab when _items.Count > 0 || SearchBox.Text.Length > 0:
                var index = _chips.ToList().FindIndex(x => x.IsActive);
                var next = _chips[(index + (shift ? _chips.Count - 1 : 1)) % _chips.Count];
                Chip_Click(new Button { DataContext = next }, e);
                e.Handled = true;
                break;
        }
    }

    // ================= 鼠标：点选 / 双击 / 拖出 / 右键 =================

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TileItem item) return;
        _gridMode = true;
        if (e.ClickCount >= 2)
        {
            Select(item.Index, scroll: false);
            OpenSelected();
            e.Handled = true;
            return;
        }
        var modifiers = Keyboard.Modifiers;
        _pressed = item;
        _pressPoint = e.GetPosition(this);
        _pressedWasSelected = item.IsSelected;
        if (modifiers.HasFlag(ModifierKeys.Control)) Select(item.Index, toggle: true, scroll: false);
        else if (modifiers.HasFlag(ModifierKeys.Shift)) Select(item.Index, extend: true, scroll: false);
        else if (!item.IsSelected) Select(item.Index, scroll: false);
        else { _current = item.Index; UpdateFooter(); } // 已选中的保持多选，方便一起拖出
        SearchBox.Focus();
        e.Handled = true;
    }

    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed || _dragging) return;
        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 1.5 && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 1.5) return;
        var pressed = _pressed;
        _pressed = null;
        if (!pressed.IsSelected) Select(pressed.Index, scroll: false);
        StartDrag((DependencyObject)sender);
    }

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TileItem item && ReferenceEquals(item, _pressed)
            && _pressedWasSelected && Keyboard.Modifiers == ModifierKeys.None)
            Select(item.Index, scroll: false); // 在已选中的卡片上单击：收成单选
        _pressed = null;
    }

    private void StartDrag(DependencyObject source)
    {
        var entries = SelectedEntries();
        if (entries.Count == 0) return;
        _dragging = true;
        try
        {
            var effect = DragOut.Start(source, entries, new Point(84, 75));
            if (effect.HasFlag(DragDropEffects.Copy))
            {
                FlashTiles(entries);
                ShowStatus(entries.Count == 1 ? "已复制到目标位置，原文件没动" : $"已复制 {entries.Count} 个文件，原文件没动");
                _host.RecordRecent(RecentQuery, entries);
            }
        }
        catch (Exception ex) { ShowStatus("拖出失败：" + ex.Message); }
        finally { _dragging = false; }
        // 放到了别的程序上、焦点已经过去：搜索条收起，不挡住目标窗口（2 分钟内再呼出会回到这里）
        if (!IsActive && !_workbench) HideAnimated();
    }

    private void Tile_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TileItem item) return;
        _gridMode = true;
        if (!item.IsSelected) Select(item.Index, scroll: false);
        else { _current = item.Index; UpdateFooter(); }

        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(MenuItemFor("打开", "双击 / Ctrl+Enter", "", OpenSelected));
        menu.Items.Add(MenuItemFor("打开所在位置", "Enter", "", RevealSelected));
        menu.Items.Add(MenuItemFor("预览", "空格", "", OpenLook));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("复制文件", "Ctrl+Shift+C", "", CopyFiles));
        menu.Items.Add(MenuItemFor("复制完整路径", "Ctrl+C", "", CopyPaths));
        menu.Items.Add(new Separator());
        menu.Items.Add(_workbench
            ? MenuItemFor("收回搜索条", "Ctrl+Shift+Enter", "", ToggleWorkbench)
            : MenuItemFor("展开为工作台", "Ctrl+Shift+Enter", "", ToggleWorkbench));
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; SearchBox.Focus(); };
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem MenuItemFor(string header, string gesture, string glyph, Action action)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 13 };
        icon.SetResourceReference(StyleProperty, "MenuIcon");
        var item = new MenuItem { Header = header, InputGestureText = gesture, Icon = icon };
        item.Click += (_, _) => action();
        return item;
    }

    // ================= 文件操作 =================

    private void OpenSelected()
    {
        var entries = SelectedEntries();
        if (entries.Count == 0) return;
        try
        {
            foreach (var entry in entries.Take(5)) FileActions.Open(entry);
            _host.RecordRecent(RecentQuery, entries.Take(5));
            if (!_workbench) HideAnimated();
        }
        catch (Exception ex) { ShowStatus("打开失败：" + ex.Message); }
    }

    private void RevealSelected()
    {
        var entry = Current?.Entry;
        if (entry is null) return;
        try
        {
            FileActions.Reveal(entry);
            _host.RecordRecent(RecentQuery, new[] { entry });
            if (!_workbench) HideAnimated();
        }
        catch (Exception ex) { ShowStatus("定位失败：" + ex.Message); }
    }

    private void CopyFiles()
    {
        var entries = SelectedEntries();
        if (entries.Count == 0) return;
        try
        {
            FileActions.CopyItems(entries);
            FlashTiles(entries);
            ShowStatus(entries.Count == 1 ? "文件已复制，可以直接粘贴" : $"已复制 {entries.Count} 个文件，可以直接粘贴");
            _host.RecordRecent(RecentQuery, entries);
        }
        catch (Exception ex) { ShowStatus("复制失败：" + ex.Message); }
    }

    /// <summary>剪贴板自动填进来的词不算“最近搜索”，不写进设置文件。</summary>
    private string RecentQuery => _fromClipboard ? "" : SearchBox.Text;

    private void CopyPaths()
    {
        var entries = SelectedEntries();
        if (entries.Count == 0) return;
        try
        {
            FileActions.CopyPaths(entries);
            ShowStatus(entries.Count == 1 ? "路径已复制" : $"已复制 {entries.Count} 条路径");
        }
        catch (Exception ex) { ShowStatus("复制失败：" + ex.Message); }
    }

    // ================= 反馈：卡片闪一下 + 底栏提示 =================

    private void FlashTiles(IEnumerable<FileEntry> entries)
    {
        var set = new HashSet<FileEntry>(entries, ReferenceEqualityComparer.Instance);
        foreach (var (item, tile) in _tiles)
        {
            if (!set.Contains(item.Entry) || FindNamed<Border>(tile, "Flash") is not { } flash) continue;
            var animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), EaseOut));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(720)), EaseOut));
            flash.BeginAnimation(OpacityProperty, animation);
        }
    }

    private void ShowStatus(string message)
    {
        FooterStatusText.Text = message;
        FooterStatus.Visibility = Visibility.Visible;
        FooterStatus.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        FooterHints.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(100)));
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void HideStatus()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { if (!_statusTimer.IsEnabled) FooterStatus.Visibility = Visibility.Collapsed; };
        FooterStatus.BeginAnimation(OpacityProperty, fade);
        FooterHints.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
    }

    // ================= 卡片进场：依次淡入 =================

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement tile || tile.Tag is not TileItem item) return;
        _tiles[item] = tile;
        if (item.Generation != _generation || DateTime.Now > _freshUntil || item.Index >= 25) return;

        var delay = TimeSpan.FromMilliseconds(item.Index * 14);
        var duration = TimeSpan.FromMilliseconds(220);
        if (tile.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
        {
            shift = new TranslateTransform();
            tile.RenderTransform = shift;
        }
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay + duration), EaseOut));
        var rise = new DoubleAnimationUsingKeyFrames();
        rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(10, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(10, KeyTime.FromTimeSpan(delay)));
        rise.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + duration), Smooth));
        tile.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    private void Tile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TileItem item } tile && _tiles.TryGetValue(item, out var known) && ReferenceEquals(known, tile))
            _tiles.Remove(item);
    }

    // ================= 平滑滚动（动画驱动偏移量，跟随刷新率） =================

    public static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register(
        "AnimatedOffset", typeof(double), typeof(SpotlightWindow),
        new PropertyMetadata(0d, (d, e) => ((SpotlightWindow)d)._scroll?.ScrollToVerticalOffset((double)e.NewValue)));

    private double _scrollTarget;
    private DoubleAnimation? _scrollAnimation;

    private void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scroll is null) return;
        e.Handled = true;
        var start = _scrollAnimating ? _scrollTarget : _scroll.VerticalOffset;
        ScrollTo(start - e.Delta * 1.15, animate: true);
    }

    private void ScrollTo(double offset, bool animate)
    {
        if (_scroll is null) return;
        _scrollTarget = Math.Clamp(offset, 0, Math.Max(0, _scroll.ScrollableHeight));
        if (!animate)
        {
            BeginAnimation(AnimatedOffsetProperty, null);
            _scrollAnimating = false;
            SetValue(AnimatedOffsetProperty, _scrollTarget);
            _scroll.ScrollToVerticalOffset(_scrollTarget);
            return;
        }
        var animation = new DoubleAnimation(_scroll.VerticalOffset, _scrollTarget, TimeSpan.FromMilliseconds(280)) { EasingFunction = Smooth };
        animation.Completed += (_, _) => { if (ReferenceEquals(_scrollAnimation, animation)) _scrollAnimating = false; };
        _scrollAnimation = animation;
        _scrollAnimating = true;
        BeginAnimation(AnimatedOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void EnsureVisible(int index)
    {
        if (_scroll is null || index < 0) return;
        // ScrollViewer 的 Padding 不计入滚动偏移：第 n 行的顶部就是 n × 行高
        var top = (index / _columns) * RowHeight;
        var bottom = top + RowHeight;
        var viewTop = _scrollTarget;
        var viewBottom = viewTop + _scroll.ViewportHeight;
        if (top < viewTop) ScrollTo(top - 6, animate: true);
        else if (bottom > viewBottom) ScrollTo(bottom - _scroll.ViewportHeight + 10, animate: true);
    }

    private void Scroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 拖滚动条、键盘翻页等非动画滚动：同步目标位置，下次滚轮从这里接着滚
        if (!_scrollAnimating && _scroll is not null) _scrollTarget = _scroll.VerticalOffset;
        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0) ScheduleThumbnails();
    }

    // ================= 缩略图：只加载看得见的几行 =================

    private void ScheduleThumbnails()
    {
        // 节流而不是去抖：连续滚动时每 60ms 也会补一次
        if (!_thumbTimer.IsEnabled) _thumbTimer.Start();
    }

    private void LoadVisibleThumbnails()
    {
        if (_items.Count == 0 || !IsVisible || _scroll is null) return;
        var first = Math.Max(0, (int)(_scroll.VerticalOffset / RowHeight));
        var rows = (int)Math.Ceiling(_scroll.ViewportHeight / RowHeight) + 1;
        var last = first + rows + 2; // 往下多预取两行
        var wanted = new List<FileEntry>();
        for (var row = first; row <= last; row++)
            for (var col = 0; col < _columns; col++)
            {
                var index = row * _columns + col;
                if (index >= _items.Count) break;
                if (_items[index].Entry.Thumbnail is null) wanted.Add(_items[index].Entry);
            }
        _thumbCts?.Cancel();
        if (wanted.Count == 0) return;
        var cancellation = _thumbCts = new CancellationTokenSource();
        foreach (var entry in wanted) _ = LoadThumbnailAsync(entry, cancellation.Token);
    }

    private async Task LoadThumbnailAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var image = await _host.Thumbnails.LoadAsync(entry, cancellationToken);
            if (cancellationToken.IsCancellationRequested || image is null) return;
            if (!Shows(entry) && !_host.IsShownInMainWindow(entry)) return;
            entry.Thumbnail = image;
            if (ReferenceEquals(Current?.Entry, entry)) UpdateFooter();
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ================= 空格预览：共享元素转场 =================

    private void OpenLook()
    {
        var item = Current;
        if (item is null || _lookOpen) return;
        _lookOpen = true;
        _lookItem = item;
        Look.Visibility = Visibility.Visible;
        Look.UpdateLayout();
        LookName.Text = item.Entry.Name;
        LookMeta.Text = LookMetaFor(item.Entry);

        ResetZoomState();
        var source = (BitmapSource?)item.Entry.Thumbnail;
        LookImage.BeginAnimation(OpacityProperty, null); // 上一次关闭的淡出可能还压着
        LookImage.Opacity = 1;
        LookImage.Source = source;
        var target = TargetRect(source);
        PlaceLookImage(target);

        var duration = TimeSpan.FromMilliseconds(300);
        var from = TileImageRect(item);
        if (source is not null && from is { } rect)
        {
            // 从网格里那张缩略图的位置和大小，放大到预览位置
            var scale = new ScaleTransform(rect.Width / target.Width, rect.Height / target.Height);
            var move = new TranslateTransform(rect.X - target.X, rect.Y - target.Y);
            var group = new TransformGroup { Children = { scale, move } };
            LookImage.RenderTransform = group;
            // 飞入用的变换直接交给缩放：飞入途中滚轮，会从当前位置接着缩放，不会跳
            _zoomScale = scale;
            _zoomMove = move;
            _zoomGroup = group;
            LookImage.Opacity = 1;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, duration) { EasingFunction = Smooth });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, duration) { EasingFunction = Smooth });
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = Smooth });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = Smooth });
        }
        else
        {
            LookImage.RenderTransform = Transform.Identity;
            LookImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }
        LookBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        LookChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(120) });
        _ = LoadLookHighResAsync(item);
    }

    private void CloseLook(bool animate)
    {
        if (!_lookOpen) return;
        _lookOpen = false;
        _lookCts?.Cancel();
        var item = _lookItem;
        _lookItem = null;
        if (!animate)
        {
            Look.Visibility = Visibility.Collapsed;
            LookImage.Source = null;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(240);
        var current = new Rect(Canvas.GetLeft(LookImage), Canvas.GetTop(LookImage), LookImage.Width, LookImage.Height);
        var back = item is null || IsZoomed ? null : TileImageRect(item); // 放大过就直接淡出，不硬缩回卡片
        EndPan();
        if (back is { } rect && current.Width > 0)
        {
            // 缩回网格里对应卡片的位置
            var scale = new ScaleTransform();
            var move = new TranslateTransform();
            LookImage.RenderTransform = new TransformGroup { Children = { scale, move } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(rect.Width / current.Width, duration) { EasingFunction = Smooth });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(rect.Height / current.Height, duration) { EasingFunction = Smooth });
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(rect.X - current.X, duration) { EasingFunction = Smooth });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rect.Y - current.Y, duration) { EasingFunction = Smooth });
        }
        else
        {
            LookImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
        }
        LookChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(100)));
        var fade = new DoubleAnimation(0, duration);
        fade.Completed += (_, _) =>
        {
            if (_lookOpen) return;
            Look.Visibility = Visibility.Collapsed;
            LookImage.Source = null;
            LookImage.BeginAnimation(OpacityProperty, null);
            LookImage.Opacity = 1;
        };
        LookBackdrop.BeginAnimation(OpacityProperty, fade);
    }

    private void LookStep(int delta)
    {
        if (!_lookOpen || _items.Count == 0) return;
        var next = Math.Clamp(_current + delta, 0, _items.Count - 1);
        if (next == _current) return;
        Select(next);
        var item = _items[next];
        _lookItem = item;
        LookName.Text = item.Entry.Name;
        LookMeta.Text = LookMetaFor(item.Entry);
        var source = (BitmapSource?)item.Entry.Thumbnail;
        ResetZoomState();
        LookImage.Source = source;
        LookImage.RenderTransform = Transform.Identity;
        PlaceLookImage(TargetRect(source));
        LookImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(160)));
        _ = LoadLookHighResAsync(item, delayMs: 140); // 连按方向键时只解码最后停下的那张
    }

    private async Task LoadLookHighResAsync(TileItem item, int delayMs = 0)
    {
        _lookCts?.Cancel();
        var cancellation = _lookCts = new CancellationTokenSource();
        BitmapSource? image = null;
        var entry = item.Entry;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cancellation.Token);
            if (FileTypes.Images.Contains(entry.Extension))
                image = (await _host.Thumbnails.LoadPreviewImageAsync(entry.FullPath, 1600, cancellation.Token)).Image;
            image ??= await _host.Thumbnails.LoadShellPreviewAsync(entry.FullPath, 1024, cancellation.Token);
        }
        catch (OperationCanceledException) { return; }
        catch { }
        if (cancellation.IsCancellationRequested || !_lookOpen || !ReferenceEquals(_lookItem, item) || image is null) return;
        // 放大时已经换上了更清楚的原图：不要用这张较小的覆盖回去
        if (CurrentSourceEdge() >= Math.Max(image.PixelWidth, image.PixelHeight)) return;
        _lookSourceEdge = Math.Max(image.PixelWidth, image.PixelHeight);
        var hadThumb = LookImage.Source is not null;
        LookImage.Source = image;
        var target = TargetRect(image);
        if (!hadThumb)
        {
            if (IsZoomed) ResetZoom(animate: false); // 占位图上已经缩放过：位置要按新图重算
            PlaceLookImage(target);
            LookImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        else if (Math.Abs(target.Width - LookImage.Width) > 1 || Math.Abs(target.Height - LookImage.Height) > 1)
        {
            // 高清图比例或尺寸和缩略图不一样（比如原图更小）：等飞入动画结束再对齐，避免跳一下
            await Task.Delay(320);
            if (_lookOpen && ReferenceEquals(_lookItem, item))
            {
                // 比例变了（比如缩略图没按 EXIF 转正）：不重排的话 Stretch=Fill 会把图拉变形，放大过也只能回到适应窗口
                if (IsZoomed) ResetZoom(animate: false);
                LookImage.RenderTransform = Transform.Identity;
                _zoomGroup = null;
                PlaceLookImage(target);
            }
        }
    }

    private Rect TargetRect(BitmapSource? image) => TargetRect(image, _lookItem?.Entry);

    private Rect TargetRect(BitmapSource? image, FileEntry? entry)
    {
        var area = LookArea();
        if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
            return new Rect(area.X + area.Width / 2 - 120, area.Y + area.Height / 2 - 120, 240, 240);
        var scale = Math.Min(area.Width / image.PixelWidth, area.Height / image.PixelHeight);
        // 小图不无限放大：知道原图尺寸就最多放到原图大小，否则最多 640
        var realEdge = entry is { Width: > 0, Height: > 0 } ? Math.Max(entry.Width, entry.Height) : 640;
        scale = Math.Min(scale, Math.Max(1.0, realEdge / (double)Math.Max(image.PixelWidth, image.PixelHeight)));
        var width = image.PixelWidth * scale;
        var height = image.PixelHeight * scale;
        return new Rect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    /// <summary>预览图可以占用的区域：面板减去边距和底部的文件名栏。</summary>
    private Rect LookArea() => new(36, 24, Math.Max(100, PanelRoot.Width - 72), Math.Max(100, PanelRoot.Height - 24 - 90));

    private void PlaceLookImage(Rect target)
    {
        Canvas.SetLeft(LookImage, target.X);
        Canvas.SetTop(LookImage, target.Y);
        LookImage.Width = target.Width;
        LookImage.Height = target.Height;
    }

    private Rect? TileImageRect(TileItem item)
    {
        if (!_tiles.TryGetValue(item, out var tile) || !tile.IsVisible) return null;
        if (FindNamed<Image>(tile, "ThumbImage") is not { ActualWidth: > 0 } image) return null;
        // Image 元素是整块区域，图片按比例居中：算出图片实际画出来的那一块
        var box = new Rect(image.RenderSize);
        if (image.Source is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap)
        {
            var scale = Math.Min(box.Width / bitmap.PixelWidth, box.Height / bitmap.PixelHeight);
            var w = bitmap.PixelWidth * scale;
            var h = bitmap.PixelHeight * scale;
            box = new Rect((box.Width - w) / 2, (box.Height - h) / 2, w, h);
        }
        try { return image.TransformToVisual(LookCanvas).TransformBounds(box); }
        catch (InvalidOperationException) { return null; }
    }

    private static string LookMetaFor(FileEntry entry) =>
        string.Join("  ·  ", new[] { entry.TypeLabel, entry.Width > 0 ? entry.ResolutionLabel : null, entry.IsDirectory ? null : entry.SizeLabel, entry.ModifiedLabel }
            .Where(x => !string.IsNullOrEmpty(x) && x != "—"));

    private void Look_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseLook(animate: true);
        e.Handled = true;
    }

    // ================= 工具 =================

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (FindNamed<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private static double DpiScaleAt(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0) return dpiX / 96.0;
        }
        catch { }
        return 1.0;
    }

    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
