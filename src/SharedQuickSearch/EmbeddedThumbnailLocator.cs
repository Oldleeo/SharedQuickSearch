using System.IO;
using System.Text;

namespace SharedQuickSearch;

/// <summary>在文件头里找到的内嵌预览图（压缩字节，尚未解码）与原图尺寸。</summary>
public sealed record EmbeddedThumbnail(byte[]? ImageBytes, int Width, int Height, int Orientation, bool SwapRedBlue);

/// <summary>
/// 只读文件开头的一小段，找出软件自己存进去的预览图，不读整个文件。
/// 这部分不依赖 WPF，可以单独测试。
///  · PSD / PSB：图像资源 1036（Photoshop 默认保存的 JPEG 缩略图，约 160px），同时拿到画布尺寸
///  · JPEG：EXIF IFD1 内嵌缩略图（约 160px）+ 方向标记 + SOF 里的原图尺寸，读前 192 KB
///  · AI / PDF / INDD / EPS：XMP 里的 xmpGImg:image（Base64 JPEG，通常 256px），读前 512 KB，找不到再扩到 4 MB
/// </summary>
public static class EmbeddedThumbnailLocator
{
    private const int JpegHeadBytes = 192 * 1024;
    private const int XmpFirstPass = 512 * 1024;
    private const int XmpSecondPass = 4 * 1024 * 1024;
    private const int MaxResourceBytes = 8 * 1024 * 1024;

    public static bool Supports(string extension) => extension.ToLowerInvariant() is
        ".psd" or ".psb" or ".jpg" or ".jpeg" or ".ai" or ".pdf" or ".indd" or ".eps";

    /// <param name="deepScan">false 时 XMP 只读第一轮 512 KB（后台预热用，省带宽）。</param>
    public static EmbeddedThumbnail? Locate(string path, string extension, bool deepScan = true)
    {
        if (!Supports(extension)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            return Locate(stream, extension, deepScan);
        }
        catch { return null; }
    }

    public static EmbeddedThumbnail? Locate(Stream stream, string extension, bool deepScan = true)
    {
        try
        {
            var ext = extension.ToLowerInvariant();
            return ext switch
            {
                ".psd" or ".psb" => FromPsd(stream),
                ".jpg" or ".jpeg" => FromJpeg(stream),
                // 大多数 PDF / INDD 没有 xmpGImg：只看 512 KB，找不到马上交给 Shell，不做第二轮
                ".pdf" or ".indd" => FromXmp(stream, secondPass: false),
                ".ai" or ".eps" => FromXmp(stream, secondPass: deepScan),
                _ => null
            };
        }
        catch { return null; }
    }

    // ---------------- PSD ----------------

    private static EmbeddedThumbnail? FromPsd(Stream s)
    {
        var header = ReadExact(s, 26);
        if (header is null || header[0] != '8' || header[1] != 'B' || header[2] != 'P' || header[3] != 'S') return null;
        var height = (int)BigEndian32(header, 14);
        var width = (int)BigEndian32(header, 18);

        var colorModeLength = ReadUInt32(s);
        if (colorModeLength is null) return new EmbeddedThumbnail(null, width, height, 1, false);
        s.Seek(colorModeLength.Value, SeekOrigin.Current);

        var resourcesLength = ReadUInt32(s);
        if (resourcesLength is null) return new EmbeddedThumbnail(null, width, height, 1, false);
        var end = s.Position + resourcesLength.Value;

        byte[]? jpeg = null;
        var swap = false;
        while (s.Position + 12 <= end)
        {
            var head = ReadExact(s, 6);
            if (head is null || !IsResourceSignature(head)) break;
            var id = (head[4] << 8) | head[5];
            var nameLength = s.ReadByte();
            if (nameLength < 0) break;
            // Pascal 字符串：长度字节 + 内容，总长补齐到偶数
            s.Seek(nameLength + ((nameLength + 1) & 1), SeekOrigin.Current);
            var size = ReadUInt32(s);
            if (size is null) break;
            var dataStart = s.Position;
            var next = dataStart + size.Value + (size.Value & 1);

            if ((id == 1036 || id == 1033) && size.Value > 28 && size.Value <= MaxResourceBytes)
            {
                var data = ReadExact(s, (int)size.Value);
                if (data is not null && BigEndian32(data, 0) == 1) // 1 = kJpegRGB
                {
                    var compressed = (int)Math.Min(BigEndian32(data, 20), (uint)(data.Length - 28));
                    if (compressed > 0)
                    {
                        jpeg = data.AsSpan(28, compressed).ToArray();
                        swap = id == 1033; // Photoshop 4 时代的旧资源存的是 BGR
                        if (id == 1036) break;
                    }
                }
            }
            if (next > end) break;
            s.Seek(next, SeekOrigin.Begin);
        }
        return new EmbeddedThumbnail(jpeg, width, height, 1, swap);
    }

