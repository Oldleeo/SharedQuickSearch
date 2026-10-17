using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SharedQuickSearch;

public enum SearchCategory { All, Images, Design, Documents, Videos, Folders }
public enum SearchSort { Relevance, Name, Modified, Size, Type, Path }

public enum QueryTokenKind { Extension, Category, MinSize, MaxSize, ModifiedAfter, Exclude }

/// <summary>搜索框里被识别成条件的一段文字（界面上显示成可删除的胶囊）。</summary>
public sealed record QueryToken(QueryTokenKind Kind, string Raw, string Label);

public sealed class ParsedQuery
{
    public string Raw { get; init; } = "";
    /// <summary>普通文字条件（已小写），每个都要命中。</summary>
    public IReadOnlyList<string> Terms { get; init; } = Array.Empty<string>();
    /// <summary>去掉空格、连字符、下划线后的整段文字（SKU 分隔符容错、拼音匹配用）。</summary>
    public string CompactText { get; init; } = "";
    public HashSet<string> Extensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public SearchCategory? Category { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTime? ModifiedAfterUtc { get; init; }
    public IReadOnlyList<string> Excludes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<QueryToken> Tokens { get; init; } = Array.Empty<QueryToken>();

    public bool HasText => Terms.Count > 0;
    public bool HasFilters => Extensions.Count > 0 || Category is not null || MinSize is not null || MaxSize is not null || ModifiedAfterUtc is not null || Excludes.Count > 0;
    public bool IsEmpty => !HasText && !HasFilters;
    /// <summary>只有纯文字条件时，继续往后输入的结果一定是上一次结果的子集，可以只在上次结果里筛。</summary>
    public bool IsPureText => HasText && !HasFilters;
    public string TextKey => string.Join(' ', Terms);
}

/// <summary>
/// 搜索语法：空格分隔，下面这些会被识别成条件，其余都是普通文字。
///   psd / .psd / *.psd     按扩展名（可写多个，任一命中）
///   图片 设计稿 文档 视频 文件夹   按分类
///   &gt;50mb  &lt;1mb  &gt;500k  &gt;2g   按大小
///   今天 昨天 本周 本月 7天 30d    按修改时间
///   -旧版                    排除含这个词的文件
/// </summary>
public static partial class QueryParser
{
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "psd", "psb", "ai", "eps", "svg", "cdr", "indd", "pdf",
        "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic",
        "mp4", "mov", "avi", "mkv", "wmv", "m4v", "webm",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "csv", "zip", "rar", "7z"
    };

