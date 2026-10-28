using System.IO;
using System.Text.Json;

namespace SharedQuickSearch;

public sealed class AppSettings
{
    public List<string> IndexRoots { get; set; } = new();
    public List<string> ExcludedPaths { get; set; } = new();
    public int ResultLimit { get; set; } = 500;
    public double WindowWidth { get; set; } = 1380;
    public double WindowHeight { get; set; } = 820;
    public double PreviewWidth { get; set; } = 390;
    public bool AutoStart { get; set; }
    public string GlobalHotkey { get; set; } = "Ctrl+Alt+Space";
    public string ResultView { get; set; } = "LargeIcons";
    public string SortMode { get; set; } = "Relevance";
    public bool MinimizeToTray { get; set; } = true;
    public string Theme { get; set; } = "System";
    public bool PreviewVisible { get; set; } = true;
    public int UiRevision { get; set; }
    public bool ThumbnailPrefetch { get; set; } = true;
    public int ThumbnailCacheLimitMb { get; set; } = 2048;
    /// <summary>"Spotlight"：快捷键/打开软件只出现搜索条；"Window"：直接打开完整窗口。</summary>
    public string LaunchMode { get; set; } = "Spotlight";
    /// <summary>呼出时如果剪贴板里是一个 SKU，自动填进搜索框。只在本机读取，不上传。</summary>
    public bool ClipboardPrefill { get; set; } = true;
    public List<string> RecentQueries { get; set; } = new();
    public List<string> RecentFiles { get; set; } = new();
    /// <summary>首次引导（添加共享盘目录、排除文件夹、选项）是否完成。</summary>
    public bool OnboardingDone { get; set; }
    /// <summary>更新源：空 = GitHub Releases；也可以填自己服务器上 update.json 的 https 地址，或共享盘上的 update.json 路径。</summary>
    public string UpdateFeed { get; set; } = "";
    /// <summary>本机缩略图缓存放哪：空 = %LocalAppData%\Oldlee\共享速搜\thumbs；否则是选的文件夹下的“共享速搜缩略图”。</summary>
    public string ThumbnailCacheFolder { get; set; } = "";
    /// <summary>全组共用缩略图缓存：共享盘上的 .共享速搜缓存 文件夹；空 = 不用。</summary>
    public string TeamCacheFolder { get; set; } = "";
    /// <summary>双击 Ctrl 呼出（需要全局键盘钩子，默认关）。</summary>
    public bool DoubleCtrlSummon { get; set; }

    public AppSettings Clone() => new()
    {
        IndexRoots = IndexRoots.ToList(),
        ExcludedPaths = ExcludedPaths.ToList(),
        ResultLimit = ResultLimit,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        PreviewWidth = PreviewWidth,
        AutoStart = AutoStart,
        GlobalHotkey = GlobalHotkey,
        ResultView = ResultView,
        SortMode = SortMode,
        MinimizeToTray = MinimizeToTray,
        Theme = Theme,
        PreviewVisible = PreviewVisible,
        UiRevision = UiRevision,
        ThumbnailPrefetch = ThumbnailPrefetch,
        ThumbnailCacheLimitMb = ThumbnailCacheLimitMb,
        LaunchMode = LaunchMode,
        ClipboardPrefill = ClipboardPrefill,
        RecentQueries = RecentQueries.ToList(),
        RecentFiles = RecentFiles.ToList(),
        OnboardingDone = OnboardingDone,
        UpdateFeed = UpdateFeed,
        ThumbnailCacheFolder = ThumbnailCacheFolder,
        TeamCacheFolder = TeamCacheFolder,
        DoubleCtrlSummon = DoubleCtrlSummon
    };
}

public static class SettingsStore
{
    public static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Oldlee", "共享速搜");
    private static readonly string SettingsPath = Path.Combine(DataFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            value.IndexRoots = PathRules.NormalizeDistinct(value.IndexRoots).ToList();
            value.ExcludedPaths = PathRules.NormalizeDistinct(value.ExcludedPaths).ToList();
            return value;
        }
        catch { return new AppSettings(); }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataFolder);
        var normalized = settings.Clone();
        normalized.IndexRoots = PathRules.NormalizeDistinct(settings.IndexRoots).ToList();
        normalized.ExcludedPaths = PathRules.NormalizeDistinct(settings.ExcludedPaths).ToList();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
    }
}