    // 除了 8BIM，真实文件里还能见到这些签名（ImageReady、PhotoDeluxe、LightRoom 等写入）
    private static bool IsResourceSignature(byte[] h) =>
        (h[0] == '8' && h[1] == 'B' && h[2] == 'I' && h[3] == 'M') || (h[0] == '8' && h[1] == 'B' && h[2] == '6' && h[3] == '4')
        || (h[0] == 'M' && h[1] == 'e' && h[2] == 'S' && h[3] == 'a') || (h[0] == 'P' && h[1] == 'H' && h[2] == 'U' && h[3] == 'T')
        || (h[0] == 'A' && h[1] == 'g' && h[2] == 'H' && h[3] == 'g') || (h[0] == 'D' && h[1] == 'C' && h[2] == 'S' && h[3] == 'R');

    // ---------------- JPEG ----------------

    private static EmbeddedThumbnail? FromJpeg(Stream s)
    {
        var (buffer, count) = ReadUpTo(s, JpegHeadBytes);
        if (count < 4 || buffer[0] != 0xFF || buffer[1] != 0xD8) return null;

        var position = 2;
        var width = 0;
        var height = 0;
        var orientation = 1;
        byte[]? thumbnail = null;
        while (position + 4 <= count)
        {
            if (buffer[position] != 0xFF) break;
            var marker = buffer[position + 1];
            if (marker == 0xFF) { position++; continue; }
            if (marker is 0xD9 or 0xDA) break;
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { position += 2; continue; }
            var length = (buffer[position + 2] << 8) | buffer[position + 3];
            if (length < 2) break;
            var segment = position + 4;
            var segmentEnd = Math.Min(count, position + 2 + length);

            if (marker == 0xE1 && thumbnail is null && segment + 6 <= segmentEnd
                && buffer[segment] == 'E' && buffer[segment + 1] == 'x' && buffer[segment + 2] == 'i' && buffer[segment + 3] == 'f'
                && buffer[segment + 4] == 0 && buffer[segment + 5] == 0)
            {
                ParseExif(buffer, segment + 6, segmentEnd, ref orientation, ref thumbnail);
            }
            else if (IsStartOfFrame(marker) && segment + 5 <= segmentEnd)
            {
                height = (buffer[segment + 1] << 8) | buffer[segment + 2];
                width = (buffer[segment + 3] << 8) | buffer[segment + 4];
                break;
            }
            position += 2 + length;
        }

        if (thumbnail is null && width == 0) return null;
        // 方向 5–8 表示需要转 90°，对人来说宽高互换
        if (orientation is >= 5 and <= 8) (width, height) = (height, width);
        return new EmbeddedThumbnail(thumbnail, width, height, orientation, false);
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        (>= 0xC0 and <= 0xC3) or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF);

