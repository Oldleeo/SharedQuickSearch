using System.IO;
using System.Text;

namespace SharedQuickSearch;

/// <summary>
/// 自动找全组共用的 .共享速搜缓存：公司里大家用同一个共享盘，就该用同一个缓存文件夹，不用每人手动填。
///
/// 找的顺序（每个网络索引目录都按这个顺序看一遍）：
///   共享根目录（\\NAS\设计部） → 往下一级一级 → 索引目录本身。
/// 先看哪里已经有了（同事建过就用同事的）；全都没有，再按同样的顺序找第一个能建文件夹的地方新建一个。
/// 共享根目录排第一：大家的索引目录可能不一样，但通常在同一个共享根下面，这样最容易用到同一个。
/// 每个位置最多等 4 秒，共享盘离线时不会卡住。只在后台线程调用。
/// </summary>
public static class TeamCacheLocator
{
    private static readonly TimeSpan PerCheck = TimeSpan.FromSeconds(4);

    /// <summary>候选的“父目录”（缓存文件夹会建在它下面），按优先顺序；本机磁盘上的索引目录不参与。</summary>
    public static IReadOnlyList<string> Candidates(IEnumerable<string> roots)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perRoot = new List<List<string>>();
        foreach (var root in roots)
        {
            // 非 Windows（只有后端测试会跑到）上 Normalize 会把反斜杠换成 /，这里保持原样
            var unc = UncPaths.ToUnc(OperatingSystem.IsWindows() ? PathRules.Normalize(root) : root.Trim().TrimEnd('\\'));
            if (unc is null || UncPaths.ShareRoot(unc) is not { } share) continue;
            var chain = new List<string> { share };
            var rest = unc.TrimEnd('\\').Length > share.Length ? unc.TrimEnd('\\')[(share.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
            var current = share;
            foreach (var part in rest) { current += @"\" + part; chain.Add(current); }
            perRoot.Add(chain);
        }
        // 先把所有共享根目录排在前面，再一层一层往下
        for (var depth = 0; perRoot.Any(x => x.Count > depth); depth++)
            foreach (var chain in perRoot)
                if (chain.Count > depth && seen.Add(chain[depth])) result.Add(chain[depth]);
        return result;
    }

    /// <summary>找已经存在的缓存文件夹；没有就建一个（create=true 时）。都不行返回 null。</summary>
    /// <param name="skip">不要这个（上次用它时没权限）。</param>
    public static Task<string?> FindOrCreateAsync(IEnumerable<string> roots, bool create, string? skip = null) =>
        FindOrCreateInAsync(Candidates(roots).Where(parent => skip is null
            || !string.Equals(Path.Combine(parent, PathRules.TeamCacheFolderName).TrimEnd('\\'), skip.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList(), create);

    /// <summary>在给定的父目录里找 / 建（测试也用这个）。</summary>
    public static async Task<string?> FindOrCreateInAsync(IReadOnlyList<string> parents, bool create)
    {
        foreach (var parent in parents)
        {
            var folder = Path.Combine(parent, PathRules.TeamCacheFolderName);
            if (await WithTimeout(() => Directory.Exists(folder)).ConfigureAwait(false) == true) return folder;
        }
        if (!create) return null;
        foreach (var parent in parents)
        {
            var folder = Path.Combine(parent, PathRules.TeamCacheFolderName);
            var created = await WithTimeout(() =>
            {
                if (!Directory.Exists(parent)) return false;
                try
                {
                    var info = Directory.CreateDirectory(folder);
                    try { info.Attributes |= FileAttributes.Hidden; } catch { }
                    WriteReadme(folder);
                    return true;
                }
                catch (UnauthorizedAccessException) { return false; } // 这一层不能建，试下一层
                catch (IOException) { return Directory.Exists(folder); }
            }).ConfigureAwait(false);
            if (created == true) return folder;
        }
        return null;
    }

    public static void WriteReadme(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "这是什么.txt");
            if (File.Exists(path)) return;
            File.WriteAllText(path,
                "这是“共享速搜”的全组共用缓存。\r\n" +
                "thumbs-v1：缩略图（每张大约 20 KB），没有原文件。\r\n" +
                "index-v1：同事扫好的索引（文件名、路径、大小、修改时间），新电脑装好后直接用，不用再扫一遍。\r\n" +
                "删掉不会丢任何素材，只是大家下次看图、新电脑建索引要重新来。\r\n" +
                "共享速搜只往这个文件夹里写，共享盘上别的文件一律只读。\r\n", Encoding.UTF8);
        }
        catch { }
    }

    private static async Task<bool?> WithTimeout(Func<bool> work)
    {
        var task = Task.Run(() => { try { return work(); } catch { return false; } });
        var finished = await Task.WhenAny(task, Task.Delay(PerCheck)).ConfigureAwait(false);
        return finished == task ? await task.ConfigureAwait(false) : null;
    }
}
