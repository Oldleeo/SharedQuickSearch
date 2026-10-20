using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Animation;

namespace SharedQuickSearch.Setup;

/// <summary>
/// 自动更新的执行者：由旧版本下载好的新安装包以 --apply-update 启动。
/// 等旧版本退出 → 覆盖安装到原来的位置（快捷方式、开机启动保持原样）→ 打开新版本 → 自己退出。
/// 只写本机的安装目录；原来是从共享盘直接运行的，装到本机默认位置，不碰共享盘上的那份。
/// </summary>
public partial class UpdatingWindow : Window
{
    private readonly int? _waitPid;
    private readonly string _fromVersion;
    private readonly bool _show;

    public UpdatingWindow(int? waitPid, string fromVersion, bool show)
    {
        InitializeComponent();
        _waitPid = waitPid;
        _fromVersion = string.IsNullOrWhiteSpace(fromVersion) ? "旧版本" : fromVersion;
        _show = show;
        VersionText.Text = $"{_fromVersion} → {InstallerEngine.Version}";
        Loaded += async (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 4;
            Flow.PlayOpen(Stage);
            await RunAsync();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this, ThemeManager.IsDarkActive);
    }

    private async Task RunAsync()
    {
        string? target = null;
        UpdateFileTransaction? transaction = null;
        Action? restoreMetadata = null;
        Process? candidate = null;
        string? healthPath = null;
        try
        {
            SetProgress(0.04, "等待旧版本退出");
            await WaitForOldProcessAsync();

            var installed = InstallerEngine.FindInstalled();
            var directory = installed?.Directory;
            // 安装位置只认本机磁盘；没装过、或者登记的位置不在本机，就装到默认位置
            if (directory is null || !InstallerEngine.IsLocalPath(directory)) directory = InstallerEngine.DefaultDirectory;
            target = Path.Combine(directory, InstallerEngine.ExeName);

            var running = new[] { target };
            if (!await InstallerEngine.RequestExitAsync(running, TimeSpan.FromSeconds(15)))
                throw new IOException("旧程序仍在运行，这次不强制打断。");

            restoreMetadata = InstallerEngine.CaptureUpdateMetadata();
            transaction = new UpdateFileTransaction(target);

            var options = new InstallOptions(
                directory,
                DesktopShortcut: installed is not null && InstallerEngine.HasOurLink(InstallerEngine.DesktopLink),
                StartMenuShortcut: installed is null || InstallerEngine.HasOurLink(InstallerEngine.StartMenuLink));
            var progress = new Progress<SetupProgress>(value => SetProgress(0.1 + value.Fraction * 0.85, value.Message));
            target = await InstallerEngine.InstallAsync(options, progress, CancellationToken.None);

            SetProgress(0.97, "正在确认新版本可以启动");
            var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = directory };
            start.ArgumentList.Add("--updated");
            start.ArgumentList.Add(_fromVersion);
            if (_show) start.ArgumentList.Add("--show");
            var token = Guid.NewGuid().ToString("N");
            healthPath = Path.Combine(UpdateService.UpdatesFolder, $"health-{token}.json");
            start.ArgumentList.Add("--update-health-path");
            start.ArgumentList.Add(healthPath);
            start.ArgumentList.Add("--update-health-token");
            start.ArgumentList.Add(token);
            candidate = Process.Start(start) ?? throw new IOException("新版本没有启动。");
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            while (!UpdateHealth.Matches(healthPath, token, InstallerEngine.Version, candidate.Id))
            {
                if (candidate.HasExited) throw new IOException("新版本启动后提前退出，正在回退。");
                if (DateTime.UtcNow >= deadline) throw new IOException("新版本没有完成启动确认，正在回退。");
                await Task.Delay(250);
            }
            await Task.Delay(2500);
            if (candidate.HasExited) throw new IOException("新版本启动后异常退出，正在回退。");
            transaction.Commit();
            transaction = null;
            restoreMetadata = null;
            candidate.Dispose();
            candidate = null;
            UpdateService.MarkUpdateHealthy();
            SetProgress(1, "新版本已启动");
            TitleText.Text = $"已更新到 {InstallerEngine.Version}";
            Flow.PlayClose(Stage, () => Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            // Stop only the candidate this updater started, then restore the exact previous executable and metadata.
            try
            {
                if (candidate is { HasExited: false }) { candidate.Kill(); await candidate.WaitForExitAsync(); }
                transaction?.Rollback();
                restoreMetadata?.Invoke();
                transaction = null;
            }
            catch (Exception recovery)
            {
                Log(recovery);
                TitleText.Text = "更新失败，自动恢复未完成";
                StatusText.Text = "旧程序备份保留在安装目录的 .rollback 文件中，请勿删除。";
                CloseButton.Visibility = Visibility.Visible;
                return;
            }
            Log(ex);
            TitleText.Text = "这次更新没有完成";
            StatusText.Text = "旧版本照常可用，稍后会自动再试。" + Short(ex);
            CloseButton.Visibility = Visibility.Visible;
            // 把旧版本重新打开（如果它还在），不让用户突然没了软件
            try
            {
                if (target is not null && File.Exists(target) && InstallerEngine.FindRunning(new[] { target }).Count == 0)
                    // --update-failed：这次启动不再马上重装，等下一次定时检查（同一版本 3 次失败后停 24 小时）
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, ArgumentList = { "--startup", "--update-failed" } });
            }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (IsVisible) Flow.PlayClose(Stage, () => Application.Current.Shutdown());
        }
        finally
        {
            candidate?.Dispose();
            try { if (healthPath is not null) File.Delete(healthPath); } catch { }
        }
    }

    private async Task WaitForOldProcessAsync()
    {
        if (_waitPid is not { } pid) return;
        try
        {
            using var old = Process.GetProcessById(pid);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await old.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { } // 已经退出了
        catch (OperationCanceledException) { } // 后面会再请它退出，实在不退就结束进程
        catch (InvalidOperationException) { }
    }

    private void SetProgress(double fraction, string message)
    {
        StatusText.Text = message;
        var width = Math.Max(0, (Bar.Parent as FrameworkElement)?.ActualWidth ?? 0) * Math.Clamp(fraction, 0, 1);
        Bar.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(260)) { EasingFunction = Flow.EaseOut });
    }

    private static string Short(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "（没有写入安装目录的权限）",
        IOException io when io.HResult == unchecked((int)0x80070070) => "（磁盘空间不够）",
        IOException => "（安装目录里的文件被占用）",
        _ => ""
    };

    private static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataFolder);
            File.AppendAllText(Path.Combine(SettingsStore.DataFolder, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 自动更新失败：{ex}\n\n");
        }
        catch { }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Flow.PlayClose(Stage, () => Application.Current.Shutdown());
}
