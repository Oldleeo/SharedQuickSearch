using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace SharedQuickSearch;

/// <summary>
/// 共享盘路径统一成 \\服务器\共享\… 的写法：有人映射成 Z:\，有人直接用 \\NAS\，同一个文件要能对上
/// （全组缩略图缓存、共享索引都靠它）。本机磁盘返回 null。
/// </summary>
public static class UncPaths
{
    private static readonly ConcurrentDictionary<char, (string? Unc, DateTime At)> DriveMap = new();

    /// <summary>Z:\设计部\a.psd → \\NAS\共享\设计部\a.psd；已经是 \\ 开头的原样返回；本机盘返回 null。</summary>
    public static string? ToUnc(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? null : path;
        if (path.Length < 3 || path[1] != ':' || !char.IsLetter(path[0])) return null;
        var drive = char.ToUpperInvariant(path[0]);
        // 映射关系很少变：每个盘符 5 分钟问一次 Windows
        if (!DriveMap.TryGetValue(drive, out var mapped) || DateTime.UtcNow - mapped.At > TimeSpan.FromMinutes(5))
        {
            mapped = (QueryDriveUnc(drive), DateTime.UtcNow);
            DriveMap[drive] = mapped;
        }
        return mapped.Unc is null ? null : mapped.Unc.TrimEnd('\\') + path[2..];
    }

    /// <summary>\\NAS\设计部\产品图\2026 → \\NAS\设计部（共享根目录）。不是 UNC 路径返回 null。</summary>
    public static string? ShareRoot(string unc)
    {
        if (!unc.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var parts = unc.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : null;
    }

    /// <summary>
    /// 把路径开头的 from 换成 to（按路径段比较，不区分大小写）：
    /// Rewrite(@"\\NAS\设计部\a.psd", @"\\NAS\设计部", @"Z:\设计部") → @"Z:\设计部\a.psd"。不以 from 开头返回 null。
    /// </summary>
    public static string? Rewrite(string path, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || path.Length > 32767 ||
            path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0 ||
            path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..")) return null;
        from = from.TrimEnd('\\');
        to = to.TrimEnd('\\');
        if (path.Equals(from, StringComparison.OrdinalIgnoreCase)) return to;
        if (path.Length > from.Length && path[from.Length] == '\\' && path.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            return to + path[from.Length..];
        return null;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    private static string? QueryDriveUnc(char drive)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var length = 512;
            var buffer = new StringBuilder(length);
            return WNetGetConnection($"{drive}:", buffer, ref length) == 0 && buffer.Length > 2 ? buffer.ToString() : null;
        }
        catch { return null; }
    }
}
