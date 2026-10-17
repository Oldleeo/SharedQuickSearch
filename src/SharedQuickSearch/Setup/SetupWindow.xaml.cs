using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace SharedQuickSearch.Setup;

public enum SetupMode { Install, Uninstall }

/// <summary>
/// 安装 / 卸载界面。和搜索条同一套面板、字体和动效。
/// 安装：欢迎 → 位置 → 快捷方式 → 进度 → 完成。卸载：确认 → 进度 → 完成。
/// </summary>
public partial class SetupWindow : Window
{
    private readonly SetupMode _mode;
    private readonly string? _uninstallDirectory;
    private bool _registrationOnly; // 程序文件找不到 / 在网络位置：只清登记，不删文件
    private readonly List<FrameworkElement> _flow = new();
    private int _page;
    private bool _busy;
    private bool _finished;
    private bool _failed;
    private string? _installedExe;
    private TaskCompletionSource<bool>? _runningChoice;
    private readonly List<(string Key, Grid Row)> _steps = new();

    public SetupWindow(SetupMode mode, string? uninstallDirectory = null)
    {
        _mode = mode;
        _uninstallDirectory = uninstallDirectory;
        InitializeComponent();

        if (mode == SetupMode.Install)
        {
            Title = "安装共享速搜";
            HeaderMode.Text = "安装 " + InstallerEngine.Version;
            WelcomeEyebrow.Text = "版本 " + InstallerEngine.Version;
            _flow.AddRange(new FrameworkElement[] { PageWelcome, PageLocation, PageOptions, PageProgress, PageDone });
            var installed = InstallerEngine.FindInstalled();
            LocationBox.Text = installed?.Directory ?? InstallerEngine.DefaultDirectory;
            if (installed is not null)
            {
                var compare = InstallerEngine.CompareVersions(InstallerEngine.Version, installed.Version);
                UpgradeNoteText.Text = compare > 0 ? $"已安装 {installed.Version}，这次会升级到 {InstallerEngine.Version}。设置、索引和缩略图缓存都保留。"
                    : compare == 0 ? $"这台电脑已经装了 {installed.Version}。继续会重新安装一遍，设置和索引都保留。"
                    : $"这台电脑装的是更新的 {installed.Version}，继续会换回 {InstallerEngine.Version}。";
                UpgradeNote.Visibility = Visibility.Visible;
            }
        }
        else
        {
            Title = "卸载共享速搜";
            HeaderMode.Text = "卸载";
            _flow.AddRange(new FrameworkElement[] { PageUninstall, PageProgress, PageDone });
            UninstallLocation.Text = "程序位置：" + (_uninstallDirectory ?? "未找到");
            if (_uninstallDirectory is null && InstallerEngine.HasRegistration())
            {
                _registrationOnly = true;
                UninstallTitle.Text = "清理共享速搜的安装记录";
                UninstallLead.Text = "程序文件已经不在了，或者装在网络位置上（为了安全不去删）。可以只清掉快捷方式、开机启动项和“设置 → 应用”里的这一条，不会删除任何程序文件。";
                UninstallLocation.Text = "";
            }
            _ = MeasureDataAsync();
        }

        Flow.BuildDots(Dots, _flow.Count, (Style)FindResource("StepDot"));
        Flow.ShowPageNow(Pages, _flow[0]);
        UpdateChrome();
        Loaded += (_, _) =>
        {
            if (_mode == SetupMode.Uninstall && _uninstallDirectory is null && !_registrationOnly)
            {
                // 没有安装记录（或者参数指向别的位置）：什么都不删
                Flow.PlayOpen(Stage);
                ShowDone(success: false, "没有找到已安装的共享速搜",
                    "这台电脑的当前用户下没有安装记录，所以没有删除任何文件。如果是直接运行的便携版，关掉它、删掉那个 exe 就可以了。");
                return;
            }
            Flow.PlayOpen(Stage);
            Flow.Stagger(ContentOf(_flow[0]), startMs: 140);
            StartAmbient();
        };
    }

    // ================= 翻页 =================

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var page = _flow[_page];
        if (_finished || ReferenceEquals(page, PageDone)) { Finish(); return; }

