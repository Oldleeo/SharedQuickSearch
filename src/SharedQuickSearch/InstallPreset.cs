using System.IO;
using System.Text.Json;

namespace SharedQuickSearch;

/// <summary>
/// 安装预设：把“共享速搜.预设.json”和安装包放在同一个文件夹里，同事装完后的首次引导会自动填好
/// 共享盘目录、不录入的文件夹、缩略图缓存这些。没有这个文件就和平时一样手动填。
///
/// 只影响首次引导里预填的内容，用户在引导里还可以改；已经设置过的电脑不受影响。
/// 写法见仓库里的 examples/共享速搜.预设.json（可以写注释，字段都可以不写）。
/// </summary>
public sealed class InstallPreset
{
    public const string FileName = "共享速搜.预设.json";

    public List<string> IndexRoots { get; set; } = new();
    public List<string> ExcludedPaths { get; set; } = new();
    /// <summary>全组共用缓存的位置（共享盘上）。不写就自动找（找不到就建）。</summary>
    public string? TeamCacheFolder { get; set; }
    /// <summary>"off" = 不用全组缓存；不写 = 默认开（自动找）。</summary>
    public string? TeamCacheMode { get; set; }
    /// <summary>false = 这台电脑只用同事的索引，不发布自己的（给权限大的人用的安装包）。</summary>
    public bool? PublishSharedIndex { get; set; }
    /// <summary>本机缓存的位置；不写就是默认位置。</summary>
    public string? ThumbnailCacheFolder { get; set; }
    /// <summary>自建的更新源（https 地址或共享盘上的 update.json）。</summary>
    public string? UpdateFeed { get; set; }
    public bool? AutoStart { get; set; }
    public bool? ClipboardPrefill { get; set; }
    public bool? DoubleCtrlSummon { get; set; }

    /// <summary>安装时从安装包旁边带过来的预设，存在这里，引导时读。</summary>
    private static string SavedPath => Path.Combine(SettingsStore.DataFolder, "preset.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>安装成功后调用：安装包旁边有预设文件，就复制一份到本机设置目录。</summary>
    public static void CaptureFromInstaller()
    {
        try
        {
            var beside = BesideProcess();
            if (beside is null || Load(beside) is null) return;
            Directory.CreateDirectory(SettingsStore.DataFolder);
            File.Copy(beside, SavedPath, overwrite: true);
        }
        catch { }
    }

    /// <summary>先找程序旁边的（便携版直接运行），再找安装时带过来的。读不出来就当没有。</summary>
    public static InstallPreset? Find()
    {
        foreach (var path in new[] { BesideProcess(), SavedPath })
            if (path is not null && Load(path) is { } preset) return preset;
        return null;
    }

    private static string? BesideProcess()
    {
        try
        {
            var directory = Path.GetDirectoryName(Environment.ProcessPath ?? "");
            if (string.IsNullOrEmpty(directory)) return null;
            var path = Path.Combine(directory, FileName);
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static InstallPreset? Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return null;
            var preset = JsonSerializer.Deserialize<InstallPreset>(File.ReadAllText(path), Options);
            if (preset is null) return null;
            preset.IndexRoots = preset.IndexRoots?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new();
            preset.ExcludedPaths = preset.ExcludedPaths?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new();
            // 更新源同样只认 https 或文件路径
            if (preset.UpdateFeed is { } feed && feed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) preset.UpdateFeed = null;
            return preset;
        }
        catch { return null; }
    }
}