    private static void ParseExif(byte[] b, int tiff, int end, ref int orientation, ref byte[]? thumbnail)
    {
        if (tiff + 8 > end) return;
        var little = b[tiff] == 'I' && b[tiff + 1] == 'I';
        if (!little && !(b[tiff] == 'M' && b[tiff + 1] == 'M')) return;
        var length = end - tiff;

        int U16(int offset) => offset < 0 || offset + 2 > length ? -1
            : little ? b[tiff + offset] | (b[tiff + offset + 1] << 8) : (b[tiff + offset] << 8) | b[tiff + offset + 1];
        long U32(int offset) => offset < 0 || offset + 4 > length ? -1
            : little ? (uint)(b[tiff + offset] | (b[tiff + offset + 1] << 8) | (b[tiff + offset + 2] << 16) | (b[tiff + offset + 3] << 24))
                     : (uint)((b[tiff + offset] << 24) | (b[tiff + offset + 1] << 16) | (b[tiff + offset + 2] << 8) | b[tiff + offset + 3]);

        var ifd0 = U32(4);
        if (ifd0 <= 0 || ifd0 >= length) return;
        var entries = U16((int)ifd0);
        if (entries <= 0 || entries > 512) return;
        for (var i = 0; i < entries; i++)
        {
            var entry = (int)ifd0 + 2 + i * 12;
            if (U16(entry) == 0x0112) { var value = U16(entry + 8); if (value is >= 1 and <= 8) orientation = value; }
        }

        var ifd1 = U32((int)ifd0 + 2 + entries * 12);
        if (ifd1 <= 0 || ifd1 >= length) return;
        var count1 = U16((int)ifd1);
        if (count1 <= 0 || count1 > 512) return;
        long offset = -1, size = -1;
        for (var i = 0; i < count1; i++)
        {
            var entry = (int)ifd1 + 2 + i * 12;
            var tag = U16(entry);
            if (tag == 0x0201) offset = U32(entry + 8);
            else if (tag == 0x0202) size = U32(entry + 8);
        }
        if (offset <= 0 || size <= 0 || offset + size > length) return;
        if (b[tiff + offset] != 0xFF || b[tiff + offset + 1] != 0xD8) return;
        thumbnail = b.AsSpan(tiff + (int)offset, (int)size).ToArray();
    }

    // ---------------- XMP（AI / PDF / INDD / EPS） ----------------

    private static readonly byte[] XmpOpen = Encoding.ASCII.GetBytes("<xmpGImg:image>");
    private static readonly byte[] XmpClose = Encoding.ASCII.GetBytes("</xmpGImg:image>");
    private static readonly byte[] XmpAttribute = Encoding.ASCII.GetBytes("xmpGImg:image=\"");

    private static EmbeddedThumbnail? FromXmp(Stream s, bool secondPass)
    {
        var (buffer, count) = ReadUpTo(s, XmpFirstPass);
        var found = FindXmpImage(buffer, count);
        if (found is null && secondPass && count == XmpFirstPass)
        {
            var (more, moreCount) = ReadUpTo(s, XmpSecondPass - XmpFirstPass);
            var combined = new byte[count + moreCount];
            Buffer.BlockCopy(buffer, 0, combined, 0, count);
            Buffer.BlockCopy(more, 0, combined, count, moreCount);
            found = FindXmpImage(combined, combined.Length);
        }
        return found is null ? null : new EmbeddedThumbnail(found, 0, 0, 1, false);
    }

    private static byte[]? FindXmpImage(byte[] buffer, int count)
    {
        var span = buffer.AsSpan(0, count);
        int start, stop;
        var open = span.IndexOf(XmpOpen);
        if (open >= 0)
        {
            start = open + XmpOpen.Length;
            var close = span[start..].IndexOf(XmpClose);
            if (close < 0) return null;
            stop = start + close;
        }
        else
        {
            var attribute = span.IndexOf(XmpAttribute);
            if (attribute < 0) return null;
            start = attribute + XmpAttribute.Length;
            var quote = span[start..].IndexOf((byte)'"');
            if (quote < 0) return null;
            stop = start + quote;
        }

        // Base64 里夹着 XML 换行实体 &#xA; 和空白，去掉后再解码
        var text = Encoding.ASCII.GetString(buffer, start, stop - start)
            .Replace("&#xA;", "", StringComparison.OrdinalIgnoreCase)
            .Replace("&#xD;", "", StringComparison.OrdinalIgnoreCase);
        var clean = new StringBuilder(text.Length);
        foreach (var c in text) if (!char.IsWhiteSpace(c)) clean.Append(c);
        try
        {
            var bytes = Convert.FromBase64String(clean.ToString());
            return bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    // ---------------- 工具 ----------------

    private static byte[]? ReadExact(Stream s, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = s.Read(buffer, read, count - read);
            if (n <= 0) return null;
            read += n;
        }
        return buffer;
    }

    private static (byte[] Buffer, int Count) ReadUpTo(Stream s, int max)
    {
        var buffer = new byte[max];
        var read = 0;
        while (read < max)
        {
            var n = s.Read(buffer, read, max - read);
            if (n <= 0) break;
            read += n;
        }
        return (buffer, read);
    }

    private static uint? ReadUInt32(Stream s) => ReadExact(s, 4) is { } b ? BigEndian32(b, 0) : null;
    private static uint BigEndian32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
}
