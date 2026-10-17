using System.IO;
using System.Reflection;
using System.Text;

namespace SharedQuickSearch;

/// <summary>
/// 文件名转拼音，用来支持“bdt → 白底图”“baidi → 白底图”。
/// 数据表覆盖 CJK 基本区 20,892 个汉字（每字取最常用读音，由 pypinyin 生成），约 150 KB，嵌入程序。
/// 多音字只取一个读音：极少数名字可能要换一种拼法才搜得到，但不会搜出错的东西。
/// </summary>
public static class Pinyin
{
    private static Dictionary<char, string>? _table;
    private static readonly object Sync = new();

    public static void Initialize(Stream data)
    {
        var table = new Dictionary<char, string>(21000);
        using var reader = new StreamReader(data, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 2) continue;
            table[line[0]] = line[1..];
        }
        lock (Sync) _table = table;
    }

    private static Dictionary<char, string> Table
    {
        get
        {
            if (_table is not null) return _table;
            lock (Sync)
            {
                if (_table is not null) return _table;
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("pinyin.dat");
                    if (stream is not null) Initialize(stream);
                }
                catch { }
                return _table ??= new Dictionary<char, string>();
            }
        }
    }

    public static bool ContainsChinese(string text)
    {
        foreach (var c in text) if (c >= '一' && c <= '龥') return true;
        return false;
    }

    /// <summary>
    /// 返回 (全拼, 首字母)，都只含小写字母和数字；不含汉字时返回 null（英文名不需要额外匹配）。
    /// “14-5 白底图.jpg” → ("145baiditujpg", "145bdtjpg")
    /// </summary>
    public static (string Full, string Initials)? Convert(string text)
    {
        if (!ContainsChinese(text)) return null;
        var table = Table;
        var full = new StringBuilder(text.Length * 3);
        var initials = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var c = char.ToLowerInvariant(raw);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                full.Append(c);
                initials.Append(c);
            }
            else if (table.TryGetValue(raw, out var py) && py.Length > 0)
            {
                full.Append(py);
                initials.Append(py[0]);
            }
        }
        return (full.ToString(), initials.ToString());
    }

    /// <summary>查询词是否适合拿去比拼音：全是字母数字、且至少有一个字母。</summary>
    public static bool IsPinyinQuery(string compact)
    {
        if (compact.Length < 2) return false;
        var letter = false;
        foreach (var c in compact)
        {
            if (c is >= 'a' and <= 'z') letter = true;
            else if (c is not (>= '0' and <= '9')) return false;
        }
        return letter;
    }
}
