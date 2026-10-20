using System.Text.RegularExpressions;

namespace SharedQuickSearch;

public static partial class PathRules
{
    /// <summary>Windows 上统一成反斜杠（UNC 保留开头两个）；其它系统（只用于跑后端测试）保持正斜杠。</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var value = input.Trim().Trim('"', '\'', '“', '”', '‘', '’');
        if (!OperatingSystem.IsWindows())
        {
            value = value.Replace('\\', '/');
            while (value.Contains("//", StringComparison.Ordinal)) value = value.Replace("//", "/");
            return value.Length > 1 ? value.TrimEnd('/') : value;
        }

        // 只换真正的分隔符。全角的 ／ ＼ 在 Windows 文件名里是合法字符，中文命名常用，不能动
        value = value.Replace('/', '\\');
        if (value.StartsWith("\\"))
        {
            value = @"\\" + RepeatedSlash().Replace(value.TrimStart('\\'), @"\");
        }
        else
        {
            value = RepeatedSlash().Replace(value, @"\");
        }

        if (value.Length > 3) value = value.TrimEnd('\\');
        return value;
    }

    public static char Separator => OperatingSystem.IsWindows() ? '\\' : '/';

    public static bool IsSameOrChild(string candidate, string parent)
    {
        var child = Normalize(candidate);
        var root = Normalize(parent);
        return root.Length > 0 && (child.Equals(root, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(root + Separator, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsExcluded(string path, IEnumerable<string> exclusions) =>
        exclusions.Any(exclusion => IsSameOrChild(path, exclusion));

    public static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string> paths) => paths
        .Select(Normalize).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string CompactForSearch(string value) =>
        value.Replace("-", "", StringComparison.Ordinal)
             .Replace("_", "", StringComparison.Ordinal)
             .Replace(" ", "", StringComparison.Ordinal)
             .ToLowerInvariant();

    [GeneratedRegex(@"\\{2,}")]
    private static partial Regex RepeatedSlash();
}
