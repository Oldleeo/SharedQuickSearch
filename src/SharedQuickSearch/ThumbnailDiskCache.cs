using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SharedQuickSearch;

/// <summary>
/// 本机缩略图缓存：%LocalAppData%\Oldlee\共享速搜\thumbs\ab\ab12…ef.sqt
/// 键 = 完整路径 + 修改时间 + 大小，文件一改自然失效，不需要比对内容。
/// 命中时完全不访问共享盘。放 Local 而不是 Roaming，避免漫游配置把几个 GB 同步来同步去。
///
/// .sqt 格式（自定义，16 字节头）：
///   "SQT1" | 原图宽 int32 | 原图高 int32 | 标志 int32（bit0 = 低清待精修） | JPEG 或 PNG 字节
/// </summary>
internal sealed class ThumbnailDiskCache
{
    private static readonly byte[] Magic = "SQT1"u8.ToArray();
    private static readonly TimeSpan TouchAfter = TimeSpan.FromDays(3);
    private readonly string _folder;
    private long _limitBytes;
    private long _sizeBytes = -1;
    private int _trimming;

    public ThumbnailDiskCache(string folder, long limitBytes)
    {
        _folder = folder;
        _limitBytes = Math.Max(64L * 1024 * 1024, limitBytes);
    }

    public string Folder => _folder;
    public long LimitBytes { get => Interlocked.Read(ref _limitBytes); set => Interlocked.Exchange(ref _limitBytes, Math.Max(64L * 1024 * 1024, value)); }
    public bool IsFull { get { var size = Interlocked.Read(ref _sizeBytes); return size >= 0 && size >= LimitBytes; } }

