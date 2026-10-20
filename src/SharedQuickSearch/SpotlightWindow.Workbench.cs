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

/// <summary>
/// 呼出、收起，以及“工作台”模式。
/// 工作台不是另一个窗口：还是这块面板，原地变大，网格变宽，右边多一栏详情，
/// 顶部多出刷新、主题、设置、关闭。设计语言和搜索条完全一致。
/// </summary>
public partial class SpotlightWindow
{
    private const double DetailWidth = 340;
    private const double TileSlot = 168;           // 每张卡片占的最小宽度（含间距），工作台按它算列数
    private const double MinWorkbenchWidth = 980;
    private const double MinWorkbenchHeight = 600;

    private bool _workbench;
    private readonly DispatcherTimer _detailTimer;
    private readonly DispatcherTimer _columnsTimer;
    private CancellationTokenSource? _detailCts;
    private FileEntry? _detailEntry;
    private bool _detailHiRes;

    public static readonly DependencyProperty GridColumnsProperty = DependencyProperty.Register(
        nameof(GridColumns), typeof(int), typeof(SpotlightWindow), new PropertyMetadata(5));
    public int GridColumns { get => (int)GetValue(GridColumnsProperty); set => SetValue(GridColumnsProperty, value); }

    // ================= 呼出 / 收起 =================

    /// <summary>快捷键：没显示就呼出，已经在前台就收起。</summary>
    public void Toggle()
    {
        if (IsVisible && IsActive && !_hiding) { HideAnimated(); return; }
        Summon();
    }

