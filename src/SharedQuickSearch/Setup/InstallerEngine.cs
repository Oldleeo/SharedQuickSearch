using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace SharedQuickSearch.Setup;

public sealed record InstalledInfo(string Directory, string Version);
public sealed record InstallOptions(string Directory, bool DesktopShortcut, bool StartMenuShortcut);
public sealed record UninstallOptions(string? Directory, bool RemoveUserData);
public sealed record SetupProgress(double Fraction, string Message);

/// <summary>
/// 安装 / 升级 / 卸载。
/// 原则：
/// - 只装到当前用户（默认 %LocalAppData%\Programs\共享速搜），不需要管理员权限，不写 HKLM。
/// - 安装包就是程序本身：把自己复制成 共享速搜.exe，不额外解压一堆文件。
/// - 卸载只删我们自己的文件、快捷方式和注册表项；安装目录里有别的文件就保留目录。
/// - 全程不访问共享盘。
/// </summary>
public static class InstallerEngine
{
    public const string ExeName = "共享速搜.exe";
    public const string ProductName = "共享速搜";
    public const string Publisher = "老李Oldlee";
    public const string ExitEventName = "Local\\Oldlee.SharedQuickSearch.Exit";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Oldlee.SharedQuickSearch";

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);

    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName + ".lnk");
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk");

    /// <summary>本机用户数据：设置、索引（Roaming）和缩略图缓存（Local）。</summary>
    public static IReadOnlyList<string> UserDataFolders => new[]
    {
        SettingsStore.DataFolder,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oldlee", ProductName)
    };

    public static long PackageSize
    {
        get { try { return new FileInfo(Environment.ProcessPath!).Length; } catch { return 80L * 1024 * 1024; } }
    }

    // ================= 已安装信息 =================

    public static InstalledInfo? FindInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, false);
            var directory = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, ExeName))) return null;
            return new InstalledInfo(directory, key?.GetValue("DisplayVersion") as string ?? "");
        }
        catch { return null; }
    }

    /// <summary>比较版本号：a 比 b 新返回正数。</summary>
    public static int CompareVersions(string a, string b) => UpdateRules.Compare(a, b);

    /// <summary>用户选的文件夹不叫“共享速搜”时，在里面建一个“共享速搜”子目录，避免和别的软件混在一起。</summary>
    public static string NormalizeTarget(string chosen)
    {
        var path = Path.GetFullPath(chosen.Trim().Trim('"'));
        return Path.GetFileName(path.TrimEnd('\\', '/')).Equals(ProductName, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.Combine(path, ProductName);
    }

    public static long FreeSpace(string directory)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    // ================= 正在运行的程序 =================

    /// <summary>
    /// 当前登录会话里、从这些位置启动的共享速搜进程。
    /// 不碰别的用户会话里的进程，也不碰放在别处的便携副本（读不到路径时按同名同会话算）。
    /// </summary>
    public static IReadOnlyList<Process> FindRunning(IEnumerable<string> exePaths)
    {
        var wanted = exePaths.Where(x => !string.IsNullOrEmpty(x)).Select(x => Path.GetFullPath(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var session = Process.GetCurrentProcess().SessionId;
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try
            {
                if (process.Id == Environment.ProcessId || process.SessionId != session) continue;
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                if (path is null || wanted.Count == 0 || wanted.Contains(Path.GetFullPath(path))) result.Add(process);
            }
            catch { }
        }
        return result;
    }

    /// <summary>请正在运行的共享速搜自己退出（1.8 起支持；更早的版本不响应，由界面询问是否强制关闭）。</summary>
    public static async Task<bool> RequestExitAsync(IReadOnlyCollection<string> exePaths, TimeSpan timeout)
    {
        if (FindRunning(exePaths).Count == 0) return true;
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ExitEventName);
            signal.Set();
        }
        catch { }
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            if (FindRunning(exePaths).Count == 0) return true;
        }
        return false;
    }

    public static void KillRunning(IReadOnlyCollection<string> exePaths)
    {
        foreach (var process in FindRunning(exePaths))
        {
            try { process.Kill(); process.WaitForExit(4000); } catch { }
        }
    }

    // ================= 安装 =================

    public static async Task<string> InstallAsync(InstallOptions options, IProgress<SetupProgress> progress, CancellationToken cancellationToken)
    {
        var previous = FindInstalled();
        var directory = options.Directory;
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, ExeName);
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("找不到安装包自身的路径。");

        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            // 先复制成 .new，完整写完再替换：中途失败不会留下半个程序
            var staging = target + ".new";
            await CopyWithProgressAsync(source, staging, fraction => progress.Report(new SetupProgress(0.05 + fraction * 0.75, "复制程序文件")), cancellationToken);
            CleanupOldCopies(directory);
            await SwapInAsync(staging, target, cancellationToken);
        }

        progress.Report(new SetupProgress(0.85, "创建快捷方式"));
        await Task.Run(() =>
        {
            UpdateLink(StartMenuLink, options.StartMenuShortcut, target, directory);
            UpdateLink(DesktopLink, options.DesktopShortcut, target, directory);
        }, cancellationToken);

        progress.Report(new SetupProgress(0.93, "写入卸载信息"));
        WriteUninstallEntry(directory, target);
        // 之前开了开机启动：路径改到新位置
        if (StartupManager.IsEnabled()) StartupManager.SetEnabled(true, target);

        // 换了安装位置：清掉旧位置的程序文件（只删我们自己的文件）
        if (previous is not null && !Path.GetFullPath(previous.Directory).Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            RemoveProgramFiles(previous.Directory);

        progress.Report(new SetupProgress(1, "完成"));
        return target;
    }

    /// <summary>
    /// 把 .new 换成正式程序，任何一步失败都把旧程序放回去：绝不留下一个没有 共享速搜.exe 的安装目录。
    /// 杀毒软件常会在新写入的 exe 上短暂加锁，所以每一步都重试几次。
    /// </summary>
    private static async Task SwapInAsync(string staging, string target, CancellationToken cancellationToken)
    {
        string? old = null;
        if (File.Exists(target))
        {
            // 旧程序如果还没完全退出，删除会失败，但改名可以；用不重复的名字，避免上次留下的 .old 还被占用
            old = $"{target}.old-{Guid.NewGuid():N}";
            await RetryAsync(() => File.Move(target, old), cancellationToken);
        }
        try
        {
            await RetryAsync(() => File.Move(staging, target, overwrite: false), cancellationToken);
        }
        catch
        {
            if (old is not null && !File.Exists(target))
                try { File.Move(old, target); } catch { /* 放不回去：.old 文件还在，手动改名就能用 */ }
            throw;
        }
        if (old is not null) try { File.Delete(old); } catch { /* 下次安装再清 */ }
    }

    private static async Task RetryAsync(Action action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (attempt < 6 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(300 * attempt, CancellationToken.None);
            }
        }
    }

    private static void UpdateLink(string link, bool wanted, string target, string directory)
    {
        if (wanted)
        {
            ShellLink.Create(link, target, directory, "共享盘素材极速搜索");
            return;
        }
        if (IsOurLink(link)) try { File.Delete(link); } catch { }
    }

    /// <summary>这个快捷方式存在、而且指向共享速搜（自动更新时保持用户原来的快捷方式选择）。</summary>
    public static bool HasOurLink(string link) => File.Exists(link) && IsOurLink(link);

    /// <summary>Capture only this application's registration, shortcuts and startup entry for transactional updates.</summary>
    public static Action CaptureUpdateMetadata()
    {
        var registration = new Dictionary<string, (object Value, RegistryValueKind Kind)>();
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath))
            if (key is not null)
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value)
                        registration[name] = (value, key.GetValueKind(name));
        var links = new[] { DesktopLink, StartMenuLink }.ToDictionary(x => x, x => File.Exists(x) ? File.ReadAllBytes(x) : null);
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        object? startup;
        RegistryValueKind startupKind = RegistryValueKind.String;
        using (var key = Registry.CurrentUser.OpenSubKey(runPath))
        {
            startup = key?.GetValue(ProductName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (startup is not null) startupKind = key!.GetValueKind(ProductName);
        }
        return () =>
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, true))
            {
                foreach (var name in key.GetValueNames()) if (!registration.ContainsKey(name)) key.DeleteValue(name, false);
                foreach (var (name, value) in registration) key.SetValue(name, value.Value, value.Kind);
            }
            foreach (var (file, bytes) in links)
            {
                if (bytes is not null) File.WriteAllBytes(file, bytes);
                else if (IsOurLink(file)) File.Delete(file);
            }
            using var run = Registry.CurrentUser.CreateSubKey(runPath, true);
            if (startup is null) run.DeleteValue(ProductName, false);
            else run.SetValue(ProductName, startup, startupKind);
        };
    }

    private static bool IsOurLink(string link)
    {
        var pointsTo = ShellLink.GetTarget(link);
        return pointsTo is not null && Path.GetFileName(pointsTo).Equals(ExeName, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyWithProgressAsync(string source, string destination, Action<double> report, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, useAsync: true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        var total = Math.Max(1, input.Length);
        var buffer = new byte[bufferSize];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            report((double)copied / total);
        }
        await output.FlushAsync(cancellationToken);
        if (output.Length != input.Length) throw new IOException("复制的文件大小不一致，磁盘可能已满。");
    }

    private static void WriteUninstallEntry(string directory, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, true);
        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", $"\"{exe}\",0");
        key.SetValue("InstallLocation", directory);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Max(1, PackageSize / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // ================= 卸载 =================

    /// <summary>
    /// 从“设置 → 应用”点卸载时，运行的是安装目录里的程序本身，它不能删除自己。
    /// 所以先把自己复制到临时目录，由那个副本来卸载，结束后副本再自删。
    /// </summary>
    public static bool RelaunchFromTemp(string installDirectory)
    {
        try
        {
            // 固定名字：.NET 单文件程序会按程序名把运行库解压到 %TEMP%\.net\<名字>，名字固定才能在结束时一起清掉
            var copy = Path.Combine(Path.GetTempPath(), UninstallerName + ".exe");
            try { File.Copy(Environment.ProcessPath!, copy, overwrite: true); }
            catch (IOException)
            {
                copy = Path.Combine(Path.GetTempPath(), $"{UninstallerName}-{Guid.NewGuid():N}"[..(UninstallerName.Length + 9)] + ".exe");
                File.Copy(Environment.ProcessPath!, copy, overwrite: true);
            }
            Process.Start(new ProcessStartInfo(copy) { UseShellExecute = false, ArgumentList = { "--uninstall", "--target", installDirectory } });
            return true;
        }
        catch { return false; }
    }

    public static async Task<(long Bytes, int Folders)> MeasureUserDataAsync() => await Task.Run(() =>
    {
        long total = 0;
        var folders = 0;
        foreach (var folder in UserDataFolders)
        {
            if (!Directory.Exists(folder)) continue;
            folders++;
            try { total += new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch { return 0L; } }); }
            catch { }
        }
        return (total, folders);
    });

    private const string UninstallerName = "共享速搜-卸载";

    /// <summary>
    /// 可以卸载的目录：只认注册表里登记的安装位置；传进来的 --target 必须和它一致。
    /// 网络路径、网络盘一律拒绝。返回 null 表示没有可卸载的安装。
    /// </summary>
    public static string? SafeUninstallTarget(string? requested)
    {
        try
        {
            var installed = FindInstalled()?.Directory;
            if (installed is null) return null;
            var full = Path.GetFullPath(installed);
            if (requested is not null && !Path.GetFullPath(requested).TrimEnd('\\').Equals(full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            return IsLocalPath(full) ? full : null;
        }
        catch { return null; } // 参数格式不对：当作没有可卸载的安装
    }

    /// <summary>注册表里有安装记录（不管程序文件还在不在）。</summary>
    public static bool HasRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, false);
            return key is not null;
        }
        catch { return false; }
    }

    /// <summary>本机磁盘（不是 \\服务器 路径，也不是映射的网络盘）。</summary>
    public static bool IsLocalPath(string path)
    {
        try
        {
            if (path.StartsWith(@"\\")) return false;
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).DriveType != DriveType.Network;
        }
        catch { return false; }
    }

    private static void CleanupOldCopies(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, ExeName + ".old*"))
                try { File.Delete(file); } catch { }
        }
        catch { }
    }

    public static async Task UninstallAsync(UninstallOptions options, IProgress<SetupProgress> progress)
    {
        var directory = options.Directory;
        // directory 为 null：只清登记（快捷方式、开机启动、卸载信息），不删任何程序文件
        if (directory is not null && !IsLocalPath(directory)) throw new InvalidOperationException("安装目录在网络位置上，为了安全不做删除。");
        progress.Report(new SetupProgress(0.1, "关闭开机启动"));
        StartupManager.SetEnabled(false);

        progress.Report(new SetupProgress(0.25, "删除快捷方式"));
        await Task.Run(() =>
        {
            foreach (var link in new[] { StartMenuLink, DesktopLink })
                if (IsOurLink(link)) try { File.Delete(link); } catch { }
        });

        progress.Report(new SetupProgress(0.45, "删除程序文件"));
        if (directory is not null) await Task.Run(() => RemoveProgramFiles(directory));

        progress.Report(new SetupProgress(0.65, "删除卸载信息"));
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false); } catch { }
        // 程序运行时 .NET 解压到临时目录的运行库
        try
        {
            var extracted = Path.Combine(Path.GetTempPath(), ".net", Path.GetFileNameWithoutExtension(ExeName));
            if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        }
        catch { }

        if (options.RemoveUserData)
        {
            progress.Report(new SetupProgress(0.8, "删除设置、索引和缩略图缓存"));
            await Task.Run(() =>
            {
                foreach (var folder in UserDataFolders)
                {
                    try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { }
                    // 上一级 Oldlee 目录空了也删掉
                    try
                    {
                        var parent = Path.GetDirectoryName(folder);
                        if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                    }
                    catch { }
                }
            });
        }
        progress.Report(new SetupProgress(1, "完成"));
    }

    /// <summary>只删我们自己的文件；目录叫“共享速搜”且已经空了才删目录。</summary>
    private static void RemoveProgramFiles(string directory)
    {
        if (!Directory.Exists(directory) || !IsLocalPath(directory)) return;
        CleanupOldCopies(directory);
        foreach (var name in new[] { ExeName, ExeName + ".new" })
        {
            var path = Path.Combine(directory, name);
            for (var attempt = 0; attempt < 10 && File.Exists(path); attempt++)
            {
                try { File.Delete(path); }
                catch { Thread.Sleep(300); } // 刚关掉的进程可能还没完全释放文件
            }
        }
        try
        {
            if (Path.GetFileName(directory.TrimEnd('\\', '/')).Equals(ProductName, StringComparison.OrdinalIgnoreCase)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch { }
    }

    /// <summary>卸载用的临时副本退出后把自己删掉。</summary>
    public static void ScheduleSelfDelete()
    {
        var self = Environment.ProcessPath;
        if (self is null || !Path.GetFullPath(self).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var extracted = Path.Combine(Path.GetTempPath(), ".net", Path.GetFileNameWithoutExtension(self));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{self}\" & rd /s /q \"{extracted}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }
}