    public static string KeyFor(FileEntry entry)
    {
        var text = $"{entry.FullPath.ToLowerInvariant()}|{entry.LastWriteUtc.Ticks}|{entry.Size}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private string PathFor(string key) => Path.Combine(_folder, key[..2], key + ".sqt");
    private string NegativePathFor(string key) => Path.Combine(_folder, key[..2], key + ".none");

    /// <summary>有缓存图，或者已确认这个版本的文件做不出缩略图。</summary>
    public bool Contains(string key) => File.Exists(PathFor(key)) || File.Exists(NegativePathFor(key));

    public bool IsNegative(string key) => File.Exists(NegativePathFor(key));

    /// <summary>记下“所有层都试过仍失败”，文件一改（键变化）自动失效。</summary>
    public void WriteNegative(string key)
    {
        try
        {
            var path = NegativePathFor(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Array.Empty<byte>());
        }
        catch { }
    }

    /// <summary>精修失败或不值得精修：把低清标志原地改成 0，以后不再整份读取。</summary>
    public void MarkFinal(string key)
    {
        try
        {
            using var stream = new FileStream(PathFor(key), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 16) return;
            stream.Position = 12;
            stream.Write(BitConverter.GetBytes(0), 0, 4);
        }
        catch { }
    }

    public ThumbResult? TryRead(string key)
    {
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path)) return null;
            byte[] bytes;
            // 允许读的同时被新版本覆盖（File.Move overwrite）
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024))
            {
                bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < bytes.Length) return null;
            }
            var decoded = Unpack(bytes);
            if (decoded is null) { TryDelete(path); return null; }

            // 粗粒度 LRU：常用的缓存隔几天“碰一下”，清理时按修改时间淘汰最久没用的。
            try
            {
                var info = new FileInfo(path);
                if (DateTime.UtcNow - info.LastWriteTimeUtc > TouchAfter) info.LastWriteTimeUtc = DateTime.UtcNow;
            }
            catch { }
            return decoded;
        }
        catch { return null; }
    }

    /// <summary>.sqt 文件内容 → 缩略图。格式不对返回 null。本机缓存和全组缓存共用这个格式。</summary>
    internal static ThumbResult? Unpack(byte[] bytes)
    {
        if (bytes.Length <= 16 || !bytes.AsSpan(0, 4).SequenceEqual(Magic)) return null;
        var width = BitConverter.ToInt32(bytes, 4);
        var height = BitConverter.ToInt32(bytes, 8);
        var flags = BitConverter.ToInt32(bytes, 12);
        var image = ImageCodec.Decode(bytes, 16, bytes.Length - 16);
        return image is null ? null : new ThumbResult(image, width, height, (flags & 1) != 0);
    }

    /// <summary>缩略图 → .sqt 文件内容。</summary>
    internal static byte[] Pack(ThumbResult result)
    {
        var encoded = ImageCodec.Encode(result.Image!);
        var data = new byte[16 + encoded.Length];
        Magic.CopyTo(data, 0);
        BitConverter.GetBytes(result.Width).CopyTo(data, 4);
        BitConverter.GetBytes(result.Height).CopyTo(data, 8);
        BitConverter.GetBytes(result.LowQuality ? 1 : 0).CopyTo(data, 12);
        encoded.CopyTo(data, 16);
        return data;
    }

    public void Write(string key, ThumbResult result)
    {
        if (result.Image is null) return;
        string? temporary = null;
        try
        {
            var data = Pack(result);

            var path = PathFor(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existed = File.Exists(path);
            temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            if (!existed && Interlocked.Read(ref _sizeBytes) >= 0)
            {
                var size = Interlocked.Add(ref _sizeBytes, data.Length);
                // 超出上限 10% 就在后台清一次，不让缓存一直涨到下次启动
                if (size > LimitBytes * 1.1 && Interlocked.CompareExchange(ref _trimming, 1, 0) == 0)
                    _ = Task.Run(() => { try { Trim(); } finally { Volatile.Write(ref _trimming, 0); } });
            }
        }
        catch { if (temporary is not null) TryDelete(temporary); }
    }

    public (long Bytes, int Files) Measure()
    {
        long bytes = 0;
        var files = 0;
        try
        {
            if (!Directory.Exists(_folder)) return (0, 0);
            foreach (var file in OwnedFiles().Where(file => file.Extension.Equals(".sqt", StringComparison.OrdinalIgnoreCase)))
            {
                bytes += file.Length;
                files++;
            }
        }
        catch { }
        Interlocked.Exchange(ref _sizeBytes, bytes);
        return (bytes, files);
    }

    /// <summary>超过上限时删掉最久没用的，删到上限的 85%。</summary>
    public void Trim()
    {
        try
        {
            if (!Directory.Exists(_folder)) { Interlocked.Exchange(ref _sizeBytes, 0); return; }
            var files = OwnedFiles().ToList();
            // 只清理一分钟前的临时文件：会话中途也会 Trim，不能删掉正在写的那个
            var staleBefore = DateTime.UtcNow.AddMinutes(-1);
            foreach (var stale in files.Where(x => x.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) && x.LastWriteTimeUtc < staleBefore)) TryDelete(stale.FullName);
            files.RemoveAll(x => !x.Extension.Equals(".sqt", StringComparison.OrdinalIgnoreCase) && !x.Extension.Equals(".none", StringComparison.OrdinalIgnoreCase));
            var total = files.Sum(x => x.Length);
            var limit = LimitBytes;
            if (total > limit)
            {
                var target = (long)(limit * 0.85);
                foreach (var file in files.OrderBy(x => x.LastWriteTimeUtc))
                {
                    if (total <= target) break;
                    var length = file.Length;
                    if (TryDelete(file.FullName)) total -= length;
                }
            }
            Interlocked.Exchange(ref _sizeBytes, total);
        }
        catch { }
    }

    public void Clear()
    {
        // Custom cache locations may contain unrelated files. Remove only our hashed cache files,
        // and never follow a junction or symlink into another directory.
        try { foreach (var file in OwnedFiles()) TryDelete(file.FullName); } catch { }
        Interlocked.Exchange(ref _sizeBytes, 0);
    }

    private IEnumerable<FileInfo> OwnedFiles()
    {
        if (!Directory.Exists(_folder) || (File.GetAttributes(_folder) & FileAttributes.ReparsePoint) != 0) yield break;
        foreach (var shard in new DirectoryInfo(_folder).EnumerateDirectories())
        {
            if ((shard.Attributes & FileAttributes.ReparsePoint) != 0 || !Regex.IsMatch(shard.Name, "^[a-fA-F0-9]{2}$")) continue;
            foreach (var file in shard.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    Regex.IsMatch(file.Name, "^[a-fA-F0-9]{40}\\.(sqt|none)(\\.[a-fA-F0-9]{32}\\.tmp)?$", RegexOptions.IgnoreCase) &&
                    file.Name.StartsWith(shard.Name, StringComparison.OrdinalIgnoreCase)) yield return file;
            }
        }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; } catch { return false; }
    }
}