    /// <param name="workbench">true = 直接打开工作台；null = 按设置和上一次的状态决定。</param>
    public void Summon(bool? workbench = null)
    {
        var noRoots = _host.SettingsData.IndexRoots.Count == 0;
        if (IsVisible && !_hiding)
        {
            if (workbench == true && !_workbench) ToggleWorkbench();
            Activate();
            SearchBox.Focus();
            return;
        }
        _hiding = false;
        _idleTimer.Stop();
        Stage.BeginAnimation(OpacityProperty, null); // 切换模式的过渡被打断时，别留下半透明
        Stage.Opacity = 1;
        // 两分钟内再次呼出：保留上次的搜索、滚动位置和模式，方便连续拖文件
        var recent = DateTime.Now - _hiddenAt <= TimeSpan.FromMinutes(2);
        var wantWorkbench = workbench
            ?? (noRoots || _host.SettingsData.LaunchMode == "Window" || (recent && _workbench));
        if (!recent) ResetState();

        ApplyWorkbenchLayout(wantWorkbench);
        if (wantWorkbench) PositionWorkbench(); else PositionOnCursorMonitor();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        TryPrefillFromClipboard();
        if (wantWorkbench && SearchBox.Text.Trim().Length == 0) ShowRecents();

        var duration = TimeSpan.FromMilliseconds(200);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = Smooth });
        StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = Smooth });
        StageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(wantWorkbench ? 8 : -10, 0, duration) { EasingFunction = Smooth });
        ScheduleThumbnails();
    }

    public void HideAnimated()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        CloseLook(animate: false);
        _thumbCts?.Cancel();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return;
            Hide();
            _hiding = false;
            _hiddenAt = DateTime.Now;
            _idleTimer.Start();
        };
        BeginAnimation(OpacityProperty, fade);
        StageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(120)));
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // 工作台是常规窗口，切走不收起；搜索条切走就收起。
        // 等一拍再判断：打开设置、提示框时焦点会先离开再落到子窗口上。
        if (_workbench || _dragging || _menuOpen) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_workbench || _dragging || _menuOpen || IsActive) return;
            if (OwnedWindows.Cast<Window>().Any(x => x.IsVisible)) return;
            HideAnimated();
        }, DispatcherPriority.Background);
    }

    private void ResetState()
    {
        _fromClipboard = false;
        _category = SearchCategory.All;
        foreach (var chip in _chips) chip.IsActive = chip.Category == SearchCategory.All;
        SearchBox.Text = "";
        Collapse(animate: false);
    }

    // ================= 搜索条的位置 =================

    private void PositionOnCursorMonitor()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        try
        {
            var cursor = Forms.Cursor.Position;
            var area = Forms.Screen.FromPoint(cursor).WorkingArea;
            var scale = DpiScaleAt(cursor.X, cursor.Y);
            FitToHeight(area.Height / scale);
            var width = (int)(Width * scale);
            var x = area.Left + (area.Width - width) / 2;
            // 默认在屏幕 14% 处；放不下展开后的高度就往上提
            var needed = (int)((16 + _expandedHeight + 16) * scale);
            var y = area.Top + Math.Min((int)(area.Height * 0.14), Math.Max(0, area.Height - needed));
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch
        {
            FitToHeight(SystemParameters.WorkArea.Height);
            Left = (SystemParameters.WorkArea.Width - Width) / 2;
            Top = Math.Min(SystemParameters.WorkArea.Height * 0.14, Math.Max(0, SystemParameters.WorkArea.Height - _expandedHeight - 32));
        }
    }

    /// <summary>可用高度（DIP）不够时缩短展开高度。只在呼出时改一次排版。</summary>
    private void FitToHeight(double available, bool force = false)
    {
        var target = Math.Clamp(Math.Floor(available - 16 - 16 - 24), 320, MaxExpandedHeight);
        if (!force && Math.Abs(target - _expandedHeight) < 0.5) return;
        var wasExpanded = _panelBase > EmptyHeight + 0.5;
        _expandedHeight = target;
        PanelRoot.Height = target;
        Height = target + 60;
        if (wasExpanded) SetPanelHeightNow(target);
    }

    private void SetPanelHeightNow(double height)
    {
        height = WithTray(height);
        PositionTray(height, animate: false);
        PanelClip.BeginAnimation(RectangleGeometry.RectProperty, null);
        PanelShadow.BeginAnimation(HeightProperty, null);
        PanelClip.Rect = new Rect(0, 0, _panelWidth, height);
        PanelShadow.Height = height;
        _panelHeight = height;
    }

    // ================= 工作台 =================

    private void ExpandButton_Click(object sender, RoutedEventArgs e) => ToggleWorkbench();

    /// <summary>搜索条 ⇄ 工作台：淡出 → 换尺寸 → 淡入，全程不重排正在显示的内容。</summary>
    private void ToggleWorkbench()
    {
        if (_hiding) return;
        CloseLook(animate: false);
        var target = !_workbench;
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fadeOut.Completed += (_, _) =>
        {
            if (_hiding || !IsVisible)
            {
                // 过渡途中窗口被收起：只换布局，不抢焦点
                ApplyWorkbenchLayout(target);
                Stage.BeginAnimation(OpacityProperty, null);
                Stage.Opacity = 1;
                return;
            }
            ApplyWorkbenchLayout(target);
            if (target) PositionWorkbench(); else PositionOnCursorMonitor();
            if (target && SearchBox.Text.Trim().Length == 0) ShowRecents();
            if (!target)
            {
                if (SearchBox.Text.Trim().Length == 0) Collapse(animate: false);
                else SetPanelHeightNow(_items.Count == 0 ? EmptyHeight : _expandedHeight);
            }
            var duration = TimeSpan.FromMilliseconds(220);
            Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
            StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, duration) { EasingFunction = Smooth });
            StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, duration) { EasingFunction = Smooth });
            Activate();
            SearchBox.Focus();
            if (_current >= 0) EnsureVisible(_current);
            ScheduleThumbnails();
        };
        Stage.BeginAnimation(OpacityProperty, fadeOut);
        StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, TimeSpan.FromMilliseconds(110)));
        StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, TimeSpan.FromMilliseconds(110)));
    }

    /// <summary>切换两种模式的外观和尺寸（不做动画，调用方负责过渡）。</summary>
    private void ApplyWorkbenchLayout(bool on)
    {
        _workbench = on;
        Topmost = !on;
        ShowInTaskbar = on;
        WorkbenchButtons.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ResizeGrip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = on ? "" : "";
        ExpandButton.ToolTip = on ? "收回搜索条 (Ctrl+Shift+Enter)" : "展开为工作台 (Ctrl+Shift+Enter)";
        DetailColumn.Width = new GridLength(on ? DetailWidth : 0);
        DetailPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FooterThumbBox.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        FooterInfo.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        FooterIndex.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        HeaderDrag.Cursor = on ? Cursors.SizeAll : null;

        if (on)
        {
            var (width, height) = WorkbenchSize(null);
            SetPanelSize(width, height);
        }
        else
        {
            _panelWidth = CompactWidth;
            PanelRoot.Width = CompactWidth;
            Width = CompactWidth + 40;
            FitToHeight(AvailableHeight(), force: true);
        }
        UpdateColumns();
        UpdateDetail();
        UpdateIndexStatus();
    }

    /// <summary>工作台尺寸：上次用的大小，超出屏幕就收小。</summary>
    private (double Width, double Height) WorkbenchSize(Size? requested)
    {
        var (availableWidth, availableHeight) = AvailableArea();
        var width = requested?.Width ?? _host.SettingsData.WindowWidth;
        var height = requested?.Height ?? _host.SettingsData.WindowHeight;
        width = Math.Clamp(width, Math.Min(MinWorkbenchWidth, availableWidth - 40), Math.Max(MinWorkbenchWidth, availableWidth - 40));
        height = Math.Clamp(height, Math.Min(MinWorkbenchHeight, availableHeight - 60), Math.Max(MinWorkbenchHeight, availableHeight - 60));
        return (Math.Floor(width), Math.Floor(height));
    }

    private void SetPanelSize(double width, double height)
    {
        _panelWidth = width;
        PanelRoot.Width = width;
        PanelRoot.Height = height;
        Width = width + 40;
        Height = height + 60;
        SetPanelHeightNow(height);
    }

    private void PositionWorkbench()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        try
        {
            var cursor = Forms.Cursor.Position;
            var area = Forms.Screen.FromPoint(cursor).WorkingArea;
            var scale = DpiScaleAt(cursor.X, cursor.Y);
            var width = (int)(Width * scale);
            var height = (int)(Height * scale);
            var x = area.Left + Math.Max(0, (area.Width - width) / 2);
            var y = area.Top + Math.Max(0, (area.Height - height) / 2 - (int)(8 * scale));
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch
        {
            Left = Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
            Top = Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2);
        }
    }

    private (double Width, double Height) AvailableArea()
    {
        try
        {
            var cursor = Forms.Cursor.Position;
            var area = Forms.Screen.FromPoint(cursor).WorkingArea;
            var scale = DpiScaleAt(cursor.X, cursor.Y);
            return (area.Width / scale, area.Height / scale);
        }
        catch { return (SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height); }
    }

    private double AvailableHeight() => AvailableArea().Height;

    /// <summary>按网格可用宽度算列数；列数变了才重排。</summary>
    private void UpdateColumns()
    {
        var gridWidth = _panelWidth - (_workbench ? DetailWidth : 0) - 22;
        var columns = _workbench ? Math.Max(3, (int)(gridWidth / TileSlot)) : 5;
        if (columns == _columns) return;
        var keepIndex = _current;
        _columns = columns;
        GridColumns = columns;
        if (_items.Count == 0) return;
        _tiles.Clear();
        _freshUntil = DateTime.MinValue; // 改列数不重播进场动画
        Rows.ItemsSource = BuildRows();
        if (keepIndex >= 0) Dispatcher.InvokeAsync(() => EnsureVisible(keepIndex), DispatcherPriority.Loaded);
        ScheduleThumbnails();
    }

    // ---- 拖动窗口 / 改大小 ----

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_workbench) { SearchBox.Focus(); return; }
        if (e.ClickCount == 2) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_workbench) return;
        var (availableWidth, availableHeight) = AvailableArea();
        var width = Math.Clamp(_panelWidth + e.HorizontalChange, MinWorkbenchWidth, Math.Max(MinWorkbenchWidth, availableWidth - 40));
        var height = Math.Clamp(PanelRoot.Height + e.VerticalChange, MinWorkbenchHeight, Math.Max(MinWorkbenchHeight, availableHeight - 60));
        SetPanelSize(Math.Floor(width), Math.Floor(height));
        // 拖动过程中列数变化做节流，松手前最多每 80ms 重排一次
        if (!_columnsTimer.IsEnabled) _columnsTimer.Start();
    }

    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _columnsTimer.Stop();
        UpdateColumns();
        _host.SettingsData.WindowWidth = _panelWidth;
        _host.SettingsData.WindowHeight = PanelRoot.Height;
        _host.SaveSettingsSoon();
        ScheduleThumbnails();
    }

    // ---- 顶部按钮 ----

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await _host.RebuildFromUiAsync();
        SearchBox.Focus();
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(_host.CycleTheme());
        SearchBox.Focus();
    }

    private void UpdateThemeGlyph()
    {
        if (ThemeManager.Current == ThemeManager.Light) { ThemeButton.Content = ""; ThemeButton.ToolTip = "浅色 · 点击切换到深色"; }
        else if (ThemeManager.Current == ThemeManager.Dark) { ThemeButton.Content = ""; ThemeButton.ToolTip = "深色 · 点击切换到跟随系统"; }
        else { ThemeButton.Content = ""; ThemeButton.ToolTip = "跟随系统 · 点击切换到浅色"; }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        // 设置窗口挂在工作台下面；从搜索条打开时先展开，避免搜索条失去焦点后自己收起
        if (!_workbench)
        {
            ApplyWorkbenchLayout(true);
            PositionWorkbench();
            if (SearchBox.Text.Trim().Length == 0) ShowRecents();
        }
        _host.OpenSettingsFrom(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host.SettingsData.MinimizeToTray)
        {
            HideAnimated();
            _host.ShowTrayTipOnce();
        }
        else _host.ExitApp();
    }

    private void NoResultsButton_Click(object sender, RoutedEventArgs e)
    {
        // 还没添加目录：打开首次引导（比设置页更好上手）；否则打开设置
        if (_host.SettingsData.IndexRoots.Count == 0) _host.ShowOnboarding();
        else OpenSettings();
    }

    /// <summary>索引是空的：告诉用户下一步做什么，而不是只显示“没有找到”。</summary>
    private void ShowSetupHint()
    {
        var noRoots = _host.SettingsData.IndexRoots.Count == 0;
        NoResults.Visibility = Visibility.Visible;
        _setupHintShown = true;
        if (!noRoots && (!_host.IndexLoaded || _host.IsIndexing))
        {
            NoResultsTitle.Text = _host.IndexLoaded ? "正在扫描共享盘" : "正在载入索引…";
            NoResultsHint.Text = "完成后这里会自动显示文件";
            NoResultsButton.Visibility = Visibility.Collapsed;
            return;
        }
        NoResultsTitle.Text = noRoots ? "还没有添加共享盘目录" : "索引还是空的";
        NoResultsHint.Text = noRoots ? "添加一个或几个共享盘文件夹，扫描一次之后就能秒搜" : "点右上角的刷新按钮扫描一次共享盘";
        NoResultsButton.Content = noRoots ? "添加共享盘目录" : "打开设置";
        NoResultsButton.Visibility = Visibility.Visible;
    }

    // ---- 底栏：索引状态 ----

    private bool _setupHintShown;

    private void UpdateIndexStatus()
    {
        if (!_workbench) return;
        if (_setupHintShown && NoResults.Visibility == Visibility.Visible && _items.Count == 0 && SearchBox.Text.Trim().Length == 0) ShowSetupHint();
        var summary = _host.IndexSummary;
        var status = _host.StatusMessage;
        FooterIndexText.Text = string.IsNullOrEmpty(status) || status == summary ? summary : $"{summary}  ·  {status}";
        FooterIndexDot.SetResourceReference(Shape.FillProperty, _host.StatusWarning ? "WarningBrush" : _host.StatusBusy ? "AccentBrush" : "SuccessBrush");
        FooterIndexDot.BeginAnimation(OpacityProperty, _host.StatusBusy
            ? new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
        RefreshButton.Content = _host.IsIndexing ? "" : "";
        RefreshButton.ToolTip = _host.IsIndexing ? "停止扫描" : "刷新索引（完整扫描一次共享盘）";
    }

    // ================= 详情栏 =================

    private void UpdateDetail()
    {
        if (!_workbench) return;
        var current = Current;
        var selected = _items.Count(x => x.IsSelected);
        if (current is null)
        {
            _detailEntry = null;
            _detailCts?.Cancel();
            DetailImage.Source = null;
            DetailGlyph.Text = "";
            DetailBadge.Visibility = Visibility.Collapsed;
            DetailEmpty.Visibility = Visibility.Visible;
            DetailName.Text = "";
            DetailMeta.Visibility = Visibility.Collapsed;
            DetailActions.IsEnabled = false;
            DetailSkuButton.Visibility = Visibility.Collapsed;
            return;
        }

        var entry = current.Entry;
        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailMeta.Visibility = Visibility.Visible;
        DetailActions.IsEnabled = true;
        DetailGlyph.Text = entry.IconGlyph;
        DetailGlyph.Foreground = entry.TypeBrush;
        DetailBadge.Visibility = entry.IsDirectory ? Visibility.Collapsed : Visibility.Visible;
        DetailBadge.Background = entry.TypeBrush;
        DetailBadgeText.Text = entry.ExtensionLabel;

        if (!ReferenceEquals(_detailEntry, entry))
        {
            _detailEntry = entry;
            _detailHiRes = false;
            _detailCts?.Cancel();
            DetailImage.Source = entry.Thumbnail;
            DetailImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(160)));
            _detailTimer.Stop();
            _detailTimer.Start();
        }
        else if (!_detailHiRes && entry.Thumbnail is not null && !ReferenceEquals(DetailImage.Source, entry.Thumbnail))
        {
            DetailImage.Source = entry.Thumbnail; // 缩略图刚到
        }
        DetailGlyph.Visibility = DetailImage.Source is null ? Visibility.Visible : Visibility.Collapsed;

        if (selected > 1)
        {
            var entries = _items.Where(x => x.IsSelected).Select(x => x.Entry).ToList();
            DetailName.Text = $"已选 {selected} 个文件";
            DetailType.Text = string.Join(" · ", entries.GroupBy(x => x.ExtensionLabel).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} {g.Count()}"));
            DetailDims.Text = "—";
            DetailSize.Text = FormatBytes(entries.Where(x => !x.IsDirectory).Sum(x => x.Size));
            DetailModified.Text = entries.Max(x => x.LastWriteUtc) is var newest && newest > DateTime.MinValue ? "最新 " + newest.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
            DetailPath.Text = "可以一起拖出，或按 Ctrl+Shift+C 一起复制";
            DetailOpenButton.Content = $"打开 {Math.Min(5, selected)} 个";
            DetailSkuButton.Visibility = Visibility.Collapsed;
            return;
        }

        DetailName.Text = entry.Name;
        DetailType.Text = entry.TypeLabel;
        DetailDims.Text = entry.Width > 0 ? entry.ResolutionLabel : entry.DurationSeconds > 0 ? entry.DurationLabel : "—";
        DetailDimsLabel.Text = entry.Width > 0 ? "尺寸" : entry.DurationSeconds > 0 ? "时长" : "尺寸";
        DetailSize.Text = entry.IsDirectory
            ? (_host.Folders.CachedInfo(entry.FullPath) is { } folderInfo ? FolderPreviewService.Describe(folderInfo) : "—")
            : entry.SizeLabel;
        DetailModified.Text = entry.LastWriteUtc == DateTime.MinValue ? "—" : entry.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        DetailPath.Text = entry.ParentPath + (entry.IsOffline ? "  ·  离线快照" : "");
        DetailOpenButton.Content = entry.IsDirectory ? "打开文件夹" : "打开";

        var sku = SkuDetector.Find(entry.Name);
        var searching = QueryParser.Compact(SearchBox.Text);
        if (sku is not null && QueryParser.Compact(sku) != searching)
        {
            DetailSkuButton.Content = $"查看同 SKU 的全部文件 · {sku}";
            DetailSkuButton.Tag = sku;
            DetailSkuButton.Visibility = Visibility.Visible;
        }
        else DetailSkuButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>选中停下 110ms 后再加载清晰大图，快速翻看时不会每张都去读共享盘。</summary>
    private async Task LoadDetailImageAsync()
    {
        var entry = _detailEntry;
        if (entry is null || !_workbench) return;
        _detailCts?.Cancel();
        var cancellation = _detailCts = new CancellationTokenSource();
        BitmapSource? image = null;
        if (entry.IsDirectory)
        {
            // 文件夹：大一点的拼图预览 + 里面有多少东西
            try
            {
                var (folderImage, info) = await _host.Folders.LoadAsync(entry, cancellation.Token, 512);
                if (cancellation.IsCancellationRequested || !ReferenceEquals(_detailEntry, entry)) return;
                DetailSize.Text = FolderPreviewService.Describe(info);
                if (folderImage is null) return;
                _detailHiRes = true;
                DetailImage.Source = folderImage;
                DetailGlyph.Visibility = Visibility.Collapsed;
            }
            catch { }
            return;
        }
        try
        {
            if (FileTypes.Images.Contains(entry.Extension))
                image = (await _host.Thumbnails.LoadPreviewImageAsync(entry.FullPath, 900, cancellation.Token)).Image;
            image ??= await _host.Thumbnails.LoadShellPreviewAsync(entry.FullPath, 512, cancellation.Token);
        }
        catch (OperationCanceledException) { return; }
        catch { }
        if (cancellation.IsCancellationRequested || image is null || !ReferenceEquals(_detailEntry, entry)) return;
        _detailHiRes = true;
        DetailImage.Source = image;
        DetailGlyph.Visibility = Visibility.Collapsed;
    }

    private void DetailPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Current is null) return;
        _gridMode = true;
        OpenLook();
        e.Handled = true;
    }

    private void DetailOpen_Click(object sender, RoutedEventArgs e) { OpenSelected(); SearchBox.Focus(); }
    private void DetailReveal_Click(object sender, RoutedEventArgs e) { RevealSelected(); SearchBox.Focus(); }
    private void DetailCopy_Click(object sender, RoutedEventArgs e) { CopyFiles(); SearchBox.Focus(); }
    private void DetailCopyPath_Click(object sender, RoutedEventArgs e) { CopyPaths(); SearchBox.Focus(); }

    private void DetailSku_Click(object sender, RoutedEventArgs e)
    {
        if (DetailSkuButton.Tag is not string sku) return;
        SearchBox.Text = sku;
        SearchBox.CaretIndex = sku.Length;
        SearchBox.Focus();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };
}
