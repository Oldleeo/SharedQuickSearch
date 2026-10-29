using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharedQuickSearch;

internal static class ReleaseFeatureTests
{
    internal static async Task Run(Action<string, bool> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "SharedQuickSearchFeatureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new AppSettings();
            check("v1.12 默认自动共享，双击 Ctrl 仍默认关闭", settings.TeamCacheMode == TeamCacheModes.Auto && settings.TeamCacheFolder == "" && !settings.DoubleCtrlSummon);
            check("新增设置克隆保留", new AppSettings { TeamCacheFolder = "team", ThumbnailCacheFolder = "local", DoubleCtrlSummon = true }.Clone() is { TeamCacheFolder: "team", ThumbnailCacheFolder: "local", DoubleCtrlSummon: true });
            check("共享缓存不重复嵌套固定目录", TeamThumbnailCache.NormalizeFolder(TeamThumbnailCache.NormalizeFolder(root)) == TeamThumbnailCache.NormalizeFolder(root));
            check("本机文件不上传全组缓存", TeamThumbnailCache.KeyFor(new FileEntry { FullPath = Path.Combine(root, "private.psd") }) is null);
            var stamp = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
            FileEntry Entry(string path, long size = 1) => new() { FullPath = path, Size = size, LastWriteUtc = stamp };
            var key = TeamThumbnailCache.KeyFor(Entry(@"\\NAS\素材\A.psd"))!;
            check("共享缓存键不含原文件名且不区分路径大小写", key.Length == 40 && key == TeamThumbnailCache.KeyFor(Entry(@"\\nas\素材\a.PSD")));
            check("共享缓存随文件大小变化失效", key != TeamThumbnailCache.KeyFor(Entry(@"\\NAS\素材\A.psd", 2)));
            check("网络路径不能作为本机缓存", ThumbnailService.ResolveLocalFolder(@"\\NAS\素材") == ThumbnailService.DefaultFolder);

            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            image.Freeze();
            var preview = new ThumbResult(image, 200, 100, false);
            var packed = ThumbnailDiskCache.Pack(preview);
            check("共享与本机缩略图格式往返一致", ThumbnailDiskCache.Unpack(packed) is { Width: 200, Height: 100, LowQuality: false, Image: not null });
            check("损坏共享缩略图被忽略", ThumbnailDiskCache.Unpack(new byte[17]) is null);

            // This is a private local test directory, never a real NAS share.
            var team = new TeamThumbnailCache(root);
            check("缓存读写探测成功", await team.ProbeAsync() == TeamCacheState.ReadWrite);
            team.Write(key, preview);
            check("全组缓存写后可读", await team.TryReadAsync(key) is { Width: 200, Height: 100 });
            team.Write(key, preview with { LowQuality = true, Width = 20 });
            check("低清缓存不能覆盖已有清晰缓存", await team.TryReadAsync(key) is { Width: 200, LowQuality: false });

            var local = new ThumbnailDiskCache(Path.Combine(root, "local"), 64 * 1024 * 1024);
            local.Write(key, preview);
            var foreign = Path.Combine(local.Folder, key[..2], "用户文件.sqt");
            File.WriteAllText(foreign, "keep");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            var outsideFile = Path.Combine(outside, key + ".sqt");
            File.WriteAllText(outsideFile, "keep outside");
            Directory.CreateSymbolicLink(Path.Combine(local.Folder, "ff"), outside);
            local.Clear();
            check("清空缓存保留用户文件", File.Exists(foreign) && !local.Contains(key));
            check("清空缓存不跟随目录链接", File.Exists(outsideFile));
            Directory.Delete(Path.Combine(local.Folder, "ff"));
        }
        finally { Directory.Delete(root, true); }
    }
}
