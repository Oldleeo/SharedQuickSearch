using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SharedQuickSearch;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>网格里的一张卡片。名字拆成 前 / 命中 / 后 三段，命中部分加粗。</summary>
public sealed class TileItem : Observable
{
    private bool _isSelected;

    public TileItem(FileEntry entry, int index, int generation, ParsedQuery query)
    {
        Entry = entry;
        Index = index;
        Generation = generation;
        (Pre, Hit, Post) = Highlight(entry.Name, query);
        Meta = entry.IsDirectory
            ? (entry.LastWriteUtc == DateTime.MinValue ? "文件夹" : $"文件夹 · {entry.LastWriteUtc.ToLocalTime():MM-dd}")
            : $"{entry.SizeLabel} · {(entry.LastWriteUtc == DateTime.MinValue ? "—" : entry.LastWriteUtc.ToLocalTime().ToString("MM-dd"))}";
    }

    public FileEntry Entry { get; }
    public int Index { get; }
    public int Generation { get; }
    public string Pre { get; }
    public string Hit { get; }
    public string Post { get; }
    public string Meta { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private static (string, string, string) Highlight(string name, ParsedQuery query)
    {
        foreach (var term in query.Terms.OrderByDescending(x => x.Length))
        {
            var at = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return (name[..at], name.Substring(at, term.Length), name[(at + term.Length)..]);
        }
        return (name, "", "");
    }
}

/// <summary>一行 5 张。按行虚拟化：上千个结果也只创建看得见的那几行。</summary>
public sealed class TileRow
{
    public TileRow(int index, IReadOnlyList<TileItem> items) { Index = index; Items = items; }
    public int Index { get; }
    public IReadOnlyList<TileItem> Items { get; }
}

public sealed class ChipItem : Observable
{
    private int _count;
    private bool _isActive;
    public ChipItem(string label, SearchCategory category) { Label = label; Category = category; }
    public string Label { get; }
    public SearchCategory Category { get; }
    public int Count { get => _count; set { Set(ref _count, value); Raise(nameof(CountText)); } }
    public string CountText => _count <= 0 ? "" : _count >= 10000 ? $"{_count / 1000}k" : _count.ToString("N0");
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
}

/// <summary>搜索框里被识别成条件的部分（或“来自剪贴板”），显示成可删除的胶囊。</summary>
public sealed class TokenChip
{
    public TokenChip(string label, QueryToken? token, bool isClipboard = false) { Label = label; Token = token; IsClipboard = isClipboard; }
    public string Label { get; }
    public QueryToken? Token { get; }
    public bool IsClipboard { get; }
}
