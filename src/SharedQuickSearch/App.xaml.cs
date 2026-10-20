using System.Windows;

namespace SharedQuickSearch;

public partial class App : Application
{
    private SingleInstanceManager? _singleInstance;
    private bool _errorShown;
    private int _errorCount;

    // 必须在任何动画对象创建之前：让动画按显示器的实际刷新率（120/144/165Hz）出帧
    static App() => FrameRate.Configure();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 常驻后台的工具不能因为一个意外异常整个退出：记下来，提示一次，继续运行
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                // 同一个错误反复出现（比如每次排版都抛）时只记前 20 条，日志最多 1 MB
                if (++_errorCount <= 20)
                {
                    System.IO.Directory.CreateDirectory(SettingsStore.DataFolder);
                    var log = System.IO.Path.Combine(SettingsStore.DataFolder, "error.log");
                    if (System.IO.File.Exists(log) && new System.IO.FileInfo(log).Length > 1024 * 1024) System.IO.File.Delete(log);
                    System.IO.File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}\n\n");
                }
            }
            catch { }
            if (_errorShown) return;
            _errorShown = true;
            try { ModernDialog.Show(MainWindow, "出了一个意外错误，软件会继续运行。\n详细信息已写入 %AppData%\\Oldlee\\共享速搜\\error.log。\n\n" + args.Exception.Message, "共享速搜", MessageBoxImage.Warning); } catch { }
        };
        // 自动更新：旧版本下载好的新安装包以 --apply-update 启动（必须在“文件名带 setup”判断之前）
        if (HasArg(e.Args, "--apply-update"))
        {
            try { ThemeManager.Apply(SettingsStore.Load().Theme); } catch { ThemeManager.Apply(ThemeManager.FollowSystem); }
            var updater = new Setup.UpdatingWindow(WaitPid(e.Args), ArgValue(e.Args, "--from") ?? "", HasArg(e.Args, "--show"));
            MainWindow = updater;
            updater.Show();
            return;
        }

        // 安装 / 卸载：同一个程序，按启动参数和文件名决定
        if (TryRunSetup(e.Args)) return;

        // 刚被更新程序（或旧的便携版）换过来：等上一个进程完全退出，再抢单实例
        if (WaitPid(e.Args) is { } previous)
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(previous); old.WaitForExit(15000); } catch { }
        }

        _singleInstance = new SingleInstanceManager();
        if (!_singleInstance.IsPrimary) { _singleInstance.SignalShow(); Shutdown(); return; }

        var settings = SettingsStore.Load();
        ThemeManager.Apply(settings.Theme);

        var updatedFrom = ArgValue(e.Args, "--updated");
        // 开机启动、或者后台自动更新完：都不弹窗（更新前用户正在看的话会带 --show）
        var startup = HasArg(e.Args, "--startup") || (updatedFrom is not null && !HasArg(e.Args, "--show"));

        // 上次已经下载、校验好的新版本：直接装，不等下一次检查（刚更新完的这次除外）
        if (updatedFrom is null && !HasArg(e.Args, "--update-failed") && UpdateService.TryApplyPendingAtStartup((_, path) => StartUpdater(path, show: !startup)))
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }
        var window = new MainWindow(settings);
        MainWindow = window;
        _singleInstance.ShowRequested += () => Dispatcher.Invoke(window.Summon);
        _singleInstance.ExitRequested += () => Dispatcher.Invoke(window.ExitApp);

        // 升级上来的老用户已经配好目录：不再弹首次引导
        if (!settings.OnboardingDone && settings.IndexRoots.Count > 0)
        {
            settings.OnboardingDone = true;
            try { SettingsStore.Save(settings); } catch { }
        }

        // 主窗口只做后台宿主（托盘、快捷键、索引），界面统一是搜索条 / 工作台。
        // 开机启动时不弹窗：只创建窗口句柄，托盘、快捷键、索引照常初始化。
        window.InitializeHidden();
        // Report only after core startup and an idle dispatcher turn; an exception or early exit cannot acknowledge.
        var healthPath = ArgValue(e.Args, "--update-health-path");
        var healthToken = ArgValue(e.Args, "--update-health-token");
        if (healthPath is not null && healthToken is not null)
            Dispatcher.InvokeAsync(() =>
            {
                if (!_errorShown && _singleInstance?.IsPrimary == true)
                    UpdateHealth.Acknowledge(healthPath, healthToken, UpdateService.UpdatesFolder, UpdateService.CurrentVersion, Environment.ProcessId);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (updatedFrom is not null) window.NotifyUpdated(updatedFrom);
        if (startup) return;
        if (!settings.OnboardingDone) window.ShowOnboarding();
        else window.Summon();
    }

    /// <summary>
    /// --uninstall：卸载（从安装目录启动时先复制到临时目录再运行，因为程序不能删除自己）。
    /// --install，或文件名里带“安装 / setup”：安装界面。
    /// </summary>
    private bool TryRunSetup(string[] args)
    {
        var uninstall = args.Any(x => x.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        var fileName = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        var install = args.Any(x => x.Equals("--install", StringComparison.OrdinalIgnoreCase))
            || fileName.Contains("安装") || fileName.Contains("setup", StringComparison.OrdinalIgnoreCase);
        if (!uninstall && !install) return false;

        try { ThemeManager.Apply(SettingsStore.Load().Theme); } catch { ThemeManager.Apply(ThemeManager.FollowSystem); }

        if (uninstall)
        {
            try { return RunUninstall(args); }
            catch
            {
                // 任何意外都不能留下一个看不见的进程（显式退出模式下不会自己结束）
                Shutdown();
                return true;
            }
        }

        var setup = new Setup.SetupWindow(Setup.SetupMode.Install);
        MainWindow = setup;
        setup.Show();
        return true;
    }

    private bool RunUninstall(string[] args)
    {
        {
            // 只卸载注册表里登记的安装目录，绝不按“程序当前所在的文件夹”去删
            // （否则在共享盘或下载文件夹里运行 --uninstall 会删掉那里的文件）
            var targetIndex = Array.FindIndex(args, x => x.Equals("--target", StringComparison.OrdinalIgnoreCase));
            var requested = targetIndex >= 0 && targetIndex + 1 < args.Length ? args[targetIndex + 1] : null;
            var target = Setup.InstallerEngine.SafeUninstallTarget(requested);
            if (requested is null && target is not null)
            {
                var self = System.IO.Path.GetFullPath(Environment.ProcessPath ?? "");
                if (target is not null && self.StartsWith(System.IO.Path.GetFullPath(target).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    && Setup.InstallerEngine.RelaunchFromTemp(target))
                {
                    Shutdown();
                    return true;
                }
            }
            var window = new Setup.SetupWindow(Setup.SetupMode.Uninstall, target);
            MainWindow = window;
            window.Show();
            return true;
        }
    }

    private static bool HasArg(string[] args, string name) => args.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--") ? args[index + 1] : index >= 0 ? "" : null;
    }

    private static int? WaitPid(string[] args) => int.TryParse(ArgValue(args, "--wait-pid"), out var pid) ? pid : null;

    /// <summary>
    /// 启动更新：installerOrExe 是下载好的新安装包（--apply-update 覆盖安装），
    /// 或者是电脑上已经装好的新版本（当前运行的是旧的便携版，直接换过去）。调用方随后退出。
    /// </summary>
    public static void StartUpdater(string installerOrExe, bool show)
    {
        var start = new System.Diagnostics.ProcessStartInfo(installerOrExe) { UseShellExecute = false };
        var isInstaller = installerOrExe.StartsWith(UpdateService.UpdatesFolder, StringComparison.OrdinalIgnoreCase);
        if (isInstaller) start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add("--wait-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        if (isInstaller)
        {
            start.ArgumentList.Add("--from");
            start.ArgumentList.Add(UpdateService.CurrentVersion);
            if (show) start.ArgumentList.Add("--show");
        }
        else if (!show) start.ArgumentList.Add("--startup");
        start.WorkingDirectory = System.IO.Path.GetDirectoryName(installerOrExe) ?? "";
        System.Diagnostics.Process.Start(start);
    }

    protected override void OnExit(ExitEventArgs e) { _singleInstance?.Dispose(); base.OnExit(e); }
}
