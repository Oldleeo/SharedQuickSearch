using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharedQuickSearch;

/// <summary>一份共享索引的说明：谁的、什么时候的、索引了哪些目录、排除了哪些。</summary>
public sealed record SharedIndexMeta(int Format, string AppVersion, DateTime CreatedUtc, int EntryCount, List<string> Roots, List<string> Exclusions);

/// <summary>本机的索引目录写法 ↔ 共享索引里统一的 \\服务器\共享 写法。</summary>
public sealed record RootMapping(string Local, string Shared);

/// <summary>
/// 共享索引（“共享起点”）：扫过的人把索引压缩一份放进 .共享速搜缓存\index-v1，
/// 新电脑第一次不用自己扫，下载这份几秒钟就能搜，之后照常自己核对和实时监听。
///
/// - 只有索引目录完全一样的才会用（按 \\服务器\共享 写法比对，Z:\ 和 \\NAS\ 算同一个）。
/// - 发布者排除的文件夹，使用者也必须排除了，否则少了那部分文件，宁可自己扫。
/// - 文件里的路径统一存成 \\服务器\共享 写法，下载后换回使用者自己的写法。
/// - 先写临时文件再改名：别人读的时候不会读到写了一半的。说明文件最后写，它在了才算发布完成。
/// - 同一份 6 小时内只发布一次，不会每次校准都往共享盘上写十几 MB。
/// 只在后台线程调用。
/// </summary>
public static class TeamIndexShare
{
    public const int Format = 1;
    public const string FolderName = "index-v1";
    private const long MaxCompressedBytes = 200L * 1024 * 1024;
    // 解压后最多读这么多、最多这么多条：防止一份坏掉的（或者被人放进来的）索引把内存吃光
    private const long MaxExpandedBytes = 600L * 1024 * 1024;
    private const int MaxEntries = 3_000_000;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>本机索引目录 → 共享写法；有一个目录在本机磁盘上（同事访问不到）就返回 null，不参与共享。</summary>
    public static IReadOnlyList<RootMapping>? MapRoots(IEnumerable<string> localRoots, Func<string, string?> toShared)
    {
        var result = new List<RootMapping>();
        foreach (var root in localRoots)
        {
            var shared = toShared(root);
            if (shared is null) return null;
            result.Add(new RootMapping(root.TrimEnd('\\'), shared.TrimEnd('\\')));
        }
        return result.Count == 0 ? null : result;
    }

