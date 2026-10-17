using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SharedQuickSearch;

public sealed record IndexProgress(
    int ScannedCount,
    string CurrentPath,
    double? Percent,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    double ItemsPerSecond);
public sealed record IndexBuildResult(int EntryCount, int SkippedCount, IReadOnlyList<string> Errors, TimeSpan Elapsed, int ReusedDirectories = 0);

/// <summary>一次搜索的完整结果：排好序的前 N 项、总命中数、各分类命中数、识别出的条件、耗时。</summary>
public sealed record SearchResponse(
    IReadOnlyList<FileEntry> Items,
    int TotalMatches,
    IReadOnlyDictionary<SearchCategory, int> CategoryCounts,
    ParsedQuery Query,
    TimeSpan Elapsed);

/// <summary>
/// 本机文件索引。
/// 并发模型：所有写操作（扫描提交、实时变更、排除规则）都经过同一把异步锁串行执行；
/// 扫描进行时到达的实时变更先记下来，扫描结果提交后再补上，不会丢也不会被覆盖。
/// 读（搜索）只拿不可变快照，不加锁、不等待。
/// 保存合并：变更后 4 秒内的多次修改只写一次文件；扫描完成立即写；退出前 FlushAsync。
/// </summary>
public sealed class FileIndexService : IDisposable
{
    private static readonly HashSet<string> BuiltInIgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db",
        "ehthumbs.db",
        "desktop.ini",
        ".DS_Store"
    };

    private const int ParallelThreshold = 20_000;
    private const int PrefixCacheLimit = 80_000;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(4);

    /// <summary>预先算好的搜索字段，条目不变就一直复用，不重复做小写和拼音。</summary>
    private sealed class SearchRecord
    {
        public SearchRecord(FileEntry entry)
        {
            Entry = entry;
            Name = entry.Name.ToLowerInvariant();
            // 只拿根目录以下的相对路径参与匹配：所有文件共有的 \\192.168.10.17\共享素材库 前缀没有区分度，
            // 还会让输入单个数字时命中全部文件。
            var full = entry.FullPath;
            var root = entry.RootPath;
            var relative = root.Length > 0 && full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full[root.Length..] : full;
            Path = relative.ToLowerInvariant();
            Compact = QueryParser.Compact(Name);
            var pinyin = Pinyin.Convert(entry.Name);
            PinyinFull = pinyin?.Full;
            PinyinInitials = pinyin?.Initials;
        }
        public FileEntry Entry { get; }
        public string Name { get; }
        public string Path { get; }
        public string Compact { get; }
        public string? PinyinFull { get; }
        public string? PinyinInitials { get; }
    }

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(new List<SearchRecord>());
        public Snapshot(List<SearchRecord> records)
        {
            Records = records;
            ByPath = new Dictionary<string, SearchRecord>(records.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var record in records) ByPath[record.Entry.FullPath] = record;
        }
        public List<SearchRecord> Records { get; }
        public Dictionary<string, SearchRecord> ByPath { get; }
    }

    private sealed record PrefixCache(Snapshot Snapshot, string TextKey, List<SearchRecord> Matches);

    private readonly string _indexPath;
    private readonly SemaphoreSlim _mutate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly HashSet<string> _deferred = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _saveTimer;
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private volatile PrefixCache? _prefixCache;
    private string[] _roots = Array.Empty<string>();
    private bool _rebuilding;
    private long _version;
    private long _savedVersion;

    public FileIndexService(string? indexPath = null)
    {
        _indexPath = indexPath ?? Path.Combine(SettingsStore.DataFolder, "file-index-v1.json");
        _saveTimer = new Timer(_ => _ = SaveIfDirtyAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int Count => _snapshot.Records.Count;

    /// <summary>当前索引条目快照（后台缩略图预热用）。</summary>
    public IReadOnlyList<FileEntry> SnapshotEntries() => _snapshot.Records.Select(x => x.Entry).ToArray();

    public bool TryGet(string path, out FileEntry? entry)
    {
        if (_snapshot.ByPath.TryGetValue(PathRules.Normalize(path), out var record)) { entry = record.Entry; return true; }
        entry = null;
        return false;
    }

    public void SetRoots(IEnumerable<string> roots) => _roots = PathRules.NormalizeDistinct(roots).OrderByDescending(x => x.Length).ToArray();

    // ================= 载入 / 保存 =================

    public async Task<int> LoadAsync(IReadOnlyCollection<string> exclusions)
    {
        try
        {
            if (!File.Exists(_indexPath)) return 0;
            List<FileEntry> entries;
            await using (var stream = File.OpenRead(_indexPath))
                entries = await JsonSerializer.DeserializeAsync<List<FileEntry>>(stream) ?? new();

            var originalCount = entries.Count;
            var normalizedExclusions = PathRules.NormalizeDistinct(exclusions);
            var records = await Task.Run(() => entries
                .Where(x => !PathRules.IsExcluded(x.FullPath, normalizedExclusions) && !IsBuiltInIgnored(x))
                .Select(x => new SearchRecord(x)).ToList());
            await _mutate.WaitAsync();
            try { Publish(new Snapshot(records)); }
            finally { _mutate.Release(); }
            if (records.Count != originalCount) await SaveNowAsync(CancellationToken.None);
            return records.Count;
        }
        catch
        {
            _snapshot = Snapshot.Empty;
            return 0;
        }
    }

    /// <summary>退出前调用：把还没落盘的变更写掉。</summary>
    public Task FlushAsync() => Task.Run(SaveIfDirtyAsync);

    private void Publish(Snapshot snapshot)
    {
        _snapshot = snapshot;
        _prefixCache = null;
        Interlocked.Increment(ref _version);
    }

    private void ScheduleSave() => _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);

    private async Task SaveIfDirtyAsync()
    {
        if (Interlocked.Read(ref _version) == Interlocked.Read(ref _savedVersion)) return;
        try { await SaveNowAsync(CancellationToken.None).ConfigureAwait(false); } catch { ScheduleSave(); }
    }

    private async Task SaveNowAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var version = Interlocked.Read(ref _version);
            var entries = _snapshot.Records.Select(x => x.Entry).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath) ?? SettingsStore.DataFolder);
            var temporaryPath = _indexPath + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _indexPath, true);
            Interlocked.Exchange(ref _savedVersion, version);
        }
        finally { _saveGate.Release(); }
    }

    // ================= 扫描 =================

    /// <param name="quick">
    /// 快速校准：目录修改时间和上次一样就直接沿用上次的子项，不再列目录内容。
    /// 新增、删除、改名都会改变所在目录的修改时间，所以能发现；只有“原地覆盖保存”不会，那部分由实时监听负责。
    /// 手动“刷新索引”用完整扫描（quick = false）。
    /// </param>
    public async Task<IndexBuildResult> RebuildAsync(
        IReadOnlyCollection<string> roots,
        IReadOnlyCollection<string> exclusions,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken,
        bool quick = false)
    {
        var started = DateTime.UtcNow;
        var normalizedRoots = PathRules.NormalizeDistinct(roots);
        var normalizedExclusions = PathRules.NormalizeDistinct(exclusions);
        SetRoots(normalizedRoots);

        await _mutate.WaitAsync(cancellationToken);
        _rebuilding = true;
        _mutate.Release();

        var previous = _snapshot;
        var entries = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var scanned = 0;
        var reused = 0;
        var processedDirectories = 0;
        var lastReportUtc = DateTime.MinValue;

        try
        {
            progress?.Report(new IndexProgress(0, "正在连接索引目录…", null, TimeSpan.Zero, null, 0));

            await Task.Run(() =>
            {
                // 快速校准要用：上次每个目录下有哪些子项
                Dictionary<string, List<FileEntry>>? childrenByParent = null;
                if (quick && previous.Records.Count > 0)
                {
                    childrenByParent = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var record in previous.Records)
                    {
                        var parent = PathRules.Normalize(record.Entry.ParentPath);
                        if (!childrenByParent.TryGetValue(parent, out var list)) childrenByParent[parent] = list = new List<FileEntry>();
                        list.Add(record.Entry);
                    }
                }

                foreach (var root in normalizedRoots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (PathRules.IsExcluded(root, normalizedExclusions)) { skipped++; continue; }
                    if (!Directory.Exists(root))
                    {
                        errors.Add($"无法访问：{root}");
                        failedPaths.Add(root);
                        continue;
                    }

                    var pending = new Stack<string>();
                    pending.Push(root);
                    while (pending.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var current = pending.Pop();
                        if (PathRules.IsExcluded(current, normalizedExclusions)) { skipped++; continue; }
                        processedDirectories++;

                        // 快速校准：目录没变，沿用上次的子项
                        if (childrenByParent is not null && !current.Equals(root, StringComparison.OrdinalIgnoreCase)
                            && previous.ByPath.TryGetValue(current, out var known) && known.Entry.IsDirectory
                            && childrenByParent.TryGetValue(current, out var children))
                        {
                            DateTime currentModified;
                            try { currentModified = Directory.GetLastWriteTimeUtc(current); } catch { currentModified = DateTime.MinValue; }
                            if (currentModified != DateTime.MinValue && currentModified == known.Entry.LastWriteUtc)
                            {
                                reused++;
                                foreach (var child in children)
                                {
                                    if (PathRules.IsExcluded(child.FullPath, normalizedExclusions) || IsBuiltInIgnored(child)) { skipped++; continue; }
                                    if (seen.Add(child.FullPath)) entries.Add(child);
                                    if (child.IsDirectory) pending.Push(child.FullPath);
                                    scanned++;
                                }
                                ReportMaybe(current);
                                continue;
                            }
                        }

                        try
                        {
                            var options = new EnumerationOptions
                            {
                                RecurseSubdirectories = false,
                                IgnoreInaccessible = true,
                                ReturnSpecialDirectories = false,
                                AttributesToSkip = FileAttributes.ReparsePoint
                            };
                            foreach (var item in new DirectoryInfo(current).EnumerateFileSystemInfos("*", options))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var fullPath = PathRules.Normalize(item.FullName);
                                if (PathRules.IsExcluded(fullPath, normalizedExclusions)) { skipped++; continue; }
                                if (IsBuiltInIgnored(item)) { skipped++; continue; }
                                if (seen.Add(fullPath))
                                {
                                    long size = 0;
                                    if (item is FileInfo file) { try { size = file.Length; } catch { } }
                                    entries.Add(CreateEntry(item, root, size, TryGetModified(item)));
                                }
                                if (item is DirectoryInfo) pending.Push(fullPath);
                                scanned++;
                                if (scanned % 100 == 0) ReportMaybe(fullPath);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            failedPaths.Add(current);
                            if (errors.Count < 20) errors.Add($"跳过 {current}：{ex.Message}");
                        }
                        ReportMaybe(current);

                        void ReportMaybe(string path)
                        {
                            if (DateTime.UtcNow - lastReportUtc < TimeSpan.FromMilliseconds(400)) return;
                            progress?.Report(CreateProgress(scanned, processedDirectories, pending.Count, started, path));
                            lastReportUtc = DateTime.UtcNow;
                        }
                    }
                }
            }, cancellationToken);

            // 访问失败的位置保留上次的记录，网络抖一下不会把索引清空
            if (failedPaths.Count > 0)
            {
                foreach (var record in previous.Records)
                {
                    var oldEntry = record.Entry;
                    if (PathRules.IsExcluded(oldEntry.FullPath, normalizedExclusions) || IsBuiltInIgnored(oldEntry)) continue;
                    if (!failedPaths.Any(failed => PathRules.IsSameOrChild(oldEntry.FullPath, failed))) continue;
                    if (seen.Add(oldEntry.FullPath)) entries.Add(oldEntry);
                }
            }

            // 复用没变的搜索记录（不重算小写、拼音）
            var records = await Task.Run(() => entries.Select(entry =>
                previous.ByPath.TryGetValue(entry.FullPath, out var old) && ReferenceEquals(old.Entry, entry) ? old : new SearchRecord(entry)).ToList(), cancellationToken);

            await _mutate.WaitAsync(CancellationToken.None);
            try
            {
                Publish(new Snapshot(records));
                _rebuilding = false;
                // 扫描期间到达的实时变更，在新结果上补做一遍
                if (_deferred.Count > 0)
                {
                    var deferred = _deferred.ToArray();
                    _deferred.Clear();
                    await Task.Run(() => ApplyCore(deferred, normalizedExclusions), CancellationToken.None);
                }
            }
            finally { _mutate.Release(); }

            await SaveNowAsync(CancellationToken.None);
            var elapsed = DateTime.UtcNow - started;
            progress?.Report(new IndexProgress(Count, "索引完成", 100, elapsed, TimeSpan.Zero,
                elapsed.TotalSeconds <= 0 ? 0 : Count / elapsed.TotalSeconds));
            return new IndexBuildResult(Count, skipped, errors, DateTime.UtcNow - started, reused);
        }
        finally
        {
            if (_rebuilding)
            {
                // 取消或出错：放弃这次扫描，但扫描期间记下的实时变更要应用到旧索引上
                await _mutate.WaitAsync(CancellationToken.None);
                try
                {
                    _rebuilding = false;
                    if (_deferred.Count > 0)
                    {
                        var deferred = _deferred.ToArray();
                        _deferred.Clear();
                        if (ApplyCore(deferred, normalizedExclusions) > 0) ScheduleSave();
                    }
                }
                finally { _mutate.Release(); }
            }
        }
    }

    // ================= 实时变更 =================

    public async Task<int> ApplyChangesAsync(IEnumerable<string> paths, IReadOnlyCollection<string> exclusions)
    {
        var normalized = paths.Select(PathRules.Normalize).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length == 0) return 0;
        var normalizedExclusions = PathRules.NormalizeDistinct(exclusions);
        await _mutate.WaitAsync();
        try
        {
            if (_rebuilding)
            {
                foreach (var path in normalized) _deferred.Add(path);
                return 0;
            }
            var changed = await Task.Run(() => ApplyCore(normalized, normalizedExclusions));
            if (changed > 0) ScheduleSave();
            return changed;
        }
        finally { _mutate.Release(); }
    }

    /// <summary>在当前快照上应用一批路径变化，生成新快照。必须在 _mutate 内调用。</summary>
    private int ApplyCore(IReadOnlyCollection<string> paths, IReadOnlyList<string> exclusions)
    {
        var current = _snapshot;
        var map = new Dictionary<string, SearchRecord>(current.ByPath, StringComparer.OrdinalIgnoreCase);
        var removedPrefixes = new List<string>();
        var changed = 0;

        foreach (var path in paths)
        {
            if (PathRules.IsExcluded(path, exclusions))
            {
                if (map.Remove(path, out var gone)) { changed++; if (gone.Entry.IsDirectory) removedPrefixes.Add(path); }
                continue;
            }

            FileSystemInfo? info = null;
            try { info = Directory.Exists(path) ? new DirectoryInfo(path) : File.Exists(path) ? new FileInfo(path) : null; } catch { }
            if (info is null)
            {
                // 删除：文件 O(1)；目录记下前缀，最后一次扫描统一去掉子项
                if (map.Remove(path, out var gone)) { changed++; if (gone.Entry.IsDirectory) removedPrefixes.Add(path); }
                else removedPrefixes.Add(path);
                continue;
            }
            if (IsBuiltInIgnored(info))
            {
                if (map.Remove(path)) changed++;
                continue;
            }

            var root = RootFor(path);
            var wasKnown = map.ContainsKey(path);
            var entry = CreateEntry(info, root, info is FileInfo f ? TryGetLength(f) : 0, TryGetModified(info));
            map[path] = new SearchRecord(entry);
            changed++;

            if (info is DirectoryInfo && !wasKnown)
            {
                // 新出现的目录（例如整个文件夹拷进来）：把里面的内容一起收进来
                var pending = new Stack<string>();
                pending.Push(path);
                while (pending.Count > 0)
                {
                    var folder = pending.Pop();
                    try
                    {
                        foreach (var child in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                        {
                            var childPath = PathRules.Normalize(child.FullName);
                            if (PathRules.IsExcluded(childPath, exclusions) || IsBuiltInIgnored(child)) continue;
                            map[childPath] = new SearchRecord(CreateEntry(child, root, child is FileInfo file ? TryGetLength(file) : 0, TryGetModified(child)));
                            changed++;
                            if (child is DirectoryInfo) pending.Push(childPath);
                        }
                    }
                    catch { }
                }
            }
        }

        if (removedPrefixes.Count > 0)
        {
            var prefixes = removedPrefixes.Select(x => x + PathRules.Separator).ToArray();
            var doomed = new List<string>();
            foreach (var key in map.Keys)
                foreach (var prefix in prefixes)
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { doomed.Add(key); break; }
            foreach (var key in doomed) map.Remove(key);
            changed += doomed.Count;
        }

        if (changed == 0) return 0;
        Publish(new Snapshot(map.Values.ToList()));
        return changed;
    }

    private string RootFor(string path)
    {
        foreach (var root in _roots)
            if (PathRules.IsSameOrChild(path, root)) return root;
        return "";
    }

    public async Task<int> ApplyExclusionsAsync(IReadOnlyCollection<string> exclusions)
    {
        var normalized = PathRules.NormalizeDistinct(exclusions);
        await _mutate.WaitAsync();
        try
        {
            var current = _snapshot;
            var filtered = current.Records.Where(x => !PathRules.IsExcluded(x.Entry.FullPath, normalized) && !IsBuiltInIgnored(x.Entry)).ToList();
            var removed = current.Records.Count - filtered.Count;
            if (removed > 0)
            {
                Publish(new Snapshot(filtered));
                ScheduleSave();
            }
            return removed;
        }
        finally { _mutate.Release(); }
    }

    public void UpdateRootAvailability(IEnumerable<string> roots)
    {
        var states = PathRules.NormalizeDistinct(roots).ToDictionary(x => x, x => { try { return Directory.Exists(x); } catch { return false; } }, StringComparer.OrdinalIgnoreCase);
        var ordered = states.Keys.OrderByDescending(x => x.Length).ToArray();
        foreach (var record in _snapshot.Records)
        {
            var root = ordered.FirstOrDefault(x => PathRules.IsSameOrChild(record.Entry.FullPath, x));
            if (root is null) continue;
            record.Entry.RootPath = root;
            record.Entry.IsOffline = !states[root];
        }
    }

    // ================= 搜索 =================

    public IReadOnlyList<FileEntry> Search(string? query, SearchCategory category, int limit,
        SearchSort sort = SearchSort.Relevance, bool descending = false) =>
        SearchDetailed(query, category, limit, sort, descending).Items;

    public SearchResponse SearchDetailed(string? query, SearchCategory category, int limit,
        SearchSort sort = SearchSort.Relevance, bool descending = false)
    {
        var watch = Stopwatch.StartNew();
        var parsed = QueryParser.Parse(query);
        var snapshot = _snapshot;
        limit = Math.Clamp(limit, 20, 5000);
        var effectiveCategory = parsed.Category ?? category;

        if (parsed.IsEmpty)
        {
            var recent = snapshot.Records.Where(x => FileTypes.Matches(x.Entry, effectiveCategory)).Select(x => new Ranked(x.Entry, 0));
            var items = Sort(recent, sort == SearchSort.Relevance ? SearchSort.Modified : sort, sort == SearchSort.Relevance || descending)
                .Take(Math.Min(limit, 100)).Select(x => x.Entry).ToArray();
            return new SearchResponse(items, items.Length, EmptyCounts, parsed, watch.Elapsed);
        }

        // 纯文字、而且是在上一次的基础上继续输入：只在上一次的命中里筛（打字越多越快）
        IEnumerable<SearchRecord> candidates = snapshot.Records;
        var cache = _prefixCache;
        if (parsed.IsPureText && cache is not null && ReferenceEquals(cache.Snapshot, snapshot)
            && parsed.TextKey.StartsWith(cache.TextKey, StringComparison.Ordinal))
        {
            candidates = cache.Matches;
        }

        var pinyinTerms = parsed.Terms.Select(t => { var c = QueryParser.Compact(t); return Pinyin.IsPinyinQuery(c) ? c : null; }).ToArray();
        var pinyinWhole = Pinyin.IsPinyinQuery(parsed.CompactText) ? parsed.CompactText : null;
        var source = candidates as List<SearchRecord> ?? candidates.ToList();
        List<SearchRecord> matches = source.Count >= ParallelThreshold
            ? source.AsParallel().Where(r => IsMatch(r, parsed, pinyinTerms, pinyinWhole)).ToList()
            : source.Where(r => IsMatch(r, parsed, pinyinTerms, pinyinWhole)).ToList();

        if (parsed.IsPureText && matches.Count <= PrefixCacheLimit)
            _prefixCache = new PrefixCache(snapshot, parsed.TextKey, matches);

        var counts = new Dictionary<SearchCategory, int>
        {
            [SearchCategory.All] = matches.Count,
            [SearchCategory.Images] = 0, [SearchCategory.Design] = 0, [SearchCategory.Documents] = 0,
            [SearchCategory.Videos] = 0, [SearchCategory.Folders] = 0
        };
        foreach (var record in matches)
        {
            var entry = record.Entry;
            if (entry.IsDirectory) { counts[SearchCategory.Folders]++; continue; }
            if (FileTypes.Images.Contains(entry.Extension)) counts[SearchCategory.Images]++;
            else if (FileTypes.Design.Contains(entry.Extension)) counts[SearchCategory.Design]++;
            if (FileTypes.Documents.Contains(entry.Extension)) counts[SearchCategory.Documents]++;
            if (FileTypes.Videos.Contains(entry.Extension)) counts[SearchCategory.Videos]++;
        }

        var joined = string.Join(' ', parsed.Terms);
        var ranked = matches
            .Where(x => FileTypes.Matches(x.Entry, effectiveCategory))
            .Select(x => new Ranked(x.Entry, Rank(x, parsed, joined)));
        var result = Sort(ranked, sort, descending).Take(limit).Select(x => x.Entry).ToArray();
        var total = effectiveCategory == SearchCategory.All ? matches.Count : counts[effectiveCategory];
        return new SearchResponse(result, total, counts, parsed, watch.Elapsed);
    }

    private static readonly IReadOnlyDictionary<SearchCategory, int> EmptyCounts = new Dictionary<SearchCategory, int>();

    private static bool IsMatch(SearchRecord r, ParsedQuery q, string?[] pinyinTerms, string? pinyinWhole)
    {
        var entry = r.Entry;
        foreach (var excluded in q.Excludes)
            if (r.Name.Contains(excluded, StringComparison.Ordinal) || (r.PinyinInitials?.Contains(excluded, StringComparison.Ordinal) ?? false)) return false;
        if (q.Extensions.Count > 0 && (entry.IsDirectory || !q.Extensions.Contains(entry.Extension))) return false;
        if (q.MinSize is { } min && (entry.IsDirectory || entry.Size < min)) return false;
        if (q.MaxSize is { } max && (entry.IsDirectory || entry.Size > max)) return false;
        if (q.ModifiedAfterUtc is { } after && entry.LastWriteUtc < after) return false;
        if (!q.HasText) return true;

        // 只输入一个字符时只看文件名：路径里到处都是单个数字和字母，匹配路径等于全部命中
        if (q.Terms.Count == 1 && q.Terms[0].Length == 1) return r.Name.Contains(q.Terms[0], StringComparison.Ordinal);

        var all = true;
        for (var i = 0; i < q.Terms.Count; i++)
        {
            var term = q.Terms[i];
            if (r.Name.Contains(term, StringComparison.Ordinal) || r.Path.Contains(term, StringComparison.Ordinal)) continue;
            if (pinyinTerms[i] is { } py && r.PinyinInitials is not null
                && (r.PinyinInitials.Contains(py, StringComparison.Ordinal) || r.PinyinFull!.Contains(py, StringComparison.Ordinal))) continue;
            all = false;
            break;
        }
        if (all) return true;

        // 整段去分隔符再比：14-5 能找到 SKU_14_5、14 5、145
        var compact = q.CompactText;
        if (compact.Length >= 2 && r.Compact.Contains(compact, StringComparison.Ordinal)) return true;
        if (pinyinWhole is not null && r.PinyinInitials is not null
            && (r.PinyinInitials.Contains(pinyinWhole, StringComparison.Ordinal) || r.PinyinFull!.Contains(pinyinWhole, StringComparison.Ordinal))) return true;
        return false;
    }

    private static int Rank(SearchRecord item, ParsedQuery q, string query)
    {
        if (!q.HasText) return 4;
        if (item.Name.Equals(query, StringComparison.Ordinal)) return 0;
        if (item.Name.StartsWith(query, StringComparison.Ordinal)) return 1;
        if (item.Name.Contains(query, StringComparison.Ordinal)) return 2;
        var inName = true;
        var inPath = true;
        foreach (var term in q.Terms)
        {
            if (item.Name.Contains(term, StringComparison.Ordinal)) continue;
            inName = false;
            if (!item.Path.Contains(term, StringComparison.Ordinal)) { inPath = false; break; }
        }
        if (inName) return 3;
        if (inPath) return 4;
        return 5; // 分隔符容错或拼音命中
    }

    private sealed record Ranked(FileEntry Entry, int Rank);

    private static IEnumerable<Ranked> Sort(IEnumerable<Ranked> source, SearchSort sort, bool descending)
    {
        IOrderedEnumerable<Ranked> ordered = sort switch
        {
            SearchSort.Name => descending ? source.OrderByDescending(x => x.Entry.Name, NaturalStringComparer.Current) : source.OrderBy(x => x.Entry.Name, NaturalStringComparer.Current),
            SearchSort.Modified => descending ? source.OrderByDescending(x => x.Entry.LastWriteUtc) : source.OrderBy(x => x.Entry.LastWriteUtc),
            SearchSort.Size => descending ? source.OrderByDescending(x => x.Entry.Size) : source.OrderBy(x => x.Entry.Size),
            SearchSort.Type => descending ? source.OrderByDescending(x => x.Entry.TypeLabel, StringComparer.CurrentCultureIgnoreCase) : source.OrderBy(x => x.Entry.TypeLabel, StringComparer.CurrentCultureIgnoreCase),
            SearchSort.Path => descending ? source.OrderByDescending(x => x.Entry.FullPath, StringComparer.CurrentCultureIgnoreCase) : source.OrderBy(x => x.Entry.FullPath, StringComparer.CurrentCultureIgnoreCase),
            _ => source.OrderBy(x => x.Rank).ThenBy(x => x.Entry.IsDirectory ? 0 : 1).ThenBy(x => x.Entry.Name.Length)
        };
        return ordered.ThenBy(x => x.Entry.Name, NaturalStringComparer.Current);
    }

    // ================= 工具 =================

    private static FileEntry CreateEntry(FileSystemInfo item, string root, long size, DateTime modified) => new()
    {
        Name = item.Name,
        FullPath = PathRules.Normalize(item.FullName),
        ParentPath = PathRules.Normalize(Path.GetDirectoryName(item.FullName) ?? root),
        Extension = item is DirectoryInfo ? "" : Path.GetExtension(item.Name),
        IsDirectory = item is DirectoryInfo,
        Size = size,
        LastWriteUtc = modified,
        CreatedUtc = TryGetCreated(item),
        RootPath = root
    };
    private static long TryGetLength(FileInfo file) { try { return file.Length; } catch { return 0; } }
    private static DateTime TryGetModified(FileSystemInfo info) { try { return info.LastWriteTimeUtc; } catch { return DateTime.MinValue; } }
    private static DateTime TryGetCreated(FileSystemInfo info) { try { return info.CreationTimeUtc; } catch { return DateTime.MinValue; } }
    private static bool IsBuiltInIgnored(FileSystemInfo info) => info is FileInfo && BuiltInIgnoredFileNames.Contains(info.Name);
    private static bool IsBuiltInIgnored(FileEntry entry) => !entry.IsDirectory && BuiltInIgnoredFileNames.Contains(entry.Name);

    private static IndexProgress CreateProgress(int scanned, int processedDirectories, int pendingDirectories, DateTime started, string currentPath)
    {
        var elapsed = DateTime.UtcNow - started;
        var rate = elapsed.TotalSeconds < 0.5 ? 0 : scanned / elapsed.TotalSeconds;
        if (processedDirectories < 2 || scanned < 20 || rate <= 0)
            return new IndexProgress(scanned, currentPath, null, elapsed, null, rate);
        var averageItemsPerDirectory = Math.Max(1, scanned / (double)processedDirectories);
        var estimatedRemainingItems = pendingDirectories * averageItemsPerDirectory;
        var estimatedTotal = scanned + estimatedRemainingItems;
        double? percent = estimatedTotal <= 0 ? null : Math.Clamp(scanned / estimatedTotal * 100, 1, 99);
        var remaining = TimeSpan.FromSeconds(Math.Clamp(estimatedRemainingItems / rate, 0, 24 * 60 * 60));
        return new IndexProgress(scanned, currentPath, percent, elapsed, remaining, rate);
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        // 在线程池上等，避免在界面线程上同步等待异步方法造成死锁
        try { Task.Run(SaveIfDirtyAsync).Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
