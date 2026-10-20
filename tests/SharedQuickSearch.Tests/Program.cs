using SharedQuickSearch;

var failures = new List<string>();
void Check(string name, bool value)
{
    if (!value) failures.Add(name);
}

Check("三斜杠规范化", PathRules.Normalize(@"\\\192.168.10.17\共享素材库\") == @"\\192.168.10.17\共享素材库");
Check("正斜杠规范化", PathRules.Normalize("//192.168.10.17/共享素材库/14-5") == @"\\192.168.10.17\共享素材库\14-5");
Check("排除自身", PathRules.IsSameOrChild(@"\\192.168.10.17\产品设计中心\@Recycle", @"\\192.168.10.17\产品设计中心\@Recycle"));
Check("排除子目录", PathRules.IsSameOrChild(@"\\192.168.10.17\产品设计中心\@Recycle\旧文件\a.psd", @"\\192.168.10.17\产品设计中心\@Recycle"));
Check("不误排相似目录", !PathRules.IsSameOrChild(@"\\192.168.10.17\产品设计中心\@Recycle-保留", @"\\192.168.10.17\产品设计中心\@Recycle"));
Check("SKU紧凑匹配", PathRules.CompactForSearch("14-5 主图") == "145主图");
Check("默认缩略图视图", new AppSettings().ResultView == "LargeIcons");
Check("默认跟随系统主题", new AppSettings().Theme == "System");
Check("主题名规范化", ThemeManager.Normalize("dark") == "Dark" && ThemeManager.Normalize("乱写") == "System");
Check("默认全局快捷键", GlobalHotkeyManager.TryParse("Ctrl+Alt+Space", out var hotkeyModifiers, out var hotkeyKey)
      && hotkeyModifiers.HasFlag(HotkeyModifiers.Control) && hotkeyModifiers.HasFlag(HotkeyModifiers.Alt)
      && hotkeyKey == System.Windows.Input.Key.Space);
Check("拒绝无修饰键快捷键", !GlobalHotkeyManager.TryParse("F8", out _, out _));
Check("SKU识别", SkuDetector.Find("新品 14-5 主图.psd") == "14-5");
Check("自然排序", new[] { "14-10", "14-2", "14-1" }.OrderBy(x => x, NaturalStringComparer.Current).SequenceEqual(new[] { "14-1", "14-2", "14-10" }));

var testRoot = Path.Combine(Path.GetTempPath(), "SharedQuickSearchTests", Guid.NewGuid().ToString("N"));
var excluded = Path.Combine(testRoot, "@Recycle");
var keep = Path.Combine(testRoot, "产品图");
Directory.CreateDirectory(excluded);
Directory.CreateDirectory(keep);
File.WriteAllText(Path.Combine(excluded, "14-5-废稿.psd"), "discard");
File.WriteAllText(Path.Combine(keep, "14-5 主图.jpg"), "image");
File.WriteAllText(Path.Combine(keep, "SKU_14_5说明.txt"), "text");
File.WriteAllText(Path.Combine(keep, "Thumbs.db"), "system thumbnail cache");
File.WriteAllText(Path.Combine(keep, "ehthumbs.db"), "system thumbnail cache");

try
{
    var indexPath = Path.Combine(testRoot, "test-index.json");
    var service = new FileIndexService(indexPath);
    var progressEvents = new List<IndexProgress>();
    var result = await service.RebuildAsync(new[] { testRoot }, new[] { excluded },
        new SyncProgress(progressEvents), CancellationToken.None);
    Check("索引保留目录", result.EntryCount == 3);
    Check("忽略系统缩略图缓存", service.Search("thumbs", SearchCategory.All, 100).Count == 0);
    Check("排除内容不可搜索", service.Search("废稿", SearchCategory.All, 100).Count == 0);
    Check("SKU普通匹配", service.Search("14-5", SearchCategory.All, 100).Any(x => x.Name == "14-5 主图.jpg"));
    Check("SKU分隔符容错", service.Search("14-5", SearchCategory.All, 100).Any(x => x.Name == "SKU_14_5说明.txt"));
    Check("图片筛选", service.Search("14-5", SearchCategory.Images, 100).All(x => x.Extension == ".jpg"));
    Check("自然名称排序", service.Search("14-5", SearchCategory.All, 100, SearchSort.Name).First().Name.Contains("14-5"));

    var incremental = Path.Combine(keep, "14-5 新增.txt");
    File.WriteAllText(incremental, "incremental");
    Check("增量新增", await service.ApplyChangesAsync(new[] { incremental }, new[] { excluded }) == 1 && service.Search("新增", SearchCategory.All, 100).Count == 1);
    File.Delete(incremental);
    Check("增量删除", await service.ApplyChangesAsync(new[] { incremental }, new[] { excluded }) == 1 && service.Search("新增", SearchCategory.All, 100).Count == 0);

    var incrementalThumbs = Path.Combine(keep, "Thumbs.db");
    Check("增量更新忽略系统缩略图缓存", await service.ApplyChangesAsync(new[] { incrementalThumbs }, new[] { excluded }) == 0
          && service.Search("thumbs", SearchCategory.All, 100).Count == 0);

    var indexJson = File.ReadAllText(indexPath);
    Check("索引不写入界面派生字段", !indexJson.Contains("TypeLabel") && !indexJson.Contains("SizeLabel") && !indexJson.Contains("IconGlyph") && !indexJson.Contains("TypeBrush"));

    await service.FlushAsync(); // 保存是合并的，重新载入前先落盘
    var loaded = new FileIndexService(indexPath);
    var loadedCount = await loaded.LoadAsync(new[] { excluded });
    Check("索引持久化", loadedCount == service.Count);
    Check("旧索引加载后仍忽略系统缩略图缓存", loaded.Search("thumbs", SearchCategory.All, 100).Count == 0);
    await Task.Delay(50);
    Check("索引进度完成", progressEvents.LastOrDefault()?.Percent == 100);
    Check("索引倒计时完成", progressEvents.LastOrDefault()?.EstimatedRemaining == TimeSpan.Zero);
}
finally
{
    Directory.Delete(testRoot, true);
}

// ---------- 文件头内嵌缩略图（按格式规范构造样本） ----------
var tinyJpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAA0JCgsKCA0LCgsODg0PEyAVExISEyccHhcgLikxMC4pLSwzOko+MzZGNywtQFdBRkxOUlNSMj5aYVpQYEpRUk//2wBDAQ4ODhMREyYVFSZPNS01T09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0//wAARCAB4AKADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDCooorzj7EKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKAP/Z");
static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };
static byte[] Be32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

var thumbResource = Join(Be32(1), Be32(160), Be32(120), Be32(480), Be32(57600), Be32(tinyJpeg.Length), Be16(24), Be16(1), tinyJpeg);
var resource = Join("8BIM"u8.ToArray(), Be16(1036), new byte[] { 0, 0 }, Be32(thumbResource.Length), thumbResource, thumbResource.Length % 2 == 1 ? new byte[] { 0 } : Array.Empty<byte>());
var psd = Join("8BPS"u8.ToArray(), Be16(1), new byte[6], Be16(3), Be32(300), Be32(400), Be16(8), Be16(3), Be32(0), Be32(resource.Length), resource, Be32(0), new byte[1024]);
var psdResult = EmbeddedThumbnailLocator.Locate(new MemoryStream(psd), ".psd");
Check("PSD 内嵌缩略图", psdResult?.ImageBytes?.Length == tinyJpeg.Length);
Check("PSD 画布尺寸", psdResult is { Width: 400, Height: 300 });

var tiff = Join("II*\0"u8.ToArray(), new byte[] { 8, 0, 0, 0 },
    new byte[] { 1, 0 }, new byte[] { 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0 }, new byte[] { 26, 0, 0, 0 },
    new byte[] { 2, 0 }, Join(new byte[] { 0x01, 0x02, 4, 0, 1, 0, 0, 0 }, BitConverter.GetBytes(56)), Join(new byte[] { 0x02, 0x02, 4, 0, 1, 0, 0, 0 }, BitConverter.GetBytes(tinyJpeg.Length)), new byte[4],
    tinyJpeg);
var app1 = Join(new byte[] { 0xFF, 0xE1 }, Be16(tiff.Length + 8), "Exif\0\0"u8.ToArray(), tiff);
var sof = new byte[] { 0xFF, 0xC0, 0, 17, 8, 0x02, 0x58, 0x03, 0x20, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 };
var jpeg = Join(new byte[] { 0xFF, 0xD8 }, app1, sof, new byte[] { 0xFF, 0xD9 });
var jpegResult = EmbeddedThumbnailLocator.Locate(new MemoryStream(jpeg), ".jpg");
Check("JPEG EXIF 缩略图", jpegResult?.ImageBytes?.Length == tinyJpeg.Length);
Check("JPEG 方向 6 宽高互换", jpegResult is { Orientation: 6, Width: 600, Height: 800 });

var base64 = Convert.ToBase64String(tinyJpeg);
var wrapped = string.Join("&#xA;", Enumerable.Range(0, (base64.Length + 75) / 76).Select(i => base64.Substring(i * 76, Math.Min(76, base64.Length - i * 76))));
var ai = System.Text.Encoding.ASCII.GetBytes("%PDF-1.6\n" + new string('%', 600 * 1024) + "<xmpGImg:image>" + wrapped + "</xmpGImg:image>\n%%EOF");
Check("AI XMP 缩略图（600KB 之后）", EmbeddedThumbnailLocator.Locate(new MemoryStream(ai), ".ai")?.ImageBytes?.Length == tinyJpeg.Length);
Check("普通 PDF 无内嵌图", EmbeddedThumbnailLocator.Locate(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF")), ".pdf") is null);
Check("损坏 PSD 不抛异常", EmbeddedThumbnailLocator.Locate(new MemoryStream(psd[..40]), ".psd") is not null);

if (failures.Count > 0)
{
    Console.Error.WriteLine("FAILED: " + string.Join(", ", failures));
    Environment.Exit(1);
}

Console.WriteLine("PASS: 路径、排除、系统缓存忽略、索引持久化与瘦身、SKU 搜索、类型筛选、自然排序、增量更新、主题设置和内嵌缩略图解析全部通过。");

// 同步回调：Progress<T> 在无同步上下文时把回调扔进线程池，顺序不确定，会偶发失败。
sealed class SyncProgress(List<IndexProgress> sink) : IProgress<IndexProgress>
{
    public void Report(IndexProgress value) { lock (sink) sink.Add(value); }
}
