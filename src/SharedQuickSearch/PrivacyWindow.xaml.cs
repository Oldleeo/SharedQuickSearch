using System.Windows;

namespace SharedQuickSearch;

public partial class PrivacyWindow : Window
{
    public PrivacyWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Ui.FadeIn(Body, offsetY: 10, milliseconds: 300);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this, ThemeManager.IsDarkActive);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
