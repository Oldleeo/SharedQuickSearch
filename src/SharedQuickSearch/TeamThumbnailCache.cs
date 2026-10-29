using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SharedQuickSearch;

public enum TeamCacheState { Off, Checking, ReadWrite, ReadOnly, Offline, NoPermission }

/// <summary>
/// 全组共用的缩略图缓存：放在共享盘上一个专门的文件夹（名字固定是 .共享速搜缓存）。
/// 一个人看过、生成过的缩略图写进去，其他人直接读，不用再从几百 MB 的 PSD 里生成一遍。
///
/// 规矩：
/// - 只往这个文件夹里写，共享盘上别的地方一律不碰；索引和实时监听都跳过它。
/// - 键用 \\服务器\共享\… 这种完整路径算：有人映射成 Z:\、有人直接用 \\NAS\，同一个文件对得上。
///   本机磁盘上的文件不进全组缓存（别人看不到那个文件）。
/// - 写入先写临时文件再改名，已经有了就不覆盖（只有低清版会被清晰版替换）；多人同时写同一张也不会写坏。
/// - 没有写权限就只读，只用别人生成好的。
/// - 网络出问题（超时、断开）就暂停 2 分钟，期间完全不碰共享盘，界面不会因为它卡住。
/// - 不做失败标记、不做“最近使用”时间更新、不自动清理：这些都会在共享盘上产生大量写入。
///   缓存大小大约是“看过的图片数 × 20 KB”，上限就是共享盘上的素材总数。
/// </summary>
internal sealed class TeamThumbnailCache
{
    private const string StoreName = "thumbs-v1";
    private static readonly TimeSpan OfflinePause = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SlowRead = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _writes = new(2);
    private readonly SemaphoreSlim _reads = new(2);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);
    private long _pausedUntilTicks;
    private volatile TeamCacheState _state = TeamCacheState.Checking;
    private long _hits, _writesDone;
    private int _probing;

    public TeamThumbnailCache(string folder)
    {
        Folder = NormalizeFolder(folder);
        Store = Path.Combine(Folder, StoreName);
    }

    /// <summary>缓存根目录（以 .共享速搜缓存 结尾）。</summary>
    public string Folder { get; }
    private string Store { get; }
    public TeamCacheState State => _state;
    public bool CanRead => _state is TeamCacheState.ReadWrite or TeamCacheState.ReadOnly && !Paused;
    public bool CanWrite => _state == TeamCacheState.ReadWrite && !Paused;
    public long Hits => Interlocked.Read(ref _hits);
    public long Writes => Interlocked.Read(ref _writesDone);
    private bool Paused => DateTime.UtcNow.Ticks < Interlocked.Read(ref _pausedUntilTicks);

    public event Action? StateChanged;

    /// <summary>用户选的文件夹不叫 .共享速搜缓存 时，在里面建一个，缓存永远在这个名字的文件夹里。</summary>
    public static string NormalizeFolder(string chosen)
    {
        var path = PathRules.Normalize(chosen);
        return Path.GetFileName(path.TrimEnd('\\', '/')).Equals(PathRules.TeamCacheFolderName, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.Combine(path, PathRules.TeamCacheFolderName);
    }

    /// <summary>
    /// 建议的位置：第一个共享盘目录所在的共享根目录（\\服务器\共享），而不是索引目录里面，
    /// 这样同事写缓存时，索引目录的实时监听收到的事件少很多。
    /// </summary>
    public static string? SuggestFor(IEnumerable<string> roots) =>
        TeamCacheLocator.Candidates(roots).FirstOrDefault() is { } shareRoot ? NormalizeFolder(shareRoot) : null;

    // ================= 状态检查 =================

    /// <summary>检查能不能读、能不能写（后台线程，最多等 8 秒）。</summary>
    public async Task<TeamCacheState> ProbeAsync()
    {
        if (Interlocked.CompareExchange(ref _probing, 1, 0) != 0) return _state;
        SetState(TeamCacheState.Checking);
        var probe = Task.Run(() =>
        {
            try { return ProbeCore(); }
            finally { Volatile.Write(ref _probing, 0); }
        });
        var finished = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
        var state = finished == probe ? await probe.ConfigureAwait(false) : TeamCacheState.Offline;
        if (state == TeamCacheState.Offline) Pause();
        else Interlocked.Exchange(ref _pausedUntilTicks, 0);
        SetState(state);
        return state;
    }

    private TeamCacheState ProbeCore()
    {
        try
        {
            var parent = Path.GetDirectoryName(Folder);
            if (parent is null || !Directory.Exists(parent)) return TeamCacheState.Offline;
            try
            {
                if (!Directory.Exists(Folder))
                {
                    var created = Directory.CreateDirectory(Folder);
                    // 隐藏起来：同事在资源管理器里不会看到一个奇怪的文件夹
                    try { created.Attributes |= FileAttributes.Hidden; } catch { }
                    WriteReadme();
                }
                Directory.CreateDirectory(Store);
                var test = Path.Combine(Store, $".write-test-{Guid.NewGuid():N}");
                File.WriteAllBytes(test, new byte[] { 1 });
                File.Delete(test);
                return TeamCacheState.ReadWrite;
            }
            // 共享根目录常常只能读、不能建文件夹：这和“连不上”是两回事，要让用户换一个能写的子文件夹
            catch (UnauthorizedAccessException) { return Directory.Exists(Store) ? TeamCacheState.ReadOnly : TeamCacheState.NoPermission; }
            catch (IOException) when (Directory.Exists(Store)) { return TeamCacheState.ReadOnly; }
        }
        catch { return TeamCacheState.Offline; }
    }

    private void WriteReadme() => TeamCacheLocator.WriteReadme(Folder);

    // ================= 键：统一成 \\服务器\共享 路径 =================

    /// <summary>这个文件在全组缓存里的键；本机磁盘上的文件返回 null（别人看不到它，不进全组缓存）。</summary>
    public static string? KeyFor(FileEntry entry)
    {
        var unc = ToUnc(entry.FullPath);
        if (unc is null) return null;
        var text = $"{unc.ToLowerInvariant()}|{entry.LastWriteUtc.Ticks}|{entry.Size}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>Z:\设计部\a.psd → \\NAS\共享\设计部\a.psd；见 <see cref="UncPaths.ToUnc"/>。</summary>
    public static string? ToUnc(string path) => UncPaths.ToUnc(path);

    // ================= 读写 =================

    private string PathFor(string key) => Path.Combine(Store, key[..2], key + ".sqt");

    /// <summary>
    /// 从全组缓存读一张（后台）。最多同时 2 个读取；名额被占满（共享盘卡住）时直接当没有，不排队。
    /// 单次超过 3 秒就当没有、暂停 2 分钟；卡住的那次读完后才释放名额，所以卡住最多占 2 个线程。
    /// </summary>
    public async Task<ThumbResult?> TryReadAsync(string key)
    {
        if (!CanRead || !_reads.Wait(0)) return null;
        var read = Task.Run(() => TryRead(key));
        _ = read.ContinueWith(_ => _reads.Release(), TaskScheduler.Default);
        var finished = await Task.WhenAny(read, Task.Delay(ReadTimeout)).ConfigureAwait(false);
        if (finished == read) return await read.ConfigureAwait(false);
        Pause();
        return null;
    }

    private ThumbResult? TryRead(string key)
    {
        if (!CanRead) return null;
        var watch = Stopwatch.StartNew();
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) { CheckSlow(watch); return null; }
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024))
            {
                if (stream.Length > 4 * 1024 * 1024) return null;
                bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) return null;
                    read += n;
                }
            }
            CheckSlow(watch);
            var result = ThumbnailDiskCache.Unpack(bytes);
            if (result is not null) Interlocked.Increment(ref _hits);
            return result;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { Pause(); return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch { return null; }
    }

    /// <summary>写进全组缓存（后台调用）。已经有清晰版就不写；只有低清版时用清晰版替换。</summary>
    public void Write(string key, ThumbResult result)
    {
        if (!CanWrite || result.Image is null) return;
        if (!_writes.Wait(0)) return; // 同时最多 2 个写入，忙就跳过这一张，下次有人看到再写
        string? temporary = null;
        try
        {
            var path = PathFor(key);
            if (File.Exists(path) && (result.LowQuality || !ExistingIsLowQuality(path))) return;
            var data = ThumbnailDiskCache.Pack(result);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporary, data);
            // 低清 → 清晰才覆盖；别人刚好同时写了同一张就用他的
            try { File.Move(temporary, path, overwrite: !result.LowQuality); temporary = null; }
            catch (IOException) when (File.Exists(path)) { }
            if (temporary is null) Interlocked.Increment(ref _writesDone);
        }
        catch (UnauthorizedAccessException) { SetState(TeamCacheState.ReadOnly); }
        catch (IOException) { Pause(); }
        catch { }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch { }
            _writes.Release();
        }
    }

    private static bool ExistingIsLowQuality(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[16];
            return stream.Read(header, 0, 16) == 16 && (BitConverter.ToInt32(header, 12) & 1) != 0;
        }
        catch { return false; }
    }

    private void CheckSlow(Stopwatch watch)
    {
        // 读一个 20 KB 的小文件超过 3 秒：网络有问题，先停一停
        if (watch.Elapsed > SlowRead) Pause();
    }

    private void Pause()
    {
        Interlocked.Exchange(ref _pausedUntilTicks, (DateTime.UtcNow + OfflinePause).Ticks);
        StateChanged?.Invoke();
    }

    private void SetState(TeamCacheState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke();
    }

    /// <summary>界面上显示的状态。</summary>
    public string Describe() => _state switch
    {
        TeamCacheState.Checking => "正在检查…",
        TeamCacheState.ReadWrite when Paused => "网络不稳定，暂停 2 分钟后再用",
        TeamCacheState.ReadWrite => "可以读写：你生成的缩略图，其他人也能直接用",
        TeamCacheState.ReadOnly => "只能读：没有写入权限，只用别人生成好的",
        TeamCacheState.Offline => "连不上这个文件夹，过一会儿自动再试",
        TeamCacheState.NoPermission => "没有权限在这里建文件夹。换一个你能写入的子文件夹，比如 \\\\NAS\\设计部\\公共",
        _ => "未开启"
    };
}
