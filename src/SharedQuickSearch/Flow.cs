using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SharedQuickSearch;

/// <summary>
/// 安装、卸载、首次引导三个流程窗口共用的动效：
/// 面板弹出、页面左右滑动切换、步骤圆点、拖动窗口。
/// 只动透明度和位移，不动布局，切页稳定满帧。
/// </summary>
internal static class Flow
{
    public static readonly IEasingFunction Smooth = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>窗口出现：面板从 96% 放大并淡入。</summary>
    public static void PlayOpen(FrameworkElement stage)
    {
        var scale = new ScaleTransform(0.96, 0.96);
        stage.RenderTransformOrigin = new Point(0.5, 0.5);
        stage.RenderTransform = scale;
        stage.Opacity = 0;
        var duration = TimeSpan.FromMilliseconds(320);
        stage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = Smooth });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = Smooth });
    }

    /// <summary>窗口关闭：淡出并略微缩小，结束后执行 done。</summary>
    public static void PlayClose(FrameworkElement stage, Action done)
    {
        if (stage.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1);
            stage.RenderTransformOrigin = new Point(0.5, 0.5);
            stage.RenderTransform = scale;
        }
        var duration = TimeSpan.FromMilliseconds(150);
        var fade = new DoubleAnimation(0, duration);
        fade.Completed += (_, _) => done();
        stage.BeginAnimation(UIElement.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, duration));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, duration));
    }

    /// <summary>
    /// 切换到 next 页。direction = 1 向前（新页从右边滑入），-1 向后。
    /// 页面都放在同一个 Grid 里叠着，只有当前页 Visible。
    /// </summary>
    public static void ShowPage(Panel container, FrameworkElement next, int direction)
    {
        foreach (FrameworkElement page in container.Children)
        {
            if (ReferenceEquals(page, next) || page.Visibility != Visibility.Visible) continue;
            var leaving = page;
            var shiftOut = EnsureShift(leaving);
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fadeOut.Completed += (_, _) =>
            {
                if (leaving.Opacity < 0.01 && !ReferenceEquals(leaving.Tag, "active")) leaving.Visibility = Visibility.Collapsed;
            };
            leaving.Tag = null;
            leaving.IsHitTestVisible = false;
            leaving.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            shiftOut.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-28 * direction, TimeSpan.FromMilliseconds(160)) { EasingFunction = EaseOut });
        }

        next.Tag = "active";
        next.IsHitTestVisible = true;
        next.Visibility = Visibility.Visible;
        var shift = EnsureShift(next);
        var duration = TimeSpan.FromMilliseconds(360);
        var delay = TimeSpan.FromMilliseconds(70);
        next.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = delay, EasingFunction = EaseOut });
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(36 * direction, 0, duration) { BeginTime = delay, EasingFunction = Smooth });
        next.Opacity = 0;
    }

    /// <summary>第一页直接显示，不做动画。</summary>
    public static void ShowPageNow(Panel container, FrameworkElement page)
    {
        foreach (FrameworkElement child in container.Children)
        {
            child.BeginAnimation(UIElement.OpacityProperty, null);
            var current = ReferenceEquals(child, page);
            child.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
            child.Opacity = current ? 1 : 0;
            child.Tag = current ? "active" : null;
            child.IsHitTestVisible = current;
        }
    }

    /// <summary>页面里的元素依次上浮淡入（标题、说明、列表……）。</summary>
    public static void Stagger(IEnumerable<UIElement> items, int startMs = 120, int stepMs = 45)
    {
        var index = 0;
        foreach (var item in items)
        {
            var shift = EnsureShiftY(item);
            var begin = TimeSpan.FromMilliseconds(startMs + stepMs * index++);
            item.Opacity = 0;
            item.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = EaseOut });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(380)) { BeginTime = begin, EasingFunction = Smooth });
        }
    }

    /// <summary>步骤圆点：当前那个变成强调色胶囊。</summary>
    public static void UpdateDots(Panel dots, int current)
    {
        for (var i = 0; i < dots.Children.Count; i++)
        {
            if (dots.Children[i] is not Border dot) continue;
            var active = i == current;
            dot.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(active ? 22 : 6, TimeSpan.FromMilliseconds(280)) { EasingFunction = Smooth });
            dot.SetResourceReference(Border.BackgroundProperty, active ? "AccentBrush" : i < current ? "TextTertiaryBrush" : "StrokeStrongBrush");
        }
    }

    public static void BuildDots(Panel dots, int count, Style style)
    {
        dots.Children.Clear();
        for (var i = 0; i < count; i++) dots.Children.Add(new Border { Style = style });
    }

    public static void DragWindow(Window window, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount > 1) return;
        try { window.DragMove(); } catch (InvalidOperationException) { }
    }

    private static TranslateTransform EnsureShift(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } shift) return shift;
        shift = new TranslateTransform();
        element.RenderTransform = shift;
        return shift;
    }

    private static TranslateTransform EnsureShiftY(UIElement element) => EnsureShift(element);
}
