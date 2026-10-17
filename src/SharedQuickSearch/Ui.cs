using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SharedQuickSearch;

/// <summary>样式用到的附加属性和几个通用动效。</summary>
public static class Ui
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(8)));
    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.RegisterAttached(
        "HoverBrush", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));
    public static Brush? GetHoverBrush(DependencyObject element) => (Brush?)element.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(DependencyObject element, Brush? value) => element.SetValue(HoverBrushProperty, value);

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));
    public static string GetPlaceholder(DependencyObject element) => (string)element.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>淡入并从下方轻移到位，用于内容切换。</summary>
    public static void FadeIn(UIElement element, double offsetY = 6, int milliseconds = 200)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
        if (offsetY == 0) return;
        if (element.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
        {
            translate = new TranslateTransform();
            element.RenderTransform = translate;
        }
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, duration) { EasingFunction = EaseOut });
    }

    /// <summary>淡入并从 98% 放大到 100%，用于预览图。</summary>
    public static void ZoomIn(UIElement element, int milliseconds = 220)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform();
            element.RenderTransform = scale;
        }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.975, 1, duration) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.975, 1, duration) { EasingFunction = EaseOut });
    }

    public static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
