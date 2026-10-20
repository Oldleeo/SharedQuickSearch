using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using SharedQuickSearch.Setup;

namespace SharedQuickSearch;

public enum UpdateStage { Idle, Checking, UpToDate, Downloading, Ready, Failed }

/// <summary>
/// 强制自动更新：有新版本就在后台下载、校验，等用户没在用的时候自动装上并重新打开。
///
/// 安全上的几条底线：
/// - 只认 https 地址，或者本机 / 共享盘上的文件（只读）。不接受 http。
/// - 下载完必须对上 update.json 里的 SHA-256 和大小；安装包自己的版本号也必须和说明一致，
///   对不上就丢掉（防止版本号写错导致每次启动都“更新”一遍）。
/// - 同一个版本连续装失败 3 次，24 小时内不再尝试。
/// - 只往本机写：下载放在 %LocalAppData%\Oldlee\共享速搜\updates，安装到本机的安装目录。
///   从共享盘上直接运行的便携版，更新时装到本机，不会改写共享盘上的那份。
/// - 联网只取更新说明、其签名和安装包，不带素材文件名、路径或搜索内容。
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>默认更新源：GitHub Releases 上最新版本的 update.json（不走 API，没有次数限制）。</summary>
    public const string DefaultFeed = "https://github.com/oldleeo/SharedQuickSearch/releases/latest/download/update.json";
    private const long MaxPackageBytes = UpdateRules.MaxPackageBytes;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(3);

    public static UpdateService? Instance { get; private set; }

    public static string UpdatesFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oldlee", "共享速搜", "updates");
    private static string StatePath => Path.Combine(UpdatesFolder, "state.json");

    private readonly Func<string?> _feed;
    private readonly Func<bool> _canApplyNow;
    private readonly Action<UpdateManifest, string> _apply;
    private readonly DispatcherTimer _checkTimer;
    private readonly DispatcherTimer _applyTimer;
    private readonly HttpClient _http;
    private CancellationTokenSource? _work;
    private string? _readyPath;
    private byte[]? _signedBytes;
    private string? _signature;
    private bool _applying;

    public UpdateStage Stage { get; private set; } = UpdateStage.Idle;
    public string StatusText { get; private set; } = "等待第一次检查";
    public UpdateManifest? Latest { get; private set; }
    public DateTime? LastChecked { get; private set; }
    public double DownloadProgress { get; private set; }
    public event Action? Changed;

    /// <summary>当前程序的版本：和校验安装包时一样读文件版本，两边口径一致，不会因为对不上而反复更新。</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    private static string ReadCurrentVersion()
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "");
            if (info.FileMajorPart + info.FileMinorPart + info.FileBuildPart > 0) return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        catch { }
        return InstallerEngine.Version;
    }

    /// <param name="feed">设置里填的更新源；空 = <see cref="DefaultFeed"/>。</param>
    /// <param name="canApplyNow">现在能不能打断用户（搜索条收起、没在拖文件、没有对话框）。</param>
    /// <param name="apply">启动更新程序并退出当前程序（参数：版本、安装包路径）。</param>
    public UpdateService(Func<string?> feed, Func<bool> canApplyNow, Action<UpdateManifest, string> apply)
    {
        _feed = feed;
        _canApplyNow = canApplyNow;
        _apply = apply;
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"SharedQuickSearch/{CurrentVersion}");
        _checkTimer = new DispatcherTimer { Interval = CheckInterval };
        _checkTimer.Tick += async (_, _) => await CheckAsync(manual: false);
        _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _applyTimer.Tick += (_, _) => TryApplyWhenIdle();
        Instance = this;
    }

    /// <summary>启动后 20–110 秒第一次检查（错峰：全组同时开机不会同时下载），之后每 3 小时一次。</summary>
    public void Start()
    {
        CleanupOldPackages();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20 + Random.Shared.Next(0, 90)) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            await CheckAsync(manual: false);
            _checkTimer.Start();
        };
        first.Start();
    }

    // ================= 检查 + 下载 =================

    /// <summary>检查更新；有新版本就接着下载。返回是否有可用的新版本。</summary>
    public async Task<bool> CheckAsync(bool manual, string? feedOverride = null)
    {
        if (_applying || Stage is UpdateStage.Checking or UpdateStage.Downloading) return Stage == UpdateStage.Downloading;
        if (Stage == UpdateStage.Ready && _readyPath is not null && !manual) return true;
        var cancellation = _work = new CancellationTokenSource();
        try
        {
            SetStage(UpdateStage.Checking, "正在检查更新…");
            var manifest = await FetchManifestAsync(feedOverride ?? _feed(), cancellation.Token);
            LastChecked = DateTime.Now;
            Latest = manifest;
            if (UpdateRules.Compare(manifest.Version, CurrentVersion) <= 0)
            {
                SetStage(UpdateStage.UpToDate, $"已是最新版本 · {DateTime.Now:HH:mm} 检查");
                return false;
            }

            // 这台电脑上其实已经装了新版本（当前运行的是旧的便携版）：不重复下载，直接换过去
            if (InstalledCopyIsAtLeast(manifest.Version) is { } installedExe)
            {
                _readyPath = installedExe;
                SetStage(UpdateStage.Ready, $"电脑上已经装了 {manifest.Version}，下次空闲时切换过去");
                _applyTimer.Start();
                return true;
            }

            if (Blocked(manifest.Version))
            {
                SetStage(UpdateStage.Failed, $"{manifest.Version} 连续安装失败，24 小时后再试");
                return false;
            }
            // 这个安装包校验没通过过：同一个包不再反复下载，等发布者重新发布（哈希会变）
            if (LoadState().PackageFailures.GetValueOrDefault(manifest.Sha256) >= BadPackageThreshold)
            {
                SetStage(UpdateStage.Failed, $"{manifest.Version} 的安装包校验没通过，等重新发布");
                return false;
            }

            var path = await DownloadAsync(manifest, cancellation.Token);
            _readyPath = path;
            SetStage(UpdateStage.Ready, $"新版本 {manifest.Version} 已下载，没在用的时候自动安装");
            _applyTimer.Start();
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStage(UpdateStage.Idle, "已取消");
            return false;
        }
        catch (Exception ex)
        {
            LastChecked = DateTime.Now;
            SetStage(UpdateStage.Failed, Friendly(ex));
            return false;
        }
        finally
        {
            if (ReferenceEquals(_work, cancellation)) _work = null;
        }
    }

    private async Task<UpdateManifest> FetchManifestAsync(string? configured, CancellationToken cancellationToken)
    {
        // 没有公钥就不更新：宁可停在旧版本，也不装一个验不了来源的程序
        if (string.IsNullOrWhiteSpace(UpdateKey.PublicKeyPem)) throw new UpdateException("这个版本没有配置更新签名，不会自动更新");
        var feed = string.IsNullOrWhiteSpace(configured) ? DefaultFeed : configured.Trim().Trim('"');
        byte[] manifestBytes;
        string signature;
        var fromFile = !IsHttps(feed);
        if (!fromFile)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            manifestBytes = await GetBytesAsync(feed, timeout.Token, notFound: "更新源上还没有发布版本");
            signature = System.Text.Encoding.ASCII.GetString(await GetBytesAsync(SignatureLocation(feed), timeout.Token, notFound: "更新源上没有签名文件（update.json.sig）"));
        }
        else if (IsHttp(feed)) throw new UpdateException("更新源必须是 https 地址或文件路径，不接受 http");
        else
        {
            manifestBytes = await ReadLocalAsync(feed, cancellationToken);
            signature = System.Text.Encoding.ASCII.GetString(await ReadLocalAsync(feed + ".sig", cancellationToken));
        }

        // 先验签名，再看内容：签名对的是文件的原始字节
        var manifest = UpdateRules.ReadSignedManifest(manifestBytes, signature, UpdateKey.PublicKeyPem, fromFile);
        _signedBytes = manifestBytes;
        _signature = signature;
        return manifest;
    }

    /// <summary>update.json 旁边的 update.json.sig（https 地址要把 .sig 加在查询参数前面）。</summary>
    private static string SignatureLocation(string feed)
    {
        var query = feed.IndexOf('?');
        return query < 0 ? feed + ".sig" : feed[..query] + ".sig" + feed[query..];
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken, string notFound)
    {
        // 加时间戳绕过代理缓存：不然公司代理可能一直给旧的 update.json
        var stamped = url + (url.Contains('?') ? "&" : "?") + "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var response = await _http.GetAsync(stamped, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new UpdateException(notFound);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 64 * 1024) throw new UpdateException("更新说明太大，不像 update.json");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > 64 * 1024) throw new UpdateException("更新说明太大，不像 update.json");
        return bytes;
    }

    private async Task<string> DownloadAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(UpdatesFolder);
        // 文件名里不能带 setup / 安装：那样双击会进安装界面；这里一律用 --apply-update 启动
        var final = Path.Combine(UpdatesFolder, $"共享速搜-{manifest.Version}.exe");
        if (File.Exists(final))
        {
            if (await Task.Run(() => Verify(final, manifest), cancellationToken) is null) return final;
            TryDelete(final);
        }

        var part = final + ".part";
        SetStage(UpdateStage.Downloading, $"正在下载 {manifest.Version}…");
        DownloadProgress = 0;
        try
        {
            if (IsHttps(manifest.Url)) await DownloadHttpsAsync(manifest, part, cancellationToken);
            else await CopyLocalAsync(manifest, part, cancellationToken);

            // 文件没了多半是杀毒软件拦了，下次可能就好：不算这个包的错
            if (!File.Exists(part)) throw new UpdateException("下载的文件不见了，可能被杀毒软件拦了，稍后再试");
            var problem = await Task.Run(() => Verify(part, manifest), cancellationToken);
            if (problem is not null)
            {
                // 一次失败可能是网络截断、代理改了内容、共享盘上的安装包还没复制完；
                // 同一个包失败满 2 次才认定是坏包，以后不再下载
                RememberPackageFailure(manifest.Sha256);
                throw new UpdateException(problem);
            }
            File.Move(part, final, overwrite: true);
            return final;
        }
        finally { TryDelete(part); }
    }

    private async Task DownloadHttpsAsync(UpdateManifest manifest, string part, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await _http.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? (manifest.Size > 0 ? manifest.Size : 0);
        if (total > MaxPackageBytes) throw new UpdateException("安装包大小不对");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await CopyAsync(input, part, total, timeout.Token);
    }

    private async Task CopyLocalAsync(UpdateManifest manifest, string part, CancellationToken cancellationToken)
    {
        // 共享盘上的安装包：只读打开，不加锁（别人也能同时读）
        await Task.Run(async () =>
        {
            await using var input = new FileStream(manifest.Url, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, useAsync: true);
            await CopyAsync(input, part, input.Length, cancellationToken);
        }, cancellationToken);
    }

    private async Task CopyAsync(Stream input, string destination, long total, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        var buffer = new byte[256 * 1024];
        long copied = 0;
        var lastReport = Environment.TickCount64;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            copied += read;
            if (copied > MaxPackageBytes) throw new UpdateException("安装包大小不对");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (total > 0 && Environment.TickCount64 - lastReport > 500)
            {
                lastReport = Environment.TickCount64;
                var fraction = Math.Min(1, (double)copied / total);
                // 下载在后台线程上继续：进度回到界面线程更新
                _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    DownloadProgress = fraction;
                    if (Stage == UpdateStage.Downloading) SetStage(UpdateStage.Downloading, $"正在下载 {Latest?.Version} · {fraction * 100:F0}%");
                });
            }
        }
    }

    /// <summary>校验安装包：大小、SHA-256、程序自己写的产品名和版本号。返回 null 表示通过。</summary>
    internal static string? Verify(string path, UpdateManifest manifest)
    {
        if (UpdateRules.VerifyContent(path, manifest) is { } problem) return problem;
        if (OperatingSystem.IsWindows())
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(version.ProductName, InstallerEngine.ProductName, StringComparison.Ordinal)) return "这不是共享速搜的安装包，已丢弃";
            var fileVersion = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}";
            if (UpdateRules.Compare(fileVersion, manifest.Version) != 0) return $"安装包的版本（{fileVersion}）和更新说明（{manifest.Version}）不一致，已丢弃";
        }
        return null;
    }

    // ================= 安装 =================

    /// <summary>每 20 秒看一次：用户没在用就开始装。</summary>
    private void TryApplyWhenIdle()
    {
        if (_applying || Stage != UpdateStage.Ready || _readyPath is null || Latest is null) { _applyTimer.Stop(); return; }
        if (!_canApplyNow()) return;
        ApplyNow();
    }

    /// <summary>马上装（启动时发现上次已经下载好的版本，或者到了空闲时机）。</summary>
    public bool ApplyNow()
    {
        if (_applying || _readyPath is null || Latest is null || !File.Exists(_readyPath)) return false;
        // 换到已经装好的新版本：不用再校验安装包
        var isInstaller = _readyPath.StartsWith(UpdatesFolder, StringComparison.OrdinalIgnoreCase);
        bool signed = false;
        try { signed = _signedBytes is not null && _signature is not null &&
            UpdateRules.ReadSignedManifest(_signedBytes, _signature, UpdateKey.PublicKeyPem) == Latest; }
        catch (UpdateException) { }
        if (isInstaller && !signed)
        {
            SetStage(UpdateStage.Failed, "更新签名与待安装版本不一致，已停止安装");
            return false;
        }
        if (isInstaller && Verify(_readyPath, Latest) is { } problem)
        {
            TryDelete(_readyPath);
            _readyPath = null;
            SetStage(UpdateStage.Failed, problem);
            return false;
        }
        if (isInstaller) RecordAttempt(Latest.Version);
        _applying = true;
        _applyTimer.Stop();
        _checkTimer.Stop();
        try
        {
            _apply(Latest, _readyPath);
            return true;
        }
        catch (Exception ex)
        {
            _applying = false;
            SetStage(UpdateStage.Failed, "没能启动安装：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 程序刚启动时调用：上次已经下载好、校验过的新版本直接装，不等下一次检查。
    /// 返回 true 表示已经交给更新程序，当前进程应该退出。
    /// </summary>
    public static bool TryApplyPendingAtStartup(Action<UpdateManifest, string> apply)
    {
        try
        {
            if (!File.Exists(StatePath)) return false;
            var state = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StatePath));
            if (state?.SignedManifestBase64 is not { Length: > 0 } raw || raw.Length > 90_000 || state.Signature is null) return false;
            var manifest = UpdateRules.ReadSignedManifest(Convert.FromBase64String(raw), state.Signature, UpdateKey.PublicKeyPem);
            if (UpdateRules.Compare(manifest.Version, CurrentVersion) <= 0) return false;
            if (Blocked(manifest.Version)) return false;
            var path = Path.Combine(UpdatesFolder, $"共享速搜-{manifest.Version}.exe");
            if (!File.Exists(path) || Verify(path, manifest) is not null) return false;
            RecordAttempt(manifest.Version);
            apply(manifest, path);
            return true;
        }
        catch { return false; }
    }

    // ================= 失败记录 =================

    private sealed class UpdateState
    {
        public string Version { get; set; } = "";
        public int Attempts { get; set; }
        public DateTime LastAttemptUtc { get; set; }
        public UpdateManifest? Manifest { get; set; }
        public string? SignedManifestBase64 { get; set; }
        public string? Signature { get; set; }
        /// <summary>安装包（SHA-256）校验失败的次数：满 2 次就不再下载这个包。</summary>
        public Dictionary<string, int> PackageFailures { get; set; } = new();
    }

    private static UpdateState LoadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StatePath)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void SaveState(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(UpdatesFolder);
            var staging = StatePath + ".new";
            File.WriteAllText(staging, JsonSerializer.Serialize(state));
            File.Move(staging, StatePath, overwrite: true);
        }
        catch { }
    }

    private static void RecordAttempt(string version)
    {
        var state = LoadState();
        if (state.Version != version) { state.Version = version; state.Attempts = 0; }
        state.Attempts++;
        state.LastAttemptUtc = DateTime.UtcNow;
        SaveState(state);
    }

    private static bool Blocked(string version)
    {
        var state = LoadState();
        return state.Version == version && state.Attempts >= MaxAttempts && DateTime.UtcNow - state.LastAttemptUtc < TimeSpan.FromHours(24);
    }

    /// <summary>下载好的版本记下来：程序重启时直接装，不用再下载。</summary>
    private void RememberReady(UpdateManifest manifest)
    {
        var state = LoadState();
        if (state.Version != manifest.Version) state = new UpdateState { Version = manifest.Version, PackageFailures = state.PackageFailures };
        state.Manifest = manifest;
        state.SignedManifestBase64 = _signedBytes is null ? null : Convert.ToBase64String(_signedBytes);
        state.Signature = _signature;
        SaveState(state);
    }

    private const int BadPackageThreshold = 2;

    private static void RememberPackageFailure(string sha256)
    {
        var state = LoadState();
        state.PackageFailures[sha256] = state.PackageFailures.GetValueOrDefault(sha256) + 1;
        // 只留最近 20 个，文件不会越长越大
        foreach (var stale in state.PackageFailures.Keys.Take(Math.Max(0, state.PackageFailures.Count - 20)).ToList()) state.PackageFailures.Remove(stale);
        SaveState(state);
    }

    /// <summary>已经更新成功：删掉旧安装包，清掉失败记录。</summary>
    public static void MarkUpdateHealthy() => CleanupOldPackages(confirmed: true);

    private static void CleanupOldPackages(bool confirmed = false)
    {
        // A candidate that later rolls back must not erase the previous process's failure/backoff state.
        if (!confirmed && Environment.GetCommandLineArgs().Contains("--update-health-path", StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            if (!Directory.Exists(UpdatesFolder)) return;
            foreach (var file in Directory.EnumerateFiles(UpdatesFolder, "共享速搜-*.exe*"))
            {
                var name = Path.GetFileNameWithoutExtension(file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(file) : file);
                var version = name["共享速搜-".Length..];
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || UpdateRules.Compare(version, CurrentVersion) <= 0) TryDelete(file);
            }
            var state = LoadState();
            if (state.Version.Length > 0 && UpdateRules.Compare(state.Version, CurrentVersion) <= 0)
            {
                // 装上了：清掉尝试次数，但记着坏包，免得以后又下载
                SaveState(new UpdateState { PackageFailures = state.PackageFailures });
            }
            // 每个版本的更新程序（共享速搜-x.y.z.exe）都会在 %TEMP%\.net 下解压一份运行库，用完清掉
            var extracted = Path.Combine(Path.GetTempPath(), ".net");
            if (Directory.Exists(extracted))
                foreach (var folder in Directory.EnumerateDirectories(extracted, "共享速搜-*"))
                    try { Directory.Delete(folder, recursive: true); } catch { }
        }
        catch { }
    }

    // ================= 便携版 =================

    /// <summary>当前运行的不是登记的安装版，而安装版已经 ≥ 新版本：返回安装版的路径。</summary>
    private static string? InstalledCopyIsAtLeast(string version)
    {
        var installed = InstallerEngine.FindInstalled();
        if (installed is null || IsRunningInstalledCopy(installed)) return null;
        var exe = Path.Combine(installed.Directory, InstallerEngine.ExeName);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            var installedVersion = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
            return UpdateRules.Compare(installedVersion, version) >= 0 ? exe : null;
        }
        catch { return null; }
    }

    public static bool IsRunningInstalledCopy(InstalledInfo? installed = null)
    {
        installed ??= InstallerEngine.FindInstalled();
        if (installed is null) return false;
        try
        {
            var self = Path.GetFullPath(Environment.ProcessPath ?? "");
            return Path.GetFullPath(Path.Combine(installed.Directory, InstallerEngine.ExeName)).Equals(self, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ================= 小工具 =================

    private void SetStage(UpdateStage stage, string text)
    {
        Stage = stage;
        StatusText = text;
        if (stage == UpdateStage.Ready && Latest is not null && _readyPath is not null && _readyPath.StartsWith(UpdatesFolder, StringComparison.OrdinalIgnoreCase)) RememberReady(Latest);
        Changed?.Invoke();
    }

    private static bool IsHttps(string value) => UpdateRules.IsHttps(value);
    private static bool IsHttp(string value) => UpdateRules.IsPlainHttp(value);

    /// <summary>共享盘离线时读文件可能卡几十秒：最多等 15 秒。</summary>
    private static async Task<byte[]> ReadLocalAsync(string path, CancellationToken cancellationToken)
    {
        var read = Task.Run(() =>
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 64 * 1024) throw new UpdateException("更新说明太大，不像 update.json");
            return File.ReadAllBytes(path);
        }, cancellationToken);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken));
        if (finished != read) throw new UpdateException("读取更新源超时，共享盘可能离线");
        return await read;
    }

    private static string Friendly(Exception ex) => ex switch
    {
        UpdateException update => update.Message,
        HttpRequestException http when http.StatusCode is { } code => $"更新源返回 {(int)code}，稍后再试",
        HttpRequestException => "连不上更新源，稍后自动再试",
        TaskCanceledException or TimeoutException => "连接更新源超时，稍后自动再试",
        FileNotFoundException or DirectoryNotFoundException => "更新源路径不存在",
        UnauthorizedAccessException => "没有权限读取更新源",
        IOException io => "读写更新文件失败：" + io.Message,
        _ => "检查更新失败：" + ex.Message
    };

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    public void Dispose()
    {
        _work?.Cancel();
        _checkTimer.Stop();
        _applyTimer.Stop();
        _http.Dispose();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
