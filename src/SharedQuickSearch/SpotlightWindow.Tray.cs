using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace SharedQuickSearch;

/// <summary>
/// 收集栏：中键点一张卡片，它从网格里划一道弧线飞进下方悬浮的收集栏。
/// 换个 SKU 继续搜、继续中键收集，最后一起复制或拖出。
/// 收集栏跨搜索、跨呼出保留（软件退出才清空）；Ctrl+D 收集选中的，Ctrl+Z 撤销清空。
/// </summary>
public partial class SpotlightWindow
{
    private const double TrayHeight = 92;
    private const double TrayGap = 12;
    private const double CardSlot = 70;

    private readonly ObservableCollection<TrayItem> _tray = new();
    private FileEntry[] _trayShown = Array.Empty<FileEntry>(); // 给后台线程看的只读快照
    private readonly HashSet<string> _trayPaths = new(StringComparer.OrdinalIgnoreCase);
    private double _panelBase = CollapsedHeight;
    private double _trayY;
    private bool _trayVisible;
    private List<FileEntry>? _lastCleared;
    private string _clearedQuery = "";
    // 正在播移出动画的卡片：逻辑上已经不在收集栏里，只是画面上还没消失
    private readonly HashSet<TrayItem> _leaving = new();
    private DateTime _clearedAt;
    private TrayItem? _pressedCard;
    private Point _cardPressPoint;
    private Point _trayDragPoint;
    private bool _trayDragPressed;

    private bool InTray(FileEntry entry) => _trayPaths.Contains(entry.FullPath);
    /// <summary>收集栏里还有东西：自动更新会等它清空（或者用户离开很久）再重启，免得收集的东西没了。</summary>
    public bool HasCollected => ActiveTrayCount > 0;
    public bool IsDraggingOut => _dragging;
    /// <summary>Windows 设置里关掉了“窗口内的动画”：直接出现，不飞。</summary>
    private static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;
    private IEnumerable<TrayItem> ActiveTray => _tray.Where(x => !_leaving.Contains(x));
    private IReadOnlyList<FileEntry> TrayEntries => ActiveTray.Select(x => x.Entry).ToList();
    private int ActiveTrayCount => _tray.Count - _leaving.Count;

    private void InitTray()
    {
        TrayList.ItemsSource = _tray;
        _tray.CollectionChanged += (_, _) => SyncTrayState();
    }

    private void SyncTrayState()
    {
        var active = ActiveTray.Select(x => x.Entry).ToArray();
        Volatile.Write(ref _trayShown, active);
        _trayPaths.Clear();
        foreach (var entry in active) _trayPaths.Add(entry.FullPath);
    }

    // ================= 面板高度：给收集栏留位置 =================

    /// <summary>
    /// 搜索条收起或没有结果时，面板往下多留一截放收集栏；展开时收集栏浮在网格底部、底栏上面。
    /// 工作台本来就是满高，收集栏直接浮在网格上。
    /// </summary>
    private double WithTray(double baseHeight)
    {
        _panelBase = baseHeight;
        SetCollapsedCover(!_workbench && baseHeight <= CollapsedHeight + 0.5);
        if (!_trayVisible || _workbench || baseHeight >= _expandedHeight - 0.5) return baseHeight;
        return baseHeight + TrayHeight + TrayGap * 2;
    }

    private bool _coverShown;