    public static string Key(IEnumerable<string> paths)
    {
        var text = string.Join("|", paths.Select(x => x.TrimEnd('\\').ToLowerInvariant()).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
    }

    private static string Folder(string cacheFolder) => Path.Combine(cacheFolder, FolderName);
    private static string BaseName(IReadOnlyList<RootMapping> roots, IReadOnlyList<string> exclusions) =>
        $"{Key(roots.Select(x => x.Shared))}-{Key(exclusions)}";

    /// <summary>这一份最近 minAge 之内有人发布过，就不用再发。</summary>
    public static bool ShouldPublish(string cacheFolder, IReadOnlyList<RootMapping> roots, IReadOnlyList<string> sharedExclusions, TimeSpan minAge)
    {
        var meta = ReadMeta(Path.Combine(Folder(cacheFolder), BaseName(roots, sharedExclusions) + ".meta.json"));
        return meta is null || DateTime.UtcNow - meta.CreatedUtc >= minAge;
    }

    /// <summary>把本机索引发布出去。返回发布的条数（0 = 没发布）。</summary>
    public static int Publish(string cacheFolder, IReadOnlyCollection<FileEntry> entries, IReadOnlyList<RootMapping> roots, IReadOnlyList<string> sharedExclusions, string appVersion)
    {
        var shared = new List<FileEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (PathRules.IsInTeamCache(entry.FullPath)) continue;
            var root = roots.FirstOrDefault(x => UncPaths.Rewrite(entry.FullPath, x.Local, x.Shared) is not null);
            if (root is null) continue;
            var copy = Copy(entry);
            copy.FullPath = UncPaths.Rewrite(entry.FullPath, root.Local, root.Shared)!;
            if (sharedExclusions.Any(excluded => PathRules.IsSameOrChild(copy.FullPath, excluded))) continue;
            copy.ParentPath = UncPaths.Rewrite(entry.ParentPath, root.Local, root.Shared) ?? copy.ParentPath;
            copy.RootPath = root.Shared;
            shared.Add(copy);
        }
        if (shared.Count == 0) return 0;

        var folder = Folder(cacheFolder);
        Directory.CreateDirectory(folder);
        var name = BaseName(roots, sharedExclusions);
        var data = Path.Combine(folder, name + ".json.gz");
        var meta = Path.Combine(folder, name + ".meta.json");
        var temporary = $"{data}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                JsonSerializer.Serialize(gzip, shared);
            File.Move(temporary, data, overwrite: true);
            temporary = null;
        }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch { } }

        RemoveStale(folder, Key(roots.Select(x => x.Shared)), name);

        var info = new SharedIndexMeta(Format, appVersion, DateTime.UtcNow, shared.Count,
            roots.Select(x => x.Shared).ToList(), sharedExclusions.ToList());
        var metaTemporary = $"{meta}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(metaTemporary, JsonSerializer.Serialize(info), Encoding.UTF8);
        File.Move(metaTemporary, meta, overwrite: true);
        return shared.Count;
    }

    /// <summary>
    /// 找一份能用的共享索引：目录完全一样、发布者排除的我也排除了，取最新的。
    /// 路径换回本机写法后返回；没有能用的返回 null。
    /// </summary>
    public static (List<FileEntry> Entries, SharedIndexMeta Meta)? TryFetch(string cacheFolder, IReadOnlyList<RootMapping> roots, IReadOnlyList<string> mySharedExclusions)
    {
        var folder = Folder(cacheFolder);
        if (!Directory.Exists(folder)) return null;
        var rootsKey = Key(roots.Select(x => x.Shared));
        var wanted = roots.Select(x => x.Shared.ToLowerInvariant()).ToHashSet();

        (string Data, SharedIndexMeta Meta)? best = null;
        foreach (var metaPath in Directory.EnumerateFiles(folder, rootsKey + "-*.meta.json"))
        {
            var meta = ReadMeta(metaPath);
            if (meta is null || meta.Format != Format) continue;
            if (!meta.Roots.Select(x => x.TrimEnd('\\').ToLowerInvariant()).ToHashSet().SetEquals(wanted)) continue;
            // 发布者排除了、我却没排除的文件夹：那部分文件这份索引里没有，不能用
            if (!meta.Exclusions.All(excluded => mySharedExclusions.Any(mine => PathRules.IsSameOrChild(excluded, mine)))) continue;
            var data = metaPath[..^".meta.json".Length] + ".json.gz";
            if (!File.Exists(data)) continue;
            if (best is null || meta.CreatedUtc > best.Value.Meta.CreatedUtc) best = (data, meta);
        }
        if (best is null) return null;

        if (new FileInfo(best.Value.Data).Length > MaxCompressedBytes) return null;
        List<FileEntry>? shared;
        try
        {
            using var file = new FileStream(best.Value.Data, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var limited = new LimitedReadStream(gzip, MaxExpandedBytes);
            shared = JsonSerializer.Deserialize<List<FileEntry>>(limited, Json);
        }
        catch (InvalidDataException) { return null; } // 解压出来太大，或者文件坏了
        catch (JsonException) { return null; }
        if (shared is null || shared.Count > MaxEntries) return null;

        var local = new List<FileEntry>(shared.Count);
        foreach (var entry in shared)
        {
            if (entry is null) continue;
            var root = roots.FirstOrDefault(x => UncPaths.Rewrite(entry.FullPath, x.Shared, x.Local) is not null);
            if (root is null) continue;
            if (mySharedExclusions.Any(excluded => PathRules.IsSameOrChild(entry.FullPath, excluded)) || PathRules.IsInTeamCache(entry.FullPath)) continue;
            entry.FullPath = UncPaths.Rewrite(entry.FullPath, root.Shared, root.Local)!;
            var lastSeparator = entry.FullPath.LastIndexOf('\\');
            entry.ParentPath = lastSeparator >= 0 ? entry.FullPath[..lastSeparator] : root.Local;
            entry.Name = lastSeparator >= 0 ? entry.FullPath[(lastSeparator + 1)..] : entry.FullPath;
            entry.Extension = entry.IsDirectory ? "" : Path.GetExtension(entry.Name);
            entry.RootPath = root.Local;
            entry.IsOffline = false;
            local.Add(entry);
        }
        return (local, best.Value.Meta);
    }

    /// <summary>同一组目录、但排除规则不同的旧副本：30 天没人更新就删掉，免得越堆越多。</summary>
    private static void RemoveStale(string folder, string rootsKey, string current)
    {
        try
        {
            foreach (var meta in Directory.EnumerateFiles(folder, rootsKey + "-*.meta.json"))
            {
                var name = Path.GetFileName(meta)[..^".meta.json".Length];
                if (name == current) continue;
                var info = ReadMeta(meta);
                if (info is not null && DateTime.UtcNow - info.CreatedUtc < StaleAfter) continue;
                try { File.Delete(meta); } catch { }
                try { File.Delete(Path.Combine(folder, name + ".json.gz")); } catch { }
            }
            // 写到一半留下的临时文件（一天以上）
            foreach (var temporary in Directory.EnumerateFiles(folder, "*.tmp"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(temporary) > TimeSpan.FromDays(1)) try { File.Delete(temporary); } catch { }
        }
        catch { }
    }

    /// <summary>读超过上限就报错的只读流（解压炸弹保护）。</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read > limit) throw new InvalidDataException("共享索引解压后太大");
            return n;
        }
        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            _read += n;
            if (_read > limit) throw new InvalidDataException("共享索引解压后太大");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>
    /// 拿来的索引先过一遍权限：每个索引目录下前 maxDepth 层的文件夹，自己看不到的（NAS 按权限隐藏、或者已经删了），
    /// 连同里面所有东西一起去掉。没权限的文件夹一般就在这几层；更深的交给随后的完整扫描。
    /// exists 由调用方提供（后台线程、带超时）。返回过滤后的列表和去掉的文件夹数。
    /// </summary>
    public static (List<FileEntry> Entries, int HiddenFolders) FilterVisible(IReadOnlyList<FileEntry> entries, IEnumerable<string> roots, Func<string, bool> exists, int maxDepth = 2)
    {
        var rootList = roots.Select(x => x.TrimEnd('\\', '/')).ToList();
        int Depth(FileEntry entry)
        {
            var root = rootList.FirstOrDefault(r => entry.FullPath.Length > r.Length && entry.FullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase) && entry.FullPath[r.Length] is '\\' or '/');
            if (root is null) return int.MaxValue;
            return entry.FullPath[(root.Length + 1)..].Split('\\', '/').Length;
        }
        var hidden = entries.Where(x => x.IsDirectory && Depth(x) <= maxDepth).Select(x => x.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).AsParallel().WithDegreeOfParallelism(16)
            .Where(path => { try { return !exists(path); } catch { return true; } })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (hidden.Count == 0) return (entries as List<FileEntry> ?? entries.ToList(), 0);
        // 按祖先查集合，避免海量条目 × 大量不可见目录的笛卡尔比较。
        bool Hidden(string path)
        {
            if (hidden.Contains(path)) return true;
            for (var i = path.Length - 1; i >= 0; i--)
                if (path[i] is '\\' or '/' && hidden.Contains(path[..i])) return true;
            return false;
        }
        var kept = entries.Where(x => !Hidden(x.FullPath)).ToList();
        return (kept, hidden.Count);
    }

    private static SharedIndexMeta? ReadMeta(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return null;
            var meta = JsonSerializer.Deserialize<SharedIndexMeta>(File.ReadAllText(path), Json);
            if (meta is null || meta.Format != Format || meta.Roots is null || meta.Exclusions is null ||
                meta.Roots.Count is 0 or > 128 || meta.Exclusions.Count > 4096 ||
                meta.Roots.Any(string.IsNullOrWhiteSpace) || meta.Exclusions.Any(string.IsNullOrWhiteSpace) ||
                meta.EntryCount is < 0 or > MaxEntries || meta.CreatedUtc > DateTime.UtcNow.AddMinutes(5)) return null;
            return meta;
        }
        catch { return null; }
    }

    private static FileEntry Copy(FileEntry entry) => new()
    {
        Name = entry.Name,
        FullPath = entry.FullPath,
        ParentPath = entry.ParentPath,
        Extension = entry.Extension,
        IsDirectory = entry.IsDirectory,
        Size = entry.Size,
        LastWriteUtc = entry.LastWriteUtc,
        CreatedUtc = entry.CreatedUtc,
        Width = entry.Width,
        Height = entry.Height,
        DurationSeconds = entry.DurationSeconds,
        RootPath = entry.RootPath
    };
}
