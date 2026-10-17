using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharedQuickSearch;

// FileEntry / FileTypes 里依赖 WPF 的部分。其余部分在 Models.cs，是纯 .NET，可在任何平台编译测试。
public sealed partial class FileEntry
{
    [JsonIgnore]
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; Notify(); Notify(nameof(HasThumbnail)); }
    }
    [JsonIgnore]
    public bool HasThumbnail => Thumbnail is not null;
    private BitmapSource? _thumbnail;

    [JsonIgnore] public Brush TypeBrush => IsDirectory ? FileTypes.FolderBrush : FileTypes.GetBrush(Extension);
}

public static partial class FileTypes
{
    // 类型色：跟随各软件的品牌直觉（PS 蓝、AI 橙、PDF 红），只用在小标签上。
    public static readonly Brush FolderBrush = Frozen("#D69E2E");
    private static readonly Brush PhotoshopBrush = Frozen("#1C8FE6");
    private static readonly Brush IllustratorBrush = Frozen("#F08A00");
    private static readonly Brush PdfBrush = Frozen("#E0484D");
    private static readonly Brush PictureBrush = Frozen("#21A366");
    private static readonly Brush VideoBrush = Frozen("#8B5CF6");
    private static readonly Brush WordBrush = Frozen("#2F6FD6");
    private static readonly Brush ExcelBrush = Frozen("#1E8E4F");
    private static readonly Brush PowerPointBrush = Frozen("#D35230");
    private static readonly Brush ArchiveBrush = Frozen("#6B7280");
    private static readonly Brush OtherBrush = Frozen("#8A90A0");

    public static Brush GetBrush(string extension) => extension.ToLowerInvariant() switch
    {
        ".psd" or ".psb" => PhotoshopBrush,
        ".ai" or ".eps" => IllustratorBrush,
        ".pdf" => PdfBrush,
        ".doc" or ".docx" => WordBrush,
        ".xls" or ".xlsx" or ".csv" => ExcelBrush,
        ".ppt" or ".pptx" => PowerPointBrush,
        ".zip" or ".rar" or ".7z" => ArchiveBrush,
        var x when Images.Contains(x) || x is ".svg" or ".cdr" => PictureBrush,
        var x when Videos.Contains(x) => VideoBrush,
        _ => OtherBrush
    };

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