    private static readonly Dictionary<string, (SearchCategory Category, string Label)> CategoryWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["图片"] = (SearchCategory.Images, "图片"), ["照片"] = (SearchCategory.Images, "图片"),
        ["设计稿"] = (SearchCategory.Design, "PSD · AI"), ["源文件"] = (SearchCategory.Design, "PSD · AI"),
        ["文档"] = (SearchCategory.Documents, "文档"),
        ["视频"] = (SearchCategory.Videos, "视频"),
        ["文件夹"] = (SearchCategory.Folders, "文件夹"), ["目录"] = (SearchCategory.Folders, "文件夹")
    };

    [GeneratedRegex(@"^([<>])(\d+(?:\.\d+)?)(k|kb|m|mb|g|gb)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"^(?:近)?(\d{1,3})(?:d|天)$", RegexOptions.IgnoreCase)]
    private static partial Regex DaysPattern();

    public static ParsedQuery Parse(string? query, DateTime? nowLocal = null)
    {
        var raw = (query ?? "").Trim();
        var now = nowLocal ?? DateTime.Now;
        var terms = new List<string>();
        var excludes = new List<string>();
        var tokens = new List<QueryToken>();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SearchCategory? category = null;
        long? minSize = null, maxSize = null;
        DateTime? after = null;

        foreach (var part in raw.Split(' ', '　', '\t'))
        {
            var word = part.Trim();
            if (word.Length == 0) continue;

            var ext = word.StartsWith("*.", StringComparison.Ordinal) ? word[2..] : word.StartsWith('.') ? word[1..] : word;
            if (KnownExtensions.Contains(ext))
            {
                extensions.Add("." + ext.ToLowerInvariant());
                tokens.Add(new QueryToken(QueryTokenKind.Extension, word, ext.ToUpperInvariant()));
                continue;
            }
            if (CategoryWords.TryGetValue(word, out var cat))
            {
                category = cat.Category;
                tokens.Add(new QueryToken(QueryTokenKind.Category, word, cat.Label));
                continue;
            }
            if (SizePattern().Match(word) is { Success: true } size)
            {
                var bytes = ToBytes(size.Groups[2].Value, size.Groups[3].Value);
                if (size.Groups[1].Value == ">") { minSize = bytes; tokens.Add(new QueryToken(QueryTokenKind.MinSize, word, $"> {FormatSize(bytes)}")); }
                else { maxSize = bytes; tokens.Add(new QueryToken(QueryTokenKind.MaxSize, word, $"< {FormatSize(bytes)}")); }
                continue;
            }
            if (TryParseDate(word, now, out var since, out var label))
            {
                after = since.ToUniversalTime();
                tokens.Add(new QueryToken(QueryTokenKind.ModifiedAfter, word, label));
                continue;
            }
            if (word.Length > 1 && (word[0] == '-' || word[0] == '－') && !char.IsDigit(word[1]))
            {
                var excluded = word[1..].ToLowerInvariant();
                excludes.Add(excluded);
                tokens.Add(new QueryToken(QueryTokenKind.Exclude, word, $"排除 {word[1..]}"));
                continue;
            }
            terms.Add(word.ToLowerInvariant());
        }

        return new ParsedQuery
        {
            Raw = raw,
            Terms = terms,
            CompactText = Compact(string.Concat(terms)),
            Extensions = extensions,
            Category = category,
            MinSize = minSize,
            MaxSize = maxSize,
            ModifiedAfterUtc = after,
            Excludes = excludes,
            Tokens = tokens
        };
    }

    /// <summary>从搜索文字里删掉一个已识别的条件（点胶囊上的 ×）。</summary>
    public static string RemoveToken(string query, QueryToken token)
    {
        // 和 Parse 用同样的分隔符（中文输入法的全角空格、Tab）
        var parts = query.Split(' ', '　', '\t').ToList();
        var index = parts.FindIndex(x => x == token.Raw);
        if (index >= 0) parts.RemoveAt(index);
        return string.Join(' ', parts.Where(x => x.Length > 0));
    }

    /// <summary>只保留字母和数字并小写：用于 SKU 分隔符容错和拼音匹配。</summary>
    public static string Compact(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is ' ' or '-' or '_' or '　' or '－' or '.') continue;
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static long ToBytes(string number, string unit)
    {
        var value = double.Parse(number, CultureInfo.InvariantCulture);
        var multiplier = unit.ToLowerInvariant() switch
        {
            "k" or "kb" => 1024d,
            "g" or "gb" => 1024d * 1024 * 1024,
            _ => 1024d * 1024 // 不写单位按 MB：设计文件说“大于 50”基本都指 MB
        };
        return (long)(value * multiplier);
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024 / 1024:0.#} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024:0.#} MB"
        : $"{bytes / 1024d:0.#} KB";

    private static bool TryParseDate(string word, DateTime now, out DateTime since, out string label)
    {
        var today = now.Date;
        switch (word)
        {
            case "今天": since = today; label = "今天"; return true;
            case "昨天": since = today.AddDays(-1); label = "昨天以来"; return true;
            case "本周":
                var offset = ((int)today.DayOfWeek + 6) % 7; // 周一为一周开始
                since = today.AddDays(-offset); label = "本周"; return true;
            case "本月": since = new DateTime(today.Year, today.Month, 1, 0, 0, 0, today.Kind); label = "本月"; return true;
        }
        if (DaysPattern().Match(word) is { Success: true } days && int.TryParse(days.Groups[1].Value, out var n) && n > 0)
        {
            since = today.AddDays(-(n - 1));
            label = $"近 {n} 天";
            return true;
        }
        since = default;
        label = "";
        return false;
    }
}