        if (ReferenceEquals(page, PageLocation) && !ValidateLocation()) return;
        if (ReferenceEquals(page, PageOptions) || ReferenceEquals(page, PageUninstall))
        {
            GoTo(_page + 1);
            if (_mode == SetupMode.Install) await RunInstallAsync(); else await RunUninstallAsync();
            return;
        }
        GoTo(_page + 1);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _page == 0) return;
        GoTo(_page - 1);
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
        if (ReferenceEquals(_flow[index], PageLocation)) UpdateSpace();
    }

    private void UpdateChrome()
    {
        Flow.UpdateDots(Dots, _page);
        var page = _flow[_page];
        BackButton.Visibility = _page > 0 && !ReferenceEquals(page, PageProgress) && !ReferenceEquals(page, PageDone) ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = !ReferenceEquals(page, PageProgress);
        NextButton.SetResourceReference(StyleProperty, ReferenceEquals(page, PageUninstall) ? "DangerButton" : "PrimaryButton");
        NextButton.Content = page switch
        {
            _ when ReferenceEquals(page, PageWelcome) => InstallerEngine.FindInstalled() is null ? "开始安装" : "继续",
            _ when ReferenceEquals(page, PageOptions) => "安装",
            _ when ReferenceEquals(page, PageUninstall) => "卸载",
            _ when ReferenceEquals(page, PageProgress) => _mode == SetupMode.Install ? "安装中…" : "卸载中…",
            _ when ReferenceEquals(page, PageDone) => _failed ? "关闭" : _mode == SetupMode.Install && LaunchCheck.IsChecked == true ? "打开共享速搜" : "完成",
            _ => "下一步"
        };
        CloseButton.IsEnabled = !_busy;
        // 欢迎页和完成页背景有一团慢慢漂移的光
        var glow = ReferenceEquals(page, PageWelcome) || ReferenceEquals(page, PageDone) ? 1 : 0.35;
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(glow, TimeSpan.FromMilliseconds(500)));
    }

    private static IEnumerable<UIElement> ContentOf(FrameworkElement page)
    {
        if (page is Panel { Children.Count: > 0 } grid && grid.Children[0] is Panel stack) return stack.Children.Cast<UIElement>().ToList();
        return Array.Empty<UIElement>();
    }

    // ================= 位置 =================

    private void BrowseLocation_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择安装位置（会在里面建一个“共享速搜”文件夹）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(LocationBox.Text) ? LocationBox.Text : InstallerEngine.DefaultDirectory
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        LocationBox.Text = InstallerEngine.NormalizeTarget(dialog.SelectedPath);
    }

    private void LocationBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSpace();

    private void UpdateSpace()
    {
        if (SpaceText is null) return;
        var need = InstallerEngine.PackageSize;
        var free = InstallerEngine.FreeSpace(LocationBox.Text.Length > 0 ? LocationBox.Text : InstallerEngine.DefaultDirectory);
        var enough = free < 0 || free > need * 1.2;
        SpaceText.Text = free < 0
            ? $"需要约 {Mb(need)}"
            : $"需要约 {Mb(need)} · 这个磁盘还有 {Size(free)} 可用";
        SpaceDot.SetResourceReference(Shape.FillProperty, enough ? "SuccessBrush" : "DangerBrush");

        string? warning = null;
        var path = LocationBox.Text.Trim();
        if (path.Length > 2 && !InstallerEngine.IsLocalPath(path)) warning = "这是网络位置，不能装在这里：请选这台电脑的本机磁盘。";
        else if (!enough) warning = "磁盘空间不够，换一个位置吧。";
        LocationWarning.Text = warning ?? "";
        LocationWarning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool ValidateLocation()
    {
        var text = LocationBox.Text.Trim().Trim('"');
        if (text.Length == 0 || !System.IO.Path.IsPathRooted(text))
        {
            Shake(LocationContent);
            LocationWarning.Text = "请填一个完整的文件夹路径，比如 D:\\Apps\\共享速搜。";
            LocationWarning.Visibility = Visibility.Visible;
            return false;
        }
        try { System.IO.Path.GetFullPath(text); }
        catch
        {
            Shake(LocationContent);
            LocationWarning.Text = "这个路径里有不能用的字符。";
            LocationWarning.Visibility = Visibility.Visible;
            return false;
        }
        if (!InstallerEngine.IsLocalPath(text))
        {
            Shake(LocationContent);
            LocationWarning.Text = "请装在这台电脑的本机磁盘上。装在网络位置，启动慢、断网打不开，卸载时为了安全也不会去删。";
            LocationWarning.Visibility = Visibility.Visible;
            return false;
        }
        var free = InstallerEngine.FreeSpace(text);
        if (free >= 0 && free < InstallerEngine.PackageSize * 1.2) { Shake(LocationContent); return false; }
        // 手动输入的路径也要套一层“共享速搜”文件夹，避免把程序直接放进 D:\Tools 或桌面
        LocationBox.Text = InstallerEngine.NormalizeTarget(text);
        return true;
    }

    // ================= 安装 =================

    private async Task RunInstallAsync()
    {
        _busy = true;
        UpdateChrome();
        ProgressEyebrow.Text = "正在安装";
        BuildSteps(("close", "关闭正在运行的共享速搜"), ("copy", "复制程序文件"), ("link", "创建快捷方式"), ("reg", "写入卸载信息"));
        StartShine();
        try
        {
            SetStep("close", StepState.Running);
            ProgressTitle.Text = "准备安装";
            if (!await CloseRunningAsync()) { CancelBack(); return; }
            SetStep("close", StepState.Done);

            var options = new InstallOptions(System.IO.Path.GetFullPath(LocationBox.Text.Trim().Trim('"')), DesktopCheck.IsChecked == true, StartMenuCheck.IsChecked == true);
            var progress = new Progress<SetupProgress>(OnProgress);
            _installedExe = await InstallerEngine.InstallAsync(options, progress, CancellationToken.None);
            foreach (var (key, _) in _steps) SetStep(key, StepState.Done);
            await Task.Delay(260); // 让进度条走满再翻页
            ShowDone(success: true,
                "装好了",
                $"已装到 {options.Directory}。第一次打开会带你添加共享盘目录、选择不要录入的文件夹，大约一分钟。");
        }
        catch (Exception ex)
        {
            ShowDone(success: false, "安装没有完成", "原因：" + ex.Message + "\n已经复制的文件不会影响旧版本，可以直接再试一次。");
        }
        finally { _busy = false; StopShine(); UpdateChrome(); }
    }

    private void OnProgress(SetupProgress value)
    {
        AnimateBar(value.Fraction);
        var key = value.Message switch
        {
            "复制程序文件" => "copy",
            "创建快捷方式" => "link",
            "写入卸载信息" => "reg",
            "关闭开机启动" => "startup",
            "删除快捷方式" => "link",
            "删除程序文件" => "files",
            "删除卸载信息" => "reg",
            "删除设置、索引和缩略图缓存" => "data",
            _ => null
        };
        if (key is null) return;
        // 当前之前的步骤都标成完成
        var reached = false;
        foreach (var (stepKey, _) in _steps.AsEnumerable().Reverse())
        {
            if (stepKey == key) { reached = true; SetStep(stepKey, StepState.Running); continue; }
            if (reached) SetStep(stepKey, StepState.Done);
        }
        ProgressTitle.Text = value.Message;
    }

    /// <summary>请旧版本退出；不响应就在界面上问要不要强制关闭。</summary>
    private async Task<bool> CloseRunningAsync()
    {
        var paths = RunningCandidates();
        if (InstallerEngine.FindRunning(paths).Count == 0) return true;
        ProgressTitle.Text = "正在关闭共享速搜";
        if (await InstallerEngine.RequestExitAsync(paths, TimeSpan.FromSeconds(6))) return true;

        RunningPanel.Visibility = Visibility.Visible;
        Flow.Stagger(new UIElement[] { RunningPanel }, startMs: 0);
        _runningChoice = new TaskCompletionSource<bool>();
        var force = await _runningChoice.Task;
        RunningPanel.Visibility = Visibility.Collapsed;
        if (!force) return false;
        ProgressTitle.Text = "正在关闭共享速搜";
        await Task.Run(() => InstallerEngine.KillRunning(paths));
        return InstallerEngine.FindRunning(paths).Count == 0;
    }

    /// <summary>要关掉的是哪些位置的共享速搜：已安装的那个，以及这次要装到的位置。</summary>
    private IReadOnlyCollection<string> RunningCandidates()
    {
        var list = new List<string>();
        var installed = InstallerEngine.FindInstalled()?.Directory;
        if (installed is not null) list.Add(System.IO.Path.Combine(installed, InstallerEngine.ExeName));
        if (_uninstallDirectory is not null) list.Add(System.IO.Path.Combine(_uninstallDirectory, InstallerEngine.ExeName));
        if (_mode == SetupMode.Install)
        {
            try { list.Add(System.IO.Path.Combine(System.IO.Path.GetFullPath(LocationBox.Text.Trim().Trim('"')), InstallerEngine.ExeName)); } catch { }
        }
        return list;
    }

    private void RunningForce_Click(object sender, RoutedEventArgs e) => _runningChoice?.TrySetResult(true);
    private void RunningCancel_Click(object sender, RoutedEventArgs e) => _runningChoice?.TrySetResult(false);

    private void CancelBack()
    {
        _busy = false;
        StopShine();
        GoTo(_page - 1);
    }

    // ================= 卸载 =================

    private async Task MeasureDataAsync()
    {
        var (bytes, folders) = await InstallerEngine.MeasureUserDataAsync();
        RemoveDataTitle.Text = folders == 0 ? "同时删除我的设置、索引和缩略图缓存（没有找到）" : $"同时删除我的设置、索引和缩略图缓存（{Size(bytes)}）";
        RemoveDataCheck.IsEnabled = folders > 0;
    }

    private async Task RunUninstallAsync()
    {
        _busy = true;
        UpdateChrome();
        ProgressEyebrow.Text = "正在卸载";
        var removeData = RemoveDataCheck.IsChecked == true;
        var steps = new List<(string, string)> { ("close", "关闭正在运行的共享速搜"), ("startup", "关闭开机启动"), ("link", "删除快捷方式") };
        if (!_registrationOnly) steps.Add(("files", "删除程序文件"));
        steps.Add(("reg", "删除卸载信息"));
        if (removeData) steps.Add(("data", "删除设置、索引和缩略图缓存"));
        BuildSteps(steps.ToArray());
        StartShine();
        try
        {
            SetStep("close", StepState.Running);
            ProgressTitle.Text = "准备卸载";
            if (!await CloseRunningAsync()) { CancelBack(); return; }
            SetStep("close", StepState.Done);
            var directory = _uninstallDirectory;
            if (directory is null && !_registrationOnly) throw new InvalidOperationException("没有找到已安装的共享速搜。");
            await InstallerEngine.UninstallAsync(new UninstallOptions(directory, removeData), new Progress<SetupProgress>(OnProgress));
            foreach (var (key, _) in _steps) SetStep(key, StepState.Done);
            await Task.Delay(260);
            ShowDone(success: true, "已经卸载",
                removeData
                    ? "程序、快捷方式、设置、索引和缩略图缓存都删干净了。谢谢用过共享速搜。"
                    : "程序和快捷方式已删除。你的设置和索引还留在本机，以后重新安装可以直接用；不想留的话再卸载一次时勾选删除即可。");
        }
        catch (Exception ex)
        {
            ShowDone(success: false, "卸载没有完成", "原因：" + ex.Message);
        }
        finally { _busy = false; StopShine(); UpdateChrome(); }
    }

    // ================= 完成 =================

    private void ShowDone(bool success, string title, string lead)
    {
        _finished = true;
        _failed = !success;
        DoneEyebrow.Text = success ? "完成" : "出了点问题";
        DoneEyebrow.SetResourceReference(TextBlock.ForegroundProperty, success ? "AccentBrush" : "DangerBrush");
        DoneTitle.Text = title;
        DoneLead.Text = lead;
        DoneHotkey.Visibility = success && _mode == SetupMode.Install ? Visibility.Visible : Visibility.Collapsed;
        if (DoneHotkey.Visibility == Visibility.Visible) BuildHotkeyCaps();
        DoneCircle.SetResourceReference(Shape.FillProperty, success ? "AccentBrush" : "DangerBrush");
        DoneCheck.Visibility = success ? Visibility.Visible : Visibility.Collapsed;
        DoneError.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        GoTo(_flow.IndexOf(PageDone));

        // 圆先弹出来，再画对勾
        var pop = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(160) };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), Flow.EaseOut));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), Flow.EaseOut));
        DoneCircleScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        DoneCircleScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        DoneCheck.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(360)) { BeginTime = TimeSpan.FromMilliseconds(420), EasingFunction = Flow.EaseOut });
    }

    /// <summary>完成页显示真正的呼出快捷键（升级时可能已经改过）。</summary>
    private void BuildHotkeyCaps()
    {
        var hotkey = "Ctrl+Alt+Space";
        try { hotkey = SettingsStore.Load().GlobalHotkey; } catch { }
        var caps = DoneHotkey.Children.OfType<Border>().ToList();
        foreach (var cap in caps) DoneHotkey.Children.Remove(cap);
        var insertAt = 1;
        foreach (var part in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cap = new Border { MinWidth = part.Length > 3 ? 76 : 38, Child = new TextBlock { Text = part, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center } };
            cap.SetResourceReference(StyleProperty, "DemoKey");
            DoneHotkey.Children.Insert(insertAt++, cap);
        }
    }

    private void Finish()
    {
        if (_mode == SetupMode.Install && !_failed && LaunchCheck.IsChecked == true && _installedExe is not null)
        {
            try { Process.Start(new ProcessStartInfo(_installedExe) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(_installedExe)! }); }
            catch { }
        }
        CloseAnimated();
    }

    // ================= 窗口 =================

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Flow.DragWindow(this, e);

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        CloseAnimated();
    }

    private bool _closing;

    private void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        Flow.PlayClose(Stage, () =>
        {
            Close();
        });
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 安装 / 卸载进行到一半不能关
        if (_busy) { e.Cancel = true; return; }
        _runningChoice?.TrySetResult(false);
        if (_mode == SetupMode.Uninstall) InstallerEngine.ScheduleSelfDelete();
        Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_busy) { CloseAnimated(); e.Handled = true; }
    }

    // ================= 动效细节 =================

    private void StartAmbient()
    {
        // 光晕缓慢漂移：12 秒一个来回
        var drift = new DoubleAnimation(-60, 40, TimeSpan.FromSeconds(12)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        GlowShift.BeginAnimation(TranslateTransform.XProperty, drift);
        GlowShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-20, 50, TimeSpan.FromSeconds(9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        var breathe = new DoubleAnimation(1, 1.06, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        IconPulse.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        IconPulse.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
    }

    private void AnimateBar(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fraction, TimeSpan.FromMilliseconds(240)) { EasingFunction = Flow.EaseOut }, HandoffBehavior.SnapshotAndReplace);
        ProgressPercent.Text = $"{fraction * 100:F0}%";
    }

    private void StartShine()
    {
        AnimateBar(0.02);
        ShineShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-140, 680, TimeSpan.FromMilliseconds(1400)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    private void StopShine()
    {
        ShineShift.BeginAnimation(TranslateTransform.XProperty, null);
        ShineShift.X = -140;
    }

    private static void Shake(UIElement element)
    {
        if (element.RenderTransform is not TranslateTransform { IsFrozen: false } shift)
        {
            shift = new TranslateTransform();
            element.RenderTransform = shift;
        }
        var shake = new DoubleAnimationUsingKeyFrames();
        foreach (var (ms, x) in new[] { (0, 0.0), (60, -8.0), (120, 7.0), (180, -5.0), (240, 3.0), (300, 0.0) })
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        shift.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    // ================= 步骤列表 =================

    private enum StepState { Pending, Running, Done }

    private void BuildSteps(params (string Key, string Text)[] steps)
    {
        ProgressSteps.Children.Clear();
        _steps.Clear();
        foreach (var (key, text) in steps)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var ring = new Ellipse { Width = 16, Height = 16, StrokeThickness = 1.6, HorizontalAlignment = HorizontalAlignment.Left, RenderTransformOrigin = new Point(0.5, 0.5) };
            ring.SetResourceReference(Shape.StrokeProperty, "StrokeStrongBrush");
            var check = new TextBlock { Text = "\uE73E", FontSize = 9, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 0), Opacity = 0 };
            check.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            check.VerticalAlignment = VerticalAlignment.Center;
            var label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            Grid.SetColumn(label, 1);
            row.Children.Add(ring);
            row.Children.Add(check);
            row.Children.Add(label);
            ProgressSteps.Children.Add(row);
            _steps.Add((key, row));
        }
        Flow.Stagger(ProgressSteps.Children.Cast<UIElement>(), startMs: 160, stepMs: 50);
    }

    private void SetStep(string key, StepState state)
    {
        var row = _steps.FirstOrDefault(x => x.Key == key).Row;
        if (row is null || row.Tag is StepState current && current == state) return;
        row.Tag = state;
        var ring = (Ellipse)row.Children[0];
        var check = (TextBlock)row.Children[1];
        var label = (TextBlock)row.Children[2];
        switch (state)
        {
            case StepState.Running:
                // 转圈：一段弧线的圆环旋转
                ring.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                ring.StrokeDashArray = new DoubleCollection { 5, 3 };
                var spin = new RotateTransform();
                ring.RenderTransform = spin;
                spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                break;
            case StepState.Done:
                ring.RenderTransform = Transform.Identity;
                ring.StrokeDashArray = null;
                ring.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                ring.SetResourceReference(Shape.FillProperty, "AccentBrush");
                check.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                break;
        }
    }

    private static string Mb(long bytes) => $"{Math.Max(1, bytes / 1024 / 1024):N0} MB";

    private static string Size(long bytes) => bytes switch
    {
        < 1024L * 1024 => $"{Math.Max(1, bytes / 1024):N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
    };
}
