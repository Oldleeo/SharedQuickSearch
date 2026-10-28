using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace SharedQuickSearch;

/// <summary>本次运行的缩略图命中统计（设置页展示，便于判断慢在哪一层）。</summary>
public sealed record ThumbnailStats(
    long Memory, long Disk, long WindowsCache, long Embedded, long Generated, long Failed,
    double AverageMilliseconds, int PrefetchDone, int PrefetchTotal, bool PrefetchRunning,
    long Team = 0, long TeamWrites = 0);

/// <summary>
/// 分层缩略图管线，从快到慢逐层尝试，命中即停：
///   ① 内存 LRU（360 张）
///   ② 本机磁盘缓存 —— 零网络，约 1ms
///   ③ Windows 缩略图缓存（InCacheOnly，不读文件内容）
///   ④ 文件头内嵌预览图 —— PSD / JPG / AI / PDF 只读开头
///   ⑤ 完整生成 —— 图片 WIC 缩放解码，其它走 Shell
/// 所有层都失败的文件记一个“失败标记”，同一版本不再重试。
/// 内嵌图太小（JPG 约 160px）时先显示，再在后台精修；精修只做还在屏幕上的、有上限、前台忙时暂停。
/// 空闲时后台预热：按修改时间从新到旧提前生成，用户在浏览或看预览时自动让路。
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    public const int ThumbnailSize = 256;
    private const int MemoryCapacity = 360;
    private const int LowQualityEdge = 200;
    private const int PrefetchMax = 60_000;
    private const int RefineQueueLimit = 48;
    /// <summary>精修要整份读取：超过这个大小就保留内嵌小图，不为清晰度多搬几十 MB。</summary>
    private const long RefineMaxBytes = 24L * 1024 * 1024;
    /// <summary>预热时，没有内嵌预览的文件超过这个大小就不整份读取，留到真正看到时再生成。</summary>
    private const long PrefetchFullReadLimit = 4L * 1024 * 1024;

    public static readonly string DefaultFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oldlee", "共享速搜", "thumbs");

    private readonly StaWorkerPool _probe = new(2, "缓存探测", ThreadPriority.Normal);
    private readonly StaWorkerPool _shell = new(4, "缩略图", ThreadPriority.BelowNormal);
    private readonly StaWorkerPool _background = new(1, "缩略图后台", ThreadPriority.Lowest);
    private readonly StaWorkerPool _preview = new(1, "预览", ThreadPriority.Normal);
    private readonly SemaphoreSlim _io = new(8);
    private volatile ThumbnailDiskCache _disk;
    private volatile TeamThumbnailCache? _team;
    private readonly Timer _teamProbe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, byte> _failed = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _map = new();
    private readonly LinkedList<CacheItem> _lru = new();
    private readonly LinkedList<(FileEntry Entry, string Key)> _refineQueue = new();
    private readonly HashSet<string> _refineKeys = new();
    private int _refineWorker;
    private CancellationTokenSource? _prefetch;
    private long _prefetchSignature;
    private int _foreground;
    private int _prefetchDone, _prefetchTotal, _prefetchRunning;
    private long _hitTeam;
    private long _hitMemory, _hitDisk, _hitWindows, _hitEmbedded, _generated, _failures, _loads, _loadTicks;

    private sealed record CacheItem(string Key, BitmapSource Image);

    /// <summary>低清图精修完成（在后台线程触发）。</summary>
    public event Action<FileEntry, BitmapSource>? Refined;

    /// <summary>精修前询问：这个条目还在界面上吗？（后台线程调用，实现要线程安全）</summary>
    public Func<FileEntry, bool>? RefineFilter { get; set; }

    public bool PrefetchEnabled { get; set; } = true;
    public string CacheFolder => _disk.Folder;
    internal TeamThumbnailCache? Team => _team;
    /// <summary>全组缓存的状态变了（可读写 / 只读 / 连不上）。可能在后台线程触发。</summary>
    public event Action? TeamStateChanged;

    /// <summary>本机缓存放哪：空 = 默认位置；选了别的文件夹就在里面建一个“共享速搜缩略图”。</summary>
    public static string ResolveLocalFolder(string? custom)
    {
        if (string.IsNullOrWhiteSpace(custom)) return DefaultFolder;
        // 本机缓存只能在本机硬盘上：网络路径（预设文件、手改的设置里写错了）一律退回默认位置，
        // 否则换位置时“清掉旧缓存”会删到共享盘上去
        if (!Setup.InstallerEngine.IsLocalPath(custom.Trim().Trim('"'))) return DefaultFolder;
        var path = Path.GetFullPath(custom.Trim().Trim('"'));
        return Path.GetFileName(path.TrimEnd('\\', '/')).Equals(LocalFolderName, StringComparison.OrdinalIgnoreCase) ? path : Path.Combine(path, LocalFolderName);
    }

    public const string LocalFolderName = "共享速搜缩略图";

    /// <summary>
    /// 换本机缓存的位置：新位置从空开始，旧位置的缓存删掉（只删我们自己的那个文件夹）。
    /// 返回 false 表示位置没变。
    /// </summary>
    public bool MoveLocalCache(string? custom)
    {
        var folder = ResolveLocalFolder(custom);
        var old = _disk;
        if (string.Equals(Path.GetFullPath(old.Folder).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        StopPrefetch();
        _disk = new ThumbnailDiskCache(folder, old.LimitBytes);
        _ = Task.Run(async () =>
        {
            // 等已经在路上的写入写完（它们拿的还是旧位置），再删旧位置；旧位置不在本机硬盘上就不删
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (Setup.InstallerEngine.IsLocalPath(old.Folder) &&
                !string.Equals(_disk.Folder, old.Folder, StringComparison.OrdinalIgnoreCase)) old.Clear();
            _disk.Trim();
        });
        return true;
    }

    /// <summary>开 / 关 / 换全组共用缓存。folder 为空 = 关闭。</summary>
    public void SetTeamFolder(string? folder)
    {
        var target = string.IsNullOrWhiteSpace(folder) ? null : TeamThumbnailCache.NormalizeFolder(folder);
        if (string.Equals(_team?.Folder, target, StringComparison.OrdinalIgnoreCase)) return;
        if (_team is { } previous) previous.StateChanged -= OnTeamStateChanged;
        if (target is null)
        {
            _team = null;
            OnTeamStateChanged();
            return;
        }
        var team = new TeamThumbnailCache(target);
        team.StateChanged += OnTeamStateChanged;
        _team = team;
        _ = team.ProbeAsync();
        // 换了来源：本次运行记下的“生成不出来”可能是别人那边能生成的，清掉
        _failed.Clear();
        Interlocked.Exchange(ref _prefetchSignature, 0);
    }

    private void OnTeamStateChanged()
    {
        try { TeamStateChanged?.Invoke(); } catch { }
    }

    public int CacheLimitMegabytes
    {
        get => (int)(_disk.LimitBytes / 1024 / 1024);
        set { _disk.LimitBytes = Math.Max(256, value) * 1024L * 1024; _ = Task.Run(_disk.Trim); }
    }

    public ThumbnailService(int cacheLimitMegabytes, string? localFolder = null)
    {
        _disk = new ThumbnailDiskCache(ResolveLocalFolder(localFolder), Math.Max(256, cacheLimitMegabytes) * 1024L * 1024);
        _ = Task.Run(_disk.Trim);
        // 全组缓存连不上 / 没权限时，每 5 分钟再看一次
        _teamProbe = new Timer(_ =>
        {
            if (_team is { State: TeamCacheState.Offline or TeamCacheState.ReadOnly or TeamCacheState.NoPermission } team) _ = team.ProbeAsync();
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>前台在用网络时持有它，后台预热和精修会暂停让路。</summary>
    public IDisposable ForegroundScope()
    {
        Interlocked.Increment(ref _foreground);
        return new Scope(this);
    }

    private sealed class Scope(ThumbnailService owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._foreground); }
    }

    // ================= 前台加载 =================

    public bool TryGetCached(FileEntry entry, out BitmapSource? image)
    {
        var key = ThumbnailDiskCache.KeyFor(entry);
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }
        image = null;
        return false;
    }

    public async Task<BitmapSource?> LoadAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        // 文件夹不做缩略图：Shell 生成文件夹预览要遍历子目录，走网络很慢，图标已足够辨认。
        if (entry.IsDirectory) return null;
        if (TryGetCached(entry, out var cached)) { Interlocked.Increment(ref _hitMemory); return cached; }
        var key = ThumbnailDiskCache.KeyFor(entry);
        if (_failed.ContainsKey(key)) return null;

        using var scope = ForegroundScope();
        var watch = Stopwatch.StartNew();
        // 共享盘离线：只查本机缓存，不去碰网络（否则 SMB 超时会把线程池卡住）
        var (result, definitive) = await GenerateAsync(entry, key, _probe, _shell, readDisk: true, count: true, long.MaxValue,
            deepScan: true, diskOnly: entry.IsOffline, cancellationToken).ConfigureAwait(false);
        if (result is not null) ApplySize(entry, result);
        if (result?.Image is not { } image)
        {
            if (definitive) _failed.TryAdd(key, 0);
            Interlocked.Increment(ref _failures);
            return null;
        }
        Put(key, image);
        if (result.LowQuality) QueueRefine(entry, key);
        Interlocked.Increment(ref _loads);
        Interlocked.Add(ref _loadTicks, watch.ElapsedTicks);
        return image;
    }

    /// <summary>预览窗格用的大图 Shell 缩略图（PSD / AI / PDF / Office / 文件夹）。</summary>
    public async Task<BitmapSource?> LoadShellPreviewAsync(string path, int size, CancellationToken cancellationToken)
    {
        using var scope = ForegroundScope();
        return await _preview.Run(() => ShellThumbnail.Load(path, size), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>预览窗格用的大图（图片）：整份读一次，按方向转正，长边不超过 maxEdge。</summary>
    public async Task<(BitmapSource? Image, int Width, int Height)> LoadPreviewImageAsync(string path, int maxEdge, CancellationToken cancellationToken)
    {
        using var scope = ForegroundScope();
        var decoded = await Task.Run(() => ImageCodec.DecodeFile(path, maxEdge), cancellationToken).ConfigureAwait(false);
        return (decoded?.Image, decoded?.Width ?? 0, decoded?.Height ?? 0);
    }

    /// <returns>Definitive = 文件确实打开过、所有层都试过仍没有图（可以记失败标记）。</returns>
    private async Task<(ThumbResult? Result, bool Definitive)> GenerateAsync(FileEntry entry, string key, StaWorkerPool probe, StaWorkerPool sta,
        bool readDisk, bool count, long fullReadLimit, bool deepScan, bool diskOnly, CancellationToken cancellationToken, bool share = true)
    {
        var path = entry.FullPath;
        var extension = entry.Extension;

        // ② 本机磁盘缓存（含失败标记）
        if (readDisk)
        {
            var disk = await RunIoAsync(() => _disk.TryRead(key), cancellationToken).ConfigureAwait(false);
            if (disk?.Image is not null)
            {
                if (count) Interlocked.Increment(ref _hitDisk);
                return (disk.LowQuality && !CanRefine(entry) ? disk with { LowQuality = false } : disk, false);
            }
            if (await RunIoAsync(() => _disk.IsNegative(key) ? "" : null, cancellationToken).ConfigureAwait(false) is not null) return (null, true);
        }
        if (diskOnly) return (null, false);

        // ②½ 全组共用缓存：同事看过的，直接从共享盘上的缓存文件夹拿一张 20 KB 的小图，顺手存一份到本机
        if (_team is { CanRead: true } team && TeamThumbnailCache.KeyFor(entry) is { } teamKey)
        {
            // 单独的读取通道 + 3 秒超时：共享盘卡住时不占本机缓存的读取名额，缩略图照常出来
            var shared = await team.TryReadAsync(teamKey).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (shared?.Image is not null)
            {
                var local = _disk;
                _ = Task.Run(() => local.Write(key, shared));
                if (count) Interlocked.Increment(ref _hitTeam);
                return (shared.LowQuality && !CanRefine(entry) ? shared with { LowQuality = false } : shared, false);
            }
        }

        // ③ Windows 缩略图缓存（资源管理器看过的文件）—— 单独的线程池，不排在慢生成后面
        var windows = await probe.Run(() => ShellThumbnail.Load(path, ThumbnailSize, ShellThumbnail.Mode.ThumbnailOnly | ShellThumbnail.Mode.InCacheOnly), cancellationToken).ConfigureAwait(false);
        if (windows is not null)
        {
            var result = new ThumbResult(ImageCodec.FitWithin(windows, ThumbnailSize), 0, 0, false);
            WriteLater(entry, key, result, share);
            if (count) Interlocked.Increment(ref _hitWindows);
            return (result, false);
        }

        // ④ 文件头内嵌预览图
        EmbeddedThumbnail? embedded = null;
        if (EmbeddedThumbnailLocator.Supports(extension))
        {
            embedded = await RunIoAsync(() => EmbeddedThumbnailLocator.Locate(path, extension, deepScan), cancellationToken).ConfigureAwait(false);
            var image = embedded is null ? null : ImageCodec.DecodeEmbedded(embedded);
            if (image is not null)
            {
                var small = image.PixelWidth < LowQualityEdge && image.PixelHeight < LowQualityEdge;
                var result = new ThumbResult(ImageCodec.FitWithin(image, ThumbnailSize), embedded!.Width, embedded.Height, small && CanRefine(entry));
                // 低清版同步写：保证它一定落在稍后精修写入的清晰版之前，不会把清晰版覆盖回去
                if (result.LowQuality) { _disk.Write(key, result); if (share) ShareLater(entry, result); }
                else WriteLater(entry, key, result, share);
                if (count) Interlocked.Increment(ref _hitEmbedded);
                return (result, false);
            }
        }

        // ⑤ 完整生成
        cancellationToken.ThrowIfCancellationRequested();
        var dimensionsOnly = embedded is { Width: > 0 } ? new ThumbResult(null, embedded.Width, embedded.Height, false) : null;
        if (entry.Size > fullReadLimit) return (dimensionsOnly, false);
        var full = await sta.Run(() => GenerateFull(path, extension), cancellationToken).ConfigureAwait(false);
        if (full?.Image is not null)
        {
            if (full.Width == 0 && embedded is { Width: > 0 }) full = full with { Width = embedded.Width, Height = embedded.Height };
            WriteLater(entry, key, full, share);
            if (count) Interlocked.Increment(ref _generated);
            return (full, false);
        }

        // 只有“确实试全了”才记失败标记：
        //  · 预热只扫了 AI/EPS 前 512 KB，不算试全
        //  · 文件此刻打不开（共享盘掉线、权限、SMB 抖动）不算失败，是暂时的
        var allLayersTried = deepScan || extension.ToLowerInvariant() is not (".ai" or ".eps");
        if (!allLayersTried) return (dimensionsOnly, false);
        var reachable = await RunIoAsync(() => File.Exists(path) ? "" : null, cancellationToken).ConfigureAwait(false) is not null;
        if (!reachable) return (dimensionsOnly, false);
        _ = Task.Run(() => _disk.WriteNegative(key));
        return (dimensionsOnly, true);
    }

    private static ThumbResult? GenerateFull(string path, string extension)
    {
        if (FileTypes.Images.Contains(extension))
        {
            var decoded = ImageCodec.DecodeFile(path, ThumbnailSize);
            if (decoded?.Image is not null) return decoded;
        }
        var shell = ShellThumbnail.Load(path, ThumbnailSize);
        return shell is null ? null : new ThumbResult(ImageCodec.FitWithin(shell, ThumbnailSize), 0, 0, false);
    }

    /// <summary>只有 WIC 能直接解码、且不算太大的图片才值得精修；PSD 的内嵌图就是 Photoshop 给的上限。</summary>
    private static bool CanRefine(FileEntry entry) => FileTypes.Images.Contains(entry.Extension) && entry.Size <= RefineMaxBytes;

    /// <summary>编码 + 写盘放后台，不拖慢图片上屏。</summary>
    private void WriteLater(FileEntry entry, string key, ThumbResult result, bool share)
    {
        var disk = _disk;
        _ = Task.Run(() => disk.Write(key, result));
        if (share) ShareLater(entry, result);
    }

    /// <summary>写进全组缓存（开了、而且有写权限时）。本机磁盘上的文件不写。</summary>
    private void ShareLater(FileEntry entry, ThumbResult result)
    {
        if (_team is not { CanWrite: true } team || TeamThumbnailCache.KeyFor(entry) is not { } teamKey) return;
        _ = Task.Run(() => team.Write(teamKey, result));
    }

    // ================= 精修（低清 → 清晰） =================

    private void QueueRefine(FileEntry entry, string key)
    {
        lock (_refineQueue)
        {
            if (!_refineKeys.Add(key)) return;
            // 新的放最前：最近看到的先精修；超出上限丢掉最旧的
            _refineQueue.AddFirst((entry, key));
            while (_refineQueue.Count > RefineQueueLimit && _refineQueue.Last is { } last)
            {
                _refineKeys.Remove(last.Value.Key);
                _refineQueue.RemoveLast();
            }
        }
        if (Interlocked.CompareExchange(ref _refineWorker, 1, 0) == 0) _ = Task.Run(RefineLoopAsync);
    }

    private async Task RefineLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                while (Volatile.Read(ref _foreground) > 0) await Task.Delay(200, _lifetime.Token).ConfigureAwait(false);
                (FileEntry Entry, string Key) next;
                lock (_refineQueue)
                {
                    if (_refineQueue.First is null) return;
                    next = _refineQueue.First.Value;
                    _refineQueue.RemoveFirst();
                    _refineKeys.Remove(next.Key);
                }

                var (entry, key) = next;
                if (RefineFilter is { } filter && !filter(entry)) continue; // 已经滚走了
                ThumbResult? refined = null;
                try { refined = await _background.Run(() => ImageCodec.DecodeFile(entry.FullPath, ThumbnailSize), _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch { }

                if (refined?.Image is not { } image)
                {
                    _disk.MarkFinal(key); // 解不出来就认了内嵌图，下次不再整份读取
                    continue;
                }
                _disk.Write(key, refined);
                ShareLater(entry, refined);
                Put(key, image);
                ApplySize(entry, refined);
                try { Refined?.Invoke(entry, image); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Volatile.Write(ref _refineWorker, 0);
            // 退出的瞬间又有新任务进来：重新拉起一个
            bool pending;
            lock (_refineQueue) pending = _refineQueue.Count > 0;
            if (pending && !_lifetime.IsCancellationRequested && Interlocked.CompareExchange(ref _refineWorker, 1, 0) == 0) _ = Task.Run(RefineLoopAsync);
        }
    }

    // ================= 后台预热 =================

    public static bool IsPrefetchable(FileEntry entry) =>
        !entry.IsDirectory && (FileTypes.Images.Contains(entry.Extension)
            || entry.Extension.ToLowerInvariant() is ".psd" or ".psb" or ".ai" or ".eps" or ".pdf" or ".indd");

    /// <summary>
    /// 开始预热。索引内容没变时直接跳过（上一轮在跑或已跑完），
    /// 避免每 30 分钟校准、每次保存设置都把失败文件重新探测一遍。
    /// </summary>
    public void StartPrefetch(IReadOnlyList<FileEntry> entries, bool force = false)
    {
        if (!PrefetchEnabled) { StopPrefetch(); return; }
        long signature = entries.Count;
        unchecked { foreach (var entry in entries) signature = signature * 31 + entry.LastWriteUtc.Ticks; }
        if (!force && signature == Interlocked.Read(ref _prefetchSignature)) return;

        CancelPrefetch();
        Interlocked.Exchange(ref _prefetchSignature, signature);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _prefetch = cancellation;
        // 同步置位：界面立刻能看到“准备中”，也不会被旧任务覆盖
        Volatile.Write(ref _prefetchDone, 0);
        Volatile.Write(ref _prefetchTotal, 0);
        Volatile.Write(ref _prefetchRunning, 1);

        _ = Task.Run(async () =>
        {
            try
            {
                // 最近改过的文件最可能被搜到，先做它们。
                var queue = entries.Where(IsPrefetchable).OrderByDescending(x => x.LastWriteUtc).Take(PrefetchMax).ToArray();
                if (cancellation.IsCancellationRequested) return;
                Volatile.Write(ref _prefetchTotal, queue.Length);
                await Task.Delay(TimeSpan.FromSeconds(4), cancellation.Token).ConfigureAwait(false);
                foreach (var entry in queue)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    while (Volatile.Read(ref _foreground) > 0) await Task.Delay(250, cancellation.Token).ConfigureAwait(false);
                    if (_disk.IsFull) break;
                    if (entry.IsOffline) { Interlocked.Increment(ref _prefetchDone); continue; }
                    var key = ThumbnailDiskCache.KeyFor(entry);
                    if (!_disk.Contains(key))
                    {
                        try
                        {
                            var (result, _) = await GenerateAsync(entry, key, _background, _background, readDisk: false, count: false,
                                PrefetchFullReadLimit, deepScan: false, diskOnly: false, cancellation.Token,
                                // 预热生成的不上传到全组缓存：全组缓存只放真有人看过的图，不然第一台开预热的电脑会把整个索引都传上去
                                share: false).ConfigureAwait(false);
                            if (result is not null) ApplySize(entry, result);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                        // 节流：给共享盘和其他同事留带宽
                        await Task.Delay(20, cancellation.Token).ConfigureAwait(false);
                    }
                    Interlocked.Increment(ref _prefetchDone);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                // 只有自己仍是当前这一轮时才复位，旧任务收尾不会覆盖新一轮的状态
                if (Interlocked.CompareExchange(ref _prefetch, null, cancellation) == cancellation) Volatile.Write(ref _prefetchRunning, 0);
            }
        });
    }

    /// <summary>共享盘在线状态变化：忘掉本次运行记下的失败，并允许预热重新跑一轮。</summary>
    public void ResetFailures()
    {
        _failed.Clear();
        Interlocked.Exchange(ref _prefetchSignature, 0);
    }

    /// <summary>用户关闭预热或清空缓存：停止并忘掉签名，下次开启会重新跑。</summary>
    public void StopPrefetch()
    {
        CancelPrefetch();
        Interlocked.Exchange(ref _prefetchSignature, 0);
    }

    private void CancelPrefetch()
    {
        var current = Interlocked.Exchange(ref _prefetch, null);
        try { current?.Cancel(); } catch (ObjectDisposedException) { }
        Volatile.Write(ref _prefetchRunning, 0);
    }

    // ================= 统计 / 维护 =================

    public ThumbnailStats GetStats()
    {
        var loads = Interlocked.Read(ref _loads);
        var average = loads == 0 ? 0 : Interlocked.Read(ref _loadTicks) * 1000.0 / Stopwatch.Frequency / loads;
        return new ThumbnailStats(
            Interlocked.Read(ref _hitMemory), Interlocked.Read(ref _hitDisk), Interlocked.Read(ref _hitWindows),
            Interlocked.Read(ref _hitEmbedded), Interlocked.Read(ref _generated), Interlocked.Read(ref _failures),
            average, Volatile.Read(ref _prefetchDone), Volatile.Read(ref _prefetchTotal), Volatile.Read(ref _prefetchRunning) == 1,
            Interlocked.Read(ref _hitTeam), _team?.Writes ?? 0);
    }

    public Task<(long Bytes, int Files)> MeasureCacheAsync() => Task.Run(_disk.Measure);

    public Task ClearCacheAsync()
    {
        StopPrefetch();
        _failed.Clear();
        lock (_sync) { _map.Clear(); _lru.Clear(); }
        return Task.Run(_disk.Clear);
    }

    private static void ApplySize(FileEntry entry, ThumbResult result)
    {
        if (result.Width <= 0 || result.Height <= 0 || entry.Width > 0) return;
        entry.Width = result.Width;
        entry.Height = result.Height;
        entry.Notify(nameof(FileEntry.ResolutionLabel));
    }

    private async Task<T?> RunIoAsync<T>(Func<T?> work, CancellationToken cancellationToken) where T : class
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(work, cancellationToken).ConfigureAwait(false); }
        finally { _io.Release(); }
    }

    private void Put(string key, BitmapSource image)
    {
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing)) { _lru.Remove(existing); _map.Remove(key); }
            _map[key] = _lru.AddFirst(new CacheItem(key, image));
            while (_lru.Count > MemoryCapacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Dispose()
    {
        _teamProbe.Dispose();
        CancelPrefetch();
        _lifetime.Cancel();
        _probe.Dispose();
        _shell.Dispose();
        _background.Dispose();
        _preview.Dispose();
    }
}

/// <summary>固定数量的 STA 线程，执行 Shell COM 调用。按提交顺序执行（可视区从上到下提交），已取消的任务出队时直接跳过。</summary>
internal sealed class StaWorkerPool : IDisposable
{
    private readonly LinkedList<Action> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();

    public StaWorkerPool(int workers, string name, ThreadPriority priority)
    {
        for (var i = 0; i < workers; i++)
        {
            var thread = new Thread(Work) { IsBackground = true, Name = $"{name}-{i + 1}", Priority = priority };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    public Task<T?> Run<T>(Func<T?> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested || _shutdown.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
            return completion.Task;
        }
        lock (_queue)
        {
            _queue.AddLast(() =>
            {
                if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); return; }
                try { completion.TrySetResult(work()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
        }
        _signal.Release();
        return completion.Task;
    }

    private void Work()
    {
        try
        {
            while (true)
            {
                _signal.Wait(_shutdown.Token);
                Action? item;
                lock (_queue)
                {
                    if (_queue.First is null) continue;
                    item = _queue.First.Value;
                    _queue.RemoveFirst();
                }
                try { item(); } catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => _shutdown.Cancel();
}
