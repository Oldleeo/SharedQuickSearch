using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace SharedQuickSearch;

/// <summary>
/// 把文件拖出到资源管理器、桌面、微信等任意位置。
/// 安全原则：只向系统声明“允许复制”（DragDropEffects.Copy），并写入 Preferred DropEffect = 复制。
/// 目标程序拿到的只能是复制操作，按 Shift 也变不成移动，共享盘上的原文件不会被移走。
/// 拖拽图：叠放的缩略图 + 数量角标，通过系统 IDragSourceHelper 显示（与资源管理器一致，目标处会出现“+ 复制到…”）。
/// </summary>
internal static class DragOut
{
    private const int DropEffectCopy = 1;

    public static DragDropEffects Start(DependencyObject source, IReadOnlyList<FileEntry> entries, Point cursorOffset)
    {
        // 不在界面线程上逐个检查文件是否存在（共享盘上每个都是一次网络往返）；离线快照里的条目不拖
        entries = entries.Where(x => !x.IsOffline).ToList(); // 拖拽图的张数和角标也按实际拖出的算
        var paths = entries.Select(x => x.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return DragDropEffects.None;

        // 首选系统 Shell 的数据对象：它接受任意格式，拖拽缩略图才能挂上去（WPF 自带的 DataObject 不支持，拖拽图会静默失败）
        var shell = TryCreateShellDataObject(paths);
        if (shell is not null)
        {
            try { SetDragImage(shell, entries, cursorOffset); } catch { /* 拖拽图失败不影响拖拽本身 */ }
            return DragDrop.DoDragDrop(source, new DataObject(shell), DragDropEffects.Copy);
        }

        var files = new StringCollection();
        files.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(files);
        // 告诉目标：首选复制
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(DropEffectCopy)));
        return DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
    }

    // ---------------- Shell 数据对象 ----------------

    private static ComIDataObject? TryCreateShellDataObject(string[] paths)
    {
        try
        {
            var iid = typeof(ComIDataObject).GUID;
            if (SHCreateDataObject(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, ref iid, out var created) != 0 || created is not ComIDataObject data) return null;

            // CF_HDROP：DROPFILES 头 + UTF-16 路径列表（每个以 \0 结尾，最后再一个 \0）
            var text = string.Join("\0", paths) + "\0\0";
            var bytes = System.Text.Encoding.Unicode.GetBytes(text);
            const int headerSize = 20;
            var drop = new byte[headerSize + bytes.Length];
            BitConverter.GetBytes(headerSize).CopyTo(drop, 0);  // pFiles
            BitConverter.GetBytes(1).CopyTo(drop, 16);          // fWide = TRUE
            bytes.CopyTo(drop, headerSize);
            SetHGlobal(data, CfHdrop, drop);

            // 首选效果 = 复制
            SetHGlobal(data, (short)RegisterClipboardFormat("Preferred DropEffect"), BitConverter.GetBytes(DropEffectCopy));
            return data;
        }
        catch { return null; }
    }

    private static void SetHGlobal(ComIDataObject data, short format, byte[] bytes)
    {
        var handle = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, handle, bytes.Length);
        var formatEtc = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = format, dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT, lindex = -1,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
        };
        var medium = new System.Runtime.InteropServices.ComTypes.STGMEDIUM
        {
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL, unionmember = handle
        };
        try { data.SetData(ref formatEtc, ref medium, true); } // fRelease = true：成功后内存归数据对象
        catch { Marshal.FreeHGlobal(handle); throw; }
    }

    // ---------------- 拖拽图 ----------------

    private static void SetDragImage(ComIDataObject data, IReadOnlyList<FileEntry> entries, Point cursorOffset)
    {
        const int width = 168, height = 150;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var shadow = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
            var stack = Math.Min(entries.Count, 3);
            for (var i = stack - 1; i >= 0; i--)
            {
                var angle = i == 0 ? 0 : i == 1 ? 5 : -6;
                dc.PushTransform(new RotateTransform(angle, width / 2.0, height / 2.0));
                var rect = new Rect(14, 14, width - 28, height - 34);
                dc.DrawRoundedRectangle(shadow, null, new Rect(rect.X + 2, rect.Y + 4, rect.Width, rect.Height), 12, 12);
                dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)), 1), rect, 12, 12);
                var image = entries[i].Thumbnail;
                if (image is not null)
                {
                    var inner = Fit(image.PixelWidth, image.PixelHeight, new Rect(rect.X + 8, rect.Y + 8, rect.Width - 16, rect.Height - 16));
                    dc.DrawImage(image, inner);
                }
                dc.Pop();
            }
            if (entries.Count > 1)
            {
                var text = new FormattedText(entries.Count.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"), 13, Brushes.White, 1.0);
                var badgeWidth = Math.Max(26, text.Width + 14);
                var badge = new Rect(width - badgeWidth - 4, 4, badgeWidth, 26);
                dc.DrawRoundedRectangle(Brushes.White, null, new Rect(badge.X - 2, badge.Y - 2, badge.Width + 4, badge.Height + 4), 15, 15);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x57, 0xE6)), null, badge, 13, 13);
                dc.DrawText(text, new Point(badge.X + (badge.Width - text.Width) / 2, badge.Y + (badge.Height - text.Height) / 2));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        // 系统拖拽图要预乘 alpha 的 32 位 DIB（Pbgra32 正好是预乘的），自上而下
        var info = new BitmapInfo
        {
            biSize = Marshal.SizeOf<BitmapInfo>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32, biCompression = 0
        };
        var hBitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (hBitmap == IntPtr.Zero) return;
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        var helper = (IDragSourceHelper)new DragDropHelper();
        var dragImage = new ShDragImage
        {
            sizeDragImage = new NativeSize { cx = width, cy = height },
            ptOffset = new NativePoint { x = (int)Math.Clamp(cursorOffset.X, 0, width - 1), y = (int)Math.Clamp(cursorOffset.Y, 0, height - 1) },
            hbmpDragImage = hBitmap,
            crColorKey = unchecked((int)0xFFFFFFFF)
        };
        // 成功后位图归系统所有，不能再 DeleteObject
        var result = helper.InitializeFromBitmap(ref dragImage, data);
        if (result != 0) DeleteObject(hBitmap);
        Marshal.ReleaseComObject(helper);
    }

    private static Rect Fit(int w, int h, Rect box)
    {
        if (w <= 0 || h <= 0) return box;
        var scale = Math.Min(box.Width / w, box.Height / h);
        var fw = w * scale;
        var fh = h * scale;
        return new Rect(box.X + (box.Width - fw) / 2, box.Y + (box.Height - fh) / 2, fw, fh);
    }

    // ---------------- COM / Win32 ----------------

    [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    private class DragDropHelper { }

    [ComImport, Guid("DE5BF786-477A-11D2-839D-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        [PreserveSig] int InitializeFromBitmap(ref ShDragImage dragImage, ComIDataObject dataObject);
        [PreserveSig] int InitializeFromWindow(IntPtr hwnd, ref NativePoint point, ComIDataObject dataObject);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int cx; public int cy; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShDragImage
    {
        public NativeSize sizeDragImage;
        public NativePoint ptOffset;
        public IntPtr hbmpDragImage;
        public int crColorKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    private const short CfHdrop = 15;

    [DllImport("shell32.dll")]
    private static extern int SHCreateDataObject(IntPtr pidlFolder, uint cidl, IntPtr apidl, IntPtr dataObject, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? created);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);
}
