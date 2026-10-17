using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;

namespace SharedQuickSearch;

/// <summary>跟随主题的提示框，替代系统 MessageBox。</summary>
public static class ModernDialog
{
    public static void Show(string message, string title, MessageBoxImage image = MessageBoxImage.Information) =>
        Show(Application.Current?.MainWindow, message, title, image);

    public static void Show(Window? owner, string message, string title, MessageBoxImage image = MessageBoxImage.Information)
    {
        var (glyph, brushKey) = image switch
        {
            MessageBoxImage.Error => ("", "DangerBrush"),
            MessageBoxImage.Warning => ("", "WarningBrush"),
            _ => ("", "AccentBrush")
        };

        var window = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.SetResourceReference(FrameworkElement.StyleProperty, "WindowBase");
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 40, GlassFrameThickness = new Thickness(0, 0, 0, 1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });

        var iconText = new TextBlock { Text = glyph, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        iconText.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        iconText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        var iconCircle = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Child = iconText, VerticalAlignment = VerticalAlignment.Top };
        iconCircle.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");

        var titleText = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        titleText.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        var messageText = new TextBox { Text = message, Margin = new Thickness(0, 6, 0, 0), MaxHeight = 320 };
        messageText.SetResourceReference(FrameworkElement.StyleProperty, "SelectableText");
        messageText.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");

        var textStack = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        textStack.Children.Add(titleText);
        textStack.Children.Add(messageText);
        var head = new DockPanel();
        DockPanel.SetDock(iconCircle, Dock.Left);
        head.Children.Add(iconCircle);
        head.Children.Add(textStack);

        var ok = new Button { Content = "知道了", Width = 96, Height = 36, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        ok.Click += (_, _) => window.Close();

        var body = new StackPanel { Margin = new Thickness(24, 26, 24, 22) };
        body.Children.Add(head);
        body.Children.Add(ok);
        var root = new Border { Child = body };
        root.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        window.Content = root;

        window.SourceInitialized += (_, _) => WindowEffects.Apply(window, ThemeManager.IsDarkActive);
        window.Loaded += (_, _) => { Ui.FadeIn(body, offsetY: 8, milliseconds: 220); ok.Focus(); };
        window.ShowDialog();
    }
}
