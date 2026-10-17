using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharedQuickSearch;

/// <summary>一次生成结果：图、原图尺寸、是否为低清（内嵌小图，稍后精修）。</summary>
internal sealed record ThumbResult(BitmapSource? Image, int Width, int Height, bool LowQuality);

/// <summary>WPF 解码/编码工具，全部在后台线程可用（结果都 Freeze）。</summary>
internal static class ImageCodec
{
    private const long MaxDecodeBytes = 160L * 1024 * 1024;
    private const long LargeFileBytes = 16L * 1024 * 1024;
    /// <summary>大文件要整份进内存，最多同时 2 个，避免 4 个线程各占 160 MB。</summary>
    private static readonly SemaphoreSlim LargeDecodeGate = new(2);

    public static BitmapSource? Decode(byte[] data, int offset, int count, int maxEdge = 0)
    {
        try
        {
            using var stream = new MemoryStream(data, offset, count, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            if (maxEdge > 0) image.DecodePixelWidth = maxEdge;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    /// <summary>把内嵌缩略图字节解码，并按 EXIF 方向转正、修正旧版 PSD 的 BGR。</summary>
    public static BitmapSource? DecodeEmbedded(EmbeddedThumbnail embedded)
    {
        if (embedded.ImageBytes is not { Length: > 0 } bytes) return null;
        var image = Decode(bytes, 0, bytes.Length);
        if (image is null) return null;
        if (embedded.SwapRedBlue) image = SwapRedBlue(image);
        return Orient(image, embedded.Orientation);
    }

    /// <summary>
    /// 整个文件只读一次进内存，先读头拿原图尺寸，再按长边缩放解码（JPEG 会走 DCT 缩放，不铺满原图）。
    /// </summary>
    public static ThumbResult? DecodeFile(string path, int maxEdge)
    {
        var gated = false;
        try
        {
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 256 * 1024, FileOptions.SequentialScan))
            {
                if (file.Length <= 0 || file.Length > MaxDecodeBytes) return null;
                // 打开之后再看大小，不为了判断大小多跑一次网络往返
                if (file.Length > LargeFileBytes) { LargeDecodeGate.Wait(); gated = true; }
                bytes = new byte[file.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = file.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < bytes.Length) return null;
            }

            int width, height, orientation = 1;
            using (var header = new MemoryStream(bytes, writable: false))
            {
                var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                if (frame.Metadata is BitmapMetadata metadata)
                {
                    // JPEG 在 /app1/ifd，TIFF 在 /ifd
                    foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                    {
                        try { if (metadata.GetQuery(query) is ushort value && value is >= 1 and <= 8) { orientation = value; break; } }
                        catch { }
                    }
                }
            }
            if (width <= 0 || height <= 0) return null;

            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            // 只设一边，另一边按比例；小图不放大
            if (width >= height) image.DecodePixelWidth = Math.Min(maxEdge, width);
            else image.DecodePixelHeight = Math.Min(maxEdge, height);
            image.EndInit();
            image.Freeze();

            var oriented = Orient(image, orientation);
            var swap = orientation is >= 5 and <= 8;
            return new ThumbResult(oriented, swap ? height : width, swap ? width : height, false);
        }
        catch { return null; }
        finally { if (gated) LargeDecodeGate.Release(); }
    }

    /// <summary>Shell / Windows 缓存可能给出比要求大得多的图（BiggerSizeOk），统一缩到长边不超过 maxEdge。</summary>
    public static BitmapSource FitWithin(BitmapSource image, int maxEdge)
    {
        var edge = Math.Max(image.PixelWidth, image.PixelHeight);
        if (edge <= maxEdge || edge <= 0) return image;
        var scale = (double)maxEdge / edge;
        var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        return Materialize(scaled);
    }

    /// <summary>
    /// TransformedBitmap 是惰性的，会一直引用原图。立即算出像素并拷贝，
    /// 内存缓存里存的就真的是小图，原始大图可以被回收。
    /// </summary>
    private static BitmapSource Materialize(BitmapSource source)
    {
        // 直接拷出像素重建，确保不再引用原始大图
        var stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, source.Format, source.Palette, pixels, stride);
        copy.Freeze();
        return copy;
    }

    /// <summary>EXIF 方向 1–8：2/4/5/7 带镜像，先翻转再顺时针旋转。</summary>
    public static BitmapSource Orient(BitmapSource image, int orientation)
    {
        var (flip, angle) = orientation switch
        {
            2 => (true, 0), 3 => (false, 180), 4 => (true, 180), 5 => (true, 270),
            6 => (false, 90), 7 => (true, 90), 8 => (false, 270), _ => (false, 0)
        };
        if (!flip && angle == 0) return image;
        var group = new TransformGroup();
        if (flip) group.Children.Add(new ScaleTransform(-1, 1));
        if (angle != 0) group.Children.Add(new RotateTransform(angle));
        return Materialize(new TransformedBitmap(image, group));
    }

    private static BitmapSource SwapRedBlue(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        var stride = converted.PixelWidth * 3;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        for (var i = 0; i + 2 < pixels.Length; i += 3) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        var swapped = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        swapped.Freeze();
        return swapped;
    }

    private static bool HasTransparentPixels(BitmapSource image)
    {
        try
        {
            BitmapSource bgra = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32
                ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var stride = bgra.PixelWidth * 4;
            var pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);
            for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] < 250) return true;
            return false;
        }
        catch { return true; }
    }

    public static bool HasAlpha(PixelFormat format) =>
        format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64
        || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float
        || format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2 || format == PixelFormats.Indexed4 || format == PixelFormats.Indexed8;

    /// <summary>
    /// 真的有透明像素才存 PNG（避免透明变黑）；否则存 JPEG，体积约为 PNG 的 1/5～1/10。
    /// Shell 给的位图格式通常带 alpha 通道但全是不透明，按像素判断而不是按格式判断。
    /// </summary>
    public static byte[] Encode(BitmapSource image)
    {
        var transparent = HasAlpha(image.Format) && HasTransparentPixels(image);
        BitmapSource source = image;
        if (!transparent && image.Format != PixelFormats.Bgr24 && image.Format != PixelFormats.Bgr32 && image.Format != PixelFormats.Gray8)
        {
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
            converted.Freeze();
            source = converted;
        }
        BitmapEncoder encoder = transparent ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 86 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
