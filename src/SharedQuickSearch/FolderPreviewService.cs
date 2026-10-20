using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharedQuickSearch;

/// <summary>
/// 文件夹缩略图：从索引里挑出文件夹里最近修改的 3 张图（不够就往下找子文件夹），
/// 画成“几张照片从打开的文件夹里探出来”的样子。
/// 不让 Windows 生成文件夹缩略图——那要遍历共享盘子目录，网络上很慢。
/// 用到的每张小图都走普通缩略图管线，看过的直接从本机缓存出来。
/// </summary>
public sealed class FolderPreviewService
{
    private const int SmallCapacity = 300;  // 网格里的 256px：每张约 256 KB
    private const int LargeCapacity = 24;   // 详情栏 512px / 空格预览 640px：每张 1–1.6 MB，只留最近的
    private readonly FileIndexService _index;
    private readonly ThumbnailService _thumbnails;
    // 只在界面线程上访问
    private readonly Dictionary<string, (string Signature, BitmapSource? Image, FolderPreviewInfo Info)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _small = new();
    private readonly LinkedList<string> _large = new();
    // 拼图在后台 STA 线程上画，滚过一排文件夹时界面线程不卡
    private readonly StaWorkerPool _composer = new(1, "文件夹预览", ThreadPriority.BelowNormal);

    public FolderPreviewService(FileIndexService index, ThumbnailService thumbnails)
    {
        _index = index;
        _thumbnails = thumbnails;
    }

    /// <summary>生成（或取缓存的）文件夹预览。没有可预览的文件时 Image 为 null，界面继续显示文件夹图标。</summary>
    public async Task<(BitmapSource? Image, FolderPreviewInfo Info)> LoadAsync(FileEntry folder, CancellationToken cancellationToken, int size = 256)
    {
        var info = await Task.Run(() => _index.FolderPreview(folder.FullPath, 3), cancellationToken);
        var signature = $"{size}|{info.FileCount}|{info.FolderCount}|{info.NewestUtc.Ticks}|" + string.Join("|", info.Samples.Select(x => x.FullPath + "@" + x.LastWriteUtc.Ticks));
        var key = folder.FullPath + "#" + size;
        if (_cache.TryGetValue(key, out var hit) && hit.Signature == signature)
        {
            Touch(key, size > 256);
            return (hit.Image, info);
        }

        // 3 张小图同时排队：以前一张取完才排下一张，后两张会排到别的卡片后面，文件夹总是最后出来
        var pending = info.Samples.Select(sample =>
        {
            if (sample.Thumbnail is BitmapSource own) return Task.FromResult<BitmapSource?>(own);
            if (_thumbnails.TryGetCached(sample, out var cached)) return Task.FromResult(cached);
            return _thumbnails.LoadAsync(sample, cancellationToken);
        }).ToList();
        var loaded = await Task.WhenAll(pending);
        cancellationToken.ThrowIfCancellationRequested();
        var images = loaded.Where(x => x is not null).Select(x => x!).ToList();

        BitmapSource? composed = null;
        if (images.Count > 0)
        {
            composed = images.All(x => x.IsFrozen)
                ? await _composer.Run(() => Compose(images, size), cancellationToken)
                : Compose(images, size); // 没冻结的图不能跨线程，少见，就地画
        }
        cancellationToken.ThrowIfCancellationRequested();
        _cache[key] = (signature, composed, info);
        Touch(key, size > 256);
        return (composed, info);
    }

    /// <summary>之前算过的文件夹信息（界面线程）。用来在结果刚出来时就显示“23 个文件”。</summary>
    public FolderPreviewInfo? CachedInfo(string folderPath) =>
        _cache.TryGetValue(folderPath + "#256", out var hit) ? hit.Info : null;

    /// <summary>“23 个文件 · 4 个文件夹”这样的说明。</summary>
    public static string Describe(FolderPreviewInfo info)
    {
        if (info.FileCount == 0 && info.FolderCount == 0) return "空文件夹";
        if (info.FolderCount == 0) return $"{info.FileCount} 个文件";
        if (info.FileCount == 0) return $"{info.FolderCount} 个文件夹";
        return $"{info.FileCount} 个文件 · {info.FolderCount} 个文件夹";
    }

