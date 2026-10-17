using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SharedQuickSearch;

public sealed partial class FileEntry : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string ParentPath { get; set; } = "";
    public string Extension { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double DurationSeconds { get; set; }
    public string RootPath { get; set; } = "";
    public bool IsOffline { get; set; }

    // 以下均为界面派生属性：[JsonIgnore] 保证不写进索引文件。
    [JsonIgnore] public string TypeLabel => IsDirectory ? "文件夹" : FileTypes.GetTypeLabel(Extension);
    [JsonIgnore] public string SizeLabel => IsDirectory ? "—" : FormatSize(Size);
    [JsonIgnore] public string ModifiedLabel => LastWriteUtc == DateTime.MinValue ? "—" : LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore] public string ResolutionLabel => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "—";
    [JsonIgnore] public string DurationLabel => DurationSeconds <= 0 ? "—" : TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    [JsonIgnore] public string IconGlyph => IsDirectory ? FileTypes.FolderGlyph : FileTypes.GetGlyph(Extension);
    [JsonIgnore] public string ExtensionLabel => IsDirectory ? "" : FileTypes.GetBadge(Extension);
    [JsonIgnore] public string CardMeta => IsDirectory
        ? (LastWriteUtc == DateTime.MinValue ? "文件夹" : $"文件夹 · {LastWriteUtc.ToLocalTime():yyyy-MM-dd}")
        : LastWriteUtc == DateTime.MinValue ? FormatSize(Size) : $"{FormatSize(Size)} · {LastWriteUtc.ToLocalTime():MM-dd HH:mm}";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024:F1} MB";
        return $"{bytes / 1024d / 1024 / 1024:F1} GB";
    }
}

public static partial class FileTypes
{
    public static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic" };
    public static readonly HashSet<string> Design = new(StringComparer.OrdinalIgnoreCase)
        { ".psd", ".psb", ".ai", ".eps", ".svg", ".cdr", ".indd" };
    public static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".json", ".xml" };
    public static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".webm" };
    public static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".csv", ".json", ".xml", ".log", ".ini", ".yaml", ".yml" };

    public static bool Matches(FileEntry item, SearchCategory category) => category switch
    {
        SearchCategory.Images => !item.IsDirectory && Images.Contains(item.Extension),
        SearchCategory.Design => !item.IsDirectory && Design.Contains(item.Extension),
        SearchCategory.Documents => !item.IsDirectory && Documents.Contains(item.Extension),
        SearchCategory.Videos => !item.IsDirectory && Videos.Contains(item.Extension),
        SearchCategory.Folders => item.IsDirectory,
        _ => true
    };

    public static string GetTypeLabel(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" => "图片",
        ".psd" or ".psb" => "Photoshop",
        ".ai" or ".eps" => "Illustrator",
        ".pdf" => "PDF",
        ".doc" or ".docx" => "Word",
        ".xls" or ".xlsx" => "Excel",
        ".ppt" or ".pptx" => "PowerPoint",
        ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".m4v" or ".webm" => "视频",
        ".zip" or ".rar" or ".7z" => "压缩包",
        "" => "文件",
        _ => extension.TrimStart('.').ToUpperInvariant()
    };

    // Segoe Fluent Icons / MDL2 Assets 字形
    public const string FolderGlyph = "\uE8B7";
    public static string GetGlyph(string extension)
    {
        if (Images.Contains(extension)) return "\uEB9F";
        if (Design.Contains(extension)) return "\uE790";
        if (Videos.Contains(extension)) return "\uE714";
        if (Documents.Contains(extension)) return "\uE8A5";
        if (extension.ToLowerInvariant() is ".zip" or ".rar" or ".7z") return "\uF012";
        return "\uE7C3";
    }

    public static string GetBadge(string extension)
    {
        var value = extension.TrimStart('.').ToUpperInvariant();
        return value.Length == 0 ? "FILE" : value.Length > 5 ? value[..5] : value;
    }
}

public static class SkuDetector
{
    private static readonly Regex Pattern = new(@"(?<![A-Za-z0-9])(?<sku>[A-Za-z]{0,5}\d{1,8}(?:[-_ .]\d{1,8})+)(?![A-Za-z0-9])", RegexOptions.Compiled);
    public static string? Find(string value) => Pattern.Match(value) is { Success: true } match ? match.Groups["sku"].Value.Replace('_', '-') : null;
}