    /// <summary>收起时盖住网格的底板：和网格淡出同步淡入，不突然跳出来。</summary>
    private void SetCollapsedCover(bool show)
    {
        if (show == _coverShown) return;
        _coverShown = show;
        if (show)
        {
            CollapsedCover.Visibility = Visibility.Visible;
            CollapsedCover.BeginAnimation(OpacityProperty, ReduceMotion ? null : new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            if (ReduceMotion) CollapsedCover.Opacity = 1;
        }
        else
        {
            CollapsedCover.BeginAnimation(OpacityProperty, null);
            CollapsedCover.Opacity = 1;
            CollapsedCover.Visibility = Visibility.Collapsed;
        }
    }

    private void PositionTray(double panelHeight, bool animate, TimeSpan? duration = null)
    {
        var expandedLike = _workbench || _panelBase >= _expandedHeight - 0.5;
        _trayY = expandedLike ? panelHeight - 54 - TrayHeight - TrayGap : panelHeight - TrayHeight - TrayGap;
        var gridWidth = _panelWidth - (_workbench ? DetailWidth : 0);
        Tray.Margin = new Thickness(0, 0, _workbench ? DetailWidth : 0, 0);
        Tray.MaxWidth = Math.Max(320, gridWidth - 40);
        if (!_trayVisible) return;
        if (animate)
            TrayShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_trayY, duration ?? TimeSpan.FromMilliseconds(300)) { EasingFunction = Smooth }, HandoffBehavior.SnapshotAndReplace);
        else
        {
            TrayShift.BeginAnimation(TranslateTransform.YProperty, null);
            TrayShift.Y = _trayY;
        }
    }

    /// <summary>网格底部多留出收集栏的高度，最后一行能滚到收集栏上面。</summary>
    private void UpdateRowsPadding()
    {
        var bottom = 12 + (_trayVisible ? TrayHeight + TrayGap * 2 : 0);
        var padding = Rows.Padding;
        if (Math.Abs(padding.Bottom - bottom) > 0.5) Rows.Padding = new Thickness(padding.Left, padding.Top, padding.Right, bottom);
    }

    private void ShowTray()
    {
        if (_trayVisible) return;
        _trayVisible = true;
        Tray.Visibility = Visibility.Visible;
        UpdateRowsPadding();
        var target = WithTray(_panelBase);
        if (_workbench) PositionTray(_panelHeight, animate: false);
        else
        {
            AnimatePanelRaw(target);
            PositionTray(target, animate: false);
        }
        // 从下面浮上来
        TrayShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_trayY + 22, _trayY, TimeSpan.FromMilliseconds(380)) { EasingFunction = Smooth });
        Tray.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    private void HideTray()
    {
        if (!_trayVisible) return;
        _trayVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { if (!_trayVisible) Tray.Visibility = Visibility.Collapsed; };
        Tray.BeginAnimation(OpacityProperty, fade);
        TrayShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_trayY + 16, TimeSpan.FromMilliseconds(200)) { EasingFunction = EaseOut });
        UpdateRowsPadding();
        if (!_workbench) AnimatePanelRaw(WithTray(_panelBase));
    }

    // ================= 收集 / 移出 =================

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || (sender as FrameworkElement)?.Tag is not TileItem item) return;
        e.Handled = true;
        _gridMode = true;
        if (item.IsCollected) Uncollect(new[] { item.Entry });
        else Collect(new[] { item });
    }

    /// <summary>Ctrl+D：选中的有没收集的就全部收集，都收集过了就全部移出。</summary>
    private void ToggleCollectSelected()
    {
        var selected = _items.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0 && Current is { } current) selected.Add(current);
        if (selected.Count == 0) return;
        if (selected.All(x => InTray(x.Entry))) Uncollect(selected.Select(x => x.Entry));
        else Collect(selected.Where(x => !InTray(x.Entry)).ToList());
    }

    private void Collect(IReadOnlyList<TileItem> items, bool fly = true)
    {
        if (items.Count == 0) return;
        var wasVisible = _trayVisible;
        ShowTray();
        var delay = wasVisible ? 0 : 140; // 收集栏刚出现：等它浮到位再飞
        var index = 0;
        var added = 0;
        foreach (var item in items)
        {
            if (InTray(item.Entry)) continue;
            added++;
            var card = new TrayItem(item.Entry) { Arriving = true };
            _tray.Add(card);
            MarkCollected(item.Entry.FullPath, true);
            if (item.Entry.Thumbnail is null) _ = LoadThumbnailAsync(item.Entry, CancellationToken.None);
            var startDelay = delay + Math.Min(index++, 8) * 70;
            Dispatcher.InvokeAsync(async () =>
            {
                if (startDelay > 0) await Task.Delay(startDelay);
                if (fly) FlyIntoTray(item, card); else Land(card, CardFor(card));
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        if (added == 0) return;
        ShowStatus(added == 1 ? $"已收集 · 共 {ActiveTrayCount} 个，换个 SKU 继续搜" : $"已收集 {added} 个 · 共 {ActiveTrayCount} 个");
    }

    private void Uncollect(IEnumerable<FileEntry> entries, bool quiet = false)
    {
        var paths = new HashSet<string>(entries.Select(x => x.FullPath), StringComparer.OrdinalIgnoreCase);
        var leaving = ActiveTray.Where(x => paths.Contains(x.Entry.FullPath)).ToList();
        if (leaving.Count == 0) return;
        // 逻辑上立刻移出：动画还在播的时候撤销、或者再中键收集同一张，都按“已经不在”处理
        foreach (var item in leaving) _leaving.Add(item);
        SyncTrayState();
        UpdateTrayCount();
        foreach (var path in paths) MarkCollected(path, false);
        foreach (var item in leaving) AnimateCardOut(item);
        if (!quiet) ShowStatus(leaving.Count == 1 ? "已移出收集栏" : $"已移出 {leaving.Count} 个");
    }

    /// <summary>卡片缩小、让出位置，旁边的卡片顺势滑过来，然后真正移除。</summary>
    private void AnimateCardOut(TrayItem item)
    {
        var container = CardFor(item);
        void Remove()
        {
            if (!_leaving.Remove(item)) return;
            _tray.Remove(item);
            if (!Shows(item.Entry) && !_host.IsShownInMainWindow(item.Entry)) item.Entry.Thumbnail = null;
            UpdateTrayCount();
            if (_tray.Count == 0) HideTray();
        }
        if (container is null) { Remove(); return; }
        var duration = TimeSpan.FromMilliseconds(220);
        var shrink = new DoubleAnimation(0, duration) { EasingFunction = EaseOut };
        shrink.Completed += (_, _) => Remove();
        container.BeginAnimation(WidthProperty, shrink);
        container.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
    }

    private void MarkCollected(string path, bool collected)
    {
        foreach (var tile in _items)
            if (tile.Entry.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)) tile.IsCollected = collected;
    }

    // ================= 飞进收集栏 =================

    /// <summary>
    /// 从卡片缩略图的位置起飞：先往上轻轻一抛，再落进收集栏的空位，一边缩小一边微微转动。
    /// 网格里看不到这张卡片（滚走了 / Ctrl+D 收集的不在屏幕上）时直接在收集栏里出现。
    /// </summary>
    private void FlyIntoTray(TileItem tile, TrayItem card)
    {
        UpdateLayout();
        var from = TileThumbRect(tile);
        if (CardFor(card) is null || from is null || ReduceMotion || !IsVisible)
        {
            Land(card, CardFor(card));
            return;
        }

        var ghost = BuildGhost(tile.Entry, from.Value.Size);
        Canvas.SetLeft(ghost, from.Value.X);
        Canvas.SetTop(ghost, from.Value.Y);
        FlyLayer.Children.Add(ghost);
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var move = new TranslateTransform();
        ghost.RenderTransformOrigin = new Point(0.5, 0.5);
        ghost.RenderTransform = new TransformGroup { Children = { scale, rotate, move } };

        var fromCenter = new Point(from.Value.X + from.Value.Width / 2, from.Value.Y + from.Value.Height / 2);
        var firstTarget = LandingCenter(card) ?? fromCenter;
        var dx0 = firstTarget.X - fromCenter.X;
        var dy0 = firstTarget.Y - fromCenter.Y;
        var total = Math.Clamp(420 + Math.Sqrt(dx0 * dx0 + dy0 * dy0) * 0.35, 460, 640);
        var lift = Math.Min(70, 30 + Math.Abs(dx0) * 0.08);
        var factor = 62 / from.Value.Width;
        var tilt = dx0 >= 0 ? 7.0 : -7.0;
        var target = firstTarget;
        var overflow = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // 每一帧重新量落点：收集栏是居中的，新卡片占位时整排会往左挪，
        // 批量收集时后面的卡片还会再挤一下。跟着真实位置走，落下来正好对上空位。
        EventHandler? frame = null;
        frame = (_, _) =>
        {
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / total);
            if (LandingCenter(card, out overflow) is { } live) target = live;
            var dx = target.X - fromCenter.X;
            var dy = target.Y - fromCenter.Y;

            move.X = dx * CubicInOut(t);
            move.Y = t < 0.3
                ? -lift * Math.Sin(t / 0.3 * Math.PI / 2)
                : -lift + (dy + lift) * Math.Pow((t - 0.3) / 0.7, 2);
            var sc = t < 0.18
                ? 1 + 0.06 * QuadOut(t / 0.18)
                : 1.06 + (factor - 1.06) * Math.Pow((t - 0.18) / 0.82, 3);
            scale.ScaleX = scale.ScaleY = sc;
            rotate.Angle = t < 0.45 ? tilt * QuadOut(t / 0.45) : tilt * (1 - QuadOut((t - 0.45) / 0.55));

            if (t < 1 && IsVisible) return;
            CompositionTarget.Rendering -= frame;
            FlyLayer.Children.Remove(ghost);
            if (overflow) TrayScroll.ScrollToRightEnd();
            if (_tray.Contains(card) && !_leaving.Contains(card)) Land(card, CardFor(card));
        };
        CompositionTarget.Rendering += frame;
    }

    private static double CubicInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    private static double QuadOut(double t) => 1 - (1 - t) * (1 - t);

    private Point? LandingCenter(TrayItem card) => LandingCenter(card, out _);

    /// <summary>空位中心在飞行层里的坐标。收集栏还在往上浮时按最终位置算；超出可视范围时落在最右边。</summary>
    private Point? LandingCenter(TrayItem card, out bool overflow)
    {
        overflow = false;
        if (CardFor(card) is not { } container || !container.IsVisible) return null;
        try
        {
            var origin = container.TranslatePoint(new Point(0, 0), FlyLayer);
            origin.Y += _trayY - TrayShift.Y;
            // 卡片宽度还在从 0 长到 70：按长满以后的位置算（居中的收集栏会往左让一半）
            var growing = CardSlot - container.ActualWidth;
            if (growing > 0) origin.X -= Tray.ActualWidth + growing <= Tray.MaxWidth ? growing / 2 : 0;
            var viewportRight = TrayScroll.TranslatePoint(new Point(TrayScroll.ViewportWidth, 0), FlyLayer).X;
            if (origin.X + CardSlot > viewportRight + 1)
            {
                overflow = true;
                origin.X = viewportRight - CardSlot;
            }
            return new Point(origin.X + CardSlot / 2.0, origin.Y + CardSlot / 2.0);
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>飞行用的“替身”：和网格卡片一样的圆角底 + 缩略图，带一点阴影。</summary>
    private static FrameworkElement BuildGhost(FileEntry entry, Size size)
    {
        var ghost = new Border { Width = size.Width, Height = size.Height, CornerRadius = new CornerRadius(11) };
        ghost.SetResourceReference(Border.BackgroundProperty, "SpotlightTileBrush");
        ghost.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 8, Direction = 270, Opacity = .28, Color = Colors.Black, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        if (entry.Thumbnail is { } thumb)
            ghost.Child = new Image { Source = thumb, Margin = new Thickness(8), Stretch = Stretch.Uniform };
        else
        {
            var glyph = new TextBlock { Text = entry.IconGlyph, FontSize = 34, Foreground = entry.TypeBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            ghost.Child = glyph;
        }
        return ghost;
    }

    /// <summary>落地：空位里的卡片弹一下出现，计数跳一下。</summary>
    private void Land(TrayItem card, FrameworkElement? container)
    {
        card.Arriving = false;
        UpdateTrayCount(pulse: true);
        if (container is null) return;
        container.BeginAnimation(OpacityProperty, null);
        container.Opacity = 1;
        if (CardScale(container) is { } scale)
        {
            var pop = new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(340)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    /// <summary>卡片的缩放变换；模板里的是冻结的，换成一个能动画的。</summary>
    private static ScaleTransform? CardScale(FrameworkElement container)
    {
        if (FindNamed<Border>(container, "CardBody") is not { } body) return null;
        if (body.RenderTransform is ScaleTransform { IsFrozen: false } live) return live;
        var fresh = new ScaleTransform(1, 1);
        body.RenderTransformOrigin = new Point(0.5, 0.5);
        body.RenderTransform = fresh;
        return fresh;
    }

    private void UpdateTrayCount(bool pulse = false)
    {
        var count = ActiveTrayCount;
        TrayCount.Text = count.ToString();
        TrayCopyButton.Content = count > 1 ? $"复制 {count} 个" : "复制";
        if (!pulse || ReduceMotion) return;
        var bump = new DoubleAnimationUsingKeyFrames();
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(1.28, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), EaseOut));
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), EaseOut));
        TrayCountScale.BeginAnimation(ScaleTransform.ScaleXProperty, bump);
        TrayCountScale.BeginAnimation(ScaleTransform.ScaleYProperty, bump);
    }

    private void TrayCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement container || container.Tag is not TrayItem item || !item.Arriving) return;
        // 新卡片先占位（宽度从 0 长出来，旁边的卡片顺势让开），内容等飞过来再显示
        container.Opacity = 0;
        container.BeginAnimation(WidthProperty, new DoubleAnimation(0, CardSlot, TimeSpan.FromMilliseconds(ReduceMotion ? 0 : 240)) { EasingFunction = Smooth });
    }

    private FrameworkElement? CardFor(TrayItem item)
    {
        if (TrayList.ItemContainerGenerator.ContainerFromItem(item) is not ContentPresenter presenter) return null;
        return FindNamed<Grid>(presenter, "Card");
    }

    private Rect? TileThumbRect(TileItem item)
    {
        if (!_tiles.TryGetValue(item, out var tile) || !tile.IsVisible) return null;
        if (FindNamed<Border>(tile, "Thumb") is not { ActualWidth: > 0 } thumb) return null;
        try { return thumb.TransformToVisual(FlyLayer).TransformBounds(new Rect(thumb.RenderSize)); }
        catch (InvalidOperationException) { return null; }
    }

    // ================= 收集栏里的操作 =================

    private void TrayCopy_Click(object sender, RoutedEventArgs e)
    {
        CopyTray();
        SearchBox.Focus();
    }

    private void CopyTray()
    {
        var entries = TrayEntries;
        if (entries.Count == 0) return;
        try
        {
            FileActions.CopyItems(entries);
            PulseCards();
            ShowStatus($"已复制 {entries.Count} 个文件，到别处直接粘贴");
            _host.RecordRecent("", entries);
        }
        catch (Exception ex) { ShowStatus("复制失败：" + ex.Message); }
    }

    private void TrayClear_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTrayCount == 0) return;
        _lastCleared = TrayEntries.ToList();
        _clearedAt = DateTime.Now;
        _clearedQuery = SearchBox.Text;
        Uncollect(_lastCleared, quiet: true);
        ShowStatus($"已清空 {_lastCleared.Count} 个 · Ctrl+Z 撤销");
        SearchBox.Focus();
    }

    /// <summary>清空后 15 秒内 Ctrl+Z 放回来（卡片直接出现，不再飞）。</summary>
    private bool TryUndoClear()
    {
        if (_lastCleared is null || (DateTime.Now - _clearedAt).TotalSeconds > 15) return false;
        // 清空之后又打过字：Ctrl+Z 留给搜索框撤销文字
        if (!string.Equals(SearchBox.Text, _clearedQuery, StringComparison.Ordinal)) { _lastCleared = null; return false; }
        var restore = _lastCleared.Where(x => !InTray(x)).ToList();
        _lastCleared = null;
        if (restore.Count == 0) return true;
        ShowTray();
        foreach (var entry in restore)
        {
            _tray.Add(new TrayItem(entry));
            MarkCollected(entry.FullPath, true);
            if (entry.Thumbnail is null) _ = LoadThumbnailAsync(entry, CancellationToken.None);
        }
        UpdateTrayCount(pulse: true);
        ShowStatus($"已放回 {restore.Count} 个");
        return true;
    }

    /// <summary>复制成功：收集栏里的卡片依次轻轻跳一下。</summary>
    private void PulseCards()
    {
        if (ReduceMotion) return;
        var index = 0;
        foreach (var item in _tray)
        {
            if (CardFor(item) is not { } container || CardScale(container) is not { } scale) continue;
            var bump = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(Math.Min(index++, 12) * 35) };
            bump.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), EaseOut));
            bump.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), EaseOut));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, bump);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, bump);
        }
    }

    // ---- 拖出全部 ----

    private void TrayDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _trayDragPressed = true;
        _trayDragPoint = e.GetPosition(this);
        e.Handled = true;
    }

    private void TrayDrag_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_trayDragPressed || e.LeftButton != MouseButtonState.Pressed || _dragging) return;
        var delta = e.GetPosition(this) - _trayDragPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _trayDragPressed = false;
        DragEntries((DependencyObject)sender, TrayEntries, pulse: true);
    }

    private void TrayDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 单击“拖出全部”而没有拖：提示一下怎么用
        if (_trayDragPressed) ShowStatus("按住它拖到桌面、文件夹或微信里，就是全部复制过去");
        _trayDragPressed = false;
    }

    private void DragEntries(DependencyObject source, IReadOnlyList<FileEntry> entries, bool pulse)
    {
        if (entries.Count == 0) return;
        _dragging = true;
        try
        {
            var effect = DragOut.Start(source, entries, new Point(84, 75));
            if (effect.HasFlag(DragDropEffects.Copy))
            {
                if (pulse) PulseCards();
                ShowStatus(entries.Count == 1 ? "已复制到目标位置，原文件没动" : $"已复制 {entries.Count} 个文件，原文件没动");
                _host.RecordRecent("", entries);
            }
        }
        catch (Exception ex) { ShowStatus("拖出失败：" + ex.Message); }
        finally { _dragging = false; }
        if (!IsActive && !_workbench) HideAnimated();
    }

    // ---- 单张卡片 ----

    private void TrayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TrayItem item) return;
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            try { FileActions.Open(item.Entry); _host.RecordRecent("", new[] { item.Entry }); if (!_workbench) HideAnimated(); }
            catch (Exception ex) { ShowStatus("打开失败：" + ex.Message); }
            return;
        }
        _pressedCard = item;
        _cardPressPoint = e.GetPosition(this);
    }

    private void TrayCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedCard is null || e.LeftButton != MouseButtonState.Pressed || _dragging) return;
        var delta = e.GetPosition(this) - _cardPressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 1.5 && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 1.5) return;
        var item = _pressedCard;
        _pressedCard = null;
        DragEntries((DependencyObject)sender, new[] { item.Entry }, pulse: false);
    }

    private void TrayCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TrayItem item && ReferenceEquals(item, _pressedCard))
        {
            // 单击：结果里有它就选中并滚过去；没有就在底栏显示它在哪
            var index = _items.FindIndex(x => x.Entry.FullPath.Equals(item.Entry.FullPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) { _gridMode = true; Select(index); }
            else ShowStatus($"{item.Entry.Name} · {item.Entry.ParentPath}");
        }
        _pressedCard = null;
        SearchBox.Focus();
    }

    private void TrayCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || (sender as FrameworkElement)?.Tag is not TrayItem item) return;
        e.Handled = true;
        Uncollect(new[] { item.Entry });
    }

    private void TrayCardRemove_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is TrayItem item) Uncollect(new[] { item.Entry });
    }

    private void TrayCard_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TrayItem item) return;
        e.Handled = true;
        var entry = item.Entry;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(MenuItemFor("打开", "双击", "", () => { try { FileActions.Open(entry); } catch (Exception ex) { ShowStatus("打开失败：" + ex.Message); } }));
        menu.Items.Add(MenuItemFor("打开所在位置", "", "", () => { try { FileActions.Reveal(entry); } catch (Exception ex) { ShowStatus("定位失败：" + ex.Message); } }));
        menu.Items.Add(MenuItemFor("复制这个文件", "", "", () => { try { FileActions.CopyItems(new[] { entry }); ShowStatus("文件已复制，可以直接粘贴"); } catch (Exception ex) { ShowStatus("复制失败：" + ex.Message); } }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("移出收集栏", "中键", "", () => Uncollect(new[] { entry })));
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; SearchBox.Focus(); };
        menu.IsOpen = true;
    }

    private void TrayScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 收集栏只横向滚动：竖滚轮当横滚用
        TrayScroll.ScrollToHorizontalOffset(TrayScroll.HorizontalOffset - e.Delta * 0.6);
        e.Handled = true;
    }
}