    private void Touch(string key, bool large)
    {
        var order = large ? _large : _small;
        var capacity = large ? LargeCapacity : SmallCapacity;
        order.Remove(key);
        order.AddFirst(key);
        while (order.Count > capacity)
        {
            _cache.Remove(order.Last!.Value);
            order.RemoveLast();
        }
    }

    // ================= 画图 =================

    private static readonly Brush FolderBack = Frozen(new LinearGradientBrush(Color.FromRgb(0xE8, 0xA4, 0x2E), Color.FromRgb(0xF0, 0xB2, 0x40), 90));
    private static readonly Brush FolderFront = Frozen(new LinearGradientBrush(Color.FromRgb(0xF9, 0xD4, 0x74), Color.FromRgb(0xF3, 0xB8, 0x45), 90));
    private static readonly Brush Highlight = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Shadow = Frozen(new SolidColorBrush(Color.FromArgb(0x2A, 0x00, 0x00, 0x00)));
    private static readonly Brush Paper = Brushes.White;

    private static T Frozen<T>(T brush) where T : Freezable { brush.Freeze(); return brush; }

    /// <summary>后面是文件夹背板，中间扇形排开的照片卡片，最前面是文件夹前板，照片上半截露在外面。</summary>
    private static BitmapSource Compose(IReadOnlyList<BitmapSource> images, int size)
    {
        double s = size;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 文件夹背板 + 标签页
            var back = new Rect(s * .07, s * .24, s * .86, s * .66);
            dc.DrawRoundedRectangle(FolderBack, null, new Rect(s * .07, s * .17, s * .33, s * .13), s * .045, s * .045);
            dc.DrawRoundedRectangle(FolderBack, null, back, s * .06, s * .06);

            // 照片：一张居中；两张左右各歪一点；三张时中间那张最高、在最上面
            var layout = images.Count switch
            {
                1 => new[] { (X: .50, Y: .40, Angle: 0.0) },
                2 => new[] { (X: .40, Y: .41, Angle: -8.0), (X: .60, Y: .40, Angle: 7.0) },
                _ => new[] { (X: .33, Y: .43, Angle: -11.0), (X: .67, Y: .43, Angle: 10.0), (X: .50, Y: .38, Angle: 0.0) }
            };
            var order = images.Count >= 3 ? new[] { 1, 2, 0 } : Enumerable.Range(0, images.Count).ToArray();
            for (var k = 0; k < layout.Length && k < images.Count; k++)
            {
                var image = images[order[k]];
                var (x, y, angle) = layout[k];
                DrawCard(dc, image, new Point(s * x, s * y), angle, s);
            }

            // 文件夹前板：只盖住照片最下面一截，照片内容大部分露出来
            var front = new Rect(s * .07, s * .64, s * .86, s * .26);
            dc.DrawRoundedRectangle(FolderFront, null, front, s * .06, s * .06);
            dc.DrawRectangle(Highlight, null, new Rect(front.X + s * .05, front.Y + 1, front.Width - s * .1, Math.Max(1, s / 200)));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawCard(DrawingContext dc, BitmapSource image, Point center, double angle, double s)
    {
        // 卡片按图片比例：最宽约为画布的 50%，最高 46%
        var maxW = s * .50;
        var maxH = s * .46;
        var ratio = image.PixelHeight > 0 ? (double)image.PixelWidth / image.PixelHeight : 1;
        var w = Math.Min(maxW, maxH * ratio);
        var h = w / ratio;
        if (h > maxH) { h = maxH; w = h * ratio; }
        var border = Math.Max(2, s * .014);
        var card = new Rect(center.X - w / 2 - border, center.Y - h / 2 - border, w + border * 2, h + border * 2);
        var radius = s * .022;

        dc.PushTransform(new RotateTransform(angle, center.X, center.Y));
        dc.DrawRoundedRectangle(Shadow, null, new Rect(card.X + s * .008, card.Y + s * .016, card.Width, card.Height), radius, radius);
        dc.DrawRoundedRectangle(Paper, null, card, radius, radius);
        dc.PushClip(new RectangleGeometry(new Rect(card.X + border, card.Y + border, w, h), radius * .6, radius * .6));
        dc.DrawImage(image, new Rect(card.X + border, card.Y + border, w, h));
        dc.Pop();
        dc.Pop();
    }
}
