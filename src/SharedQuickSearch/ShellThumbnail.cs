using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SharedQuickSearch;

/// <summary>调用 Windows Shell 缩略图（与资源管理器同一套缓存与预览处理器）。必须在 STA 线程上调用。</summary>
internal static class ShellThumbnail
{
    [Flags]
    public enum Mode
    {
        BiggerSizeOk = 0x1,
        IconOnly = 0x4,
        ThumbnailOnly = 0x8,
        InCacheOnly = 0x10
    }

    public static BitmapSource? Load(string path, int size, Mode mode = Mode.ThumbnailOnly)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        var result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (result != 0 || factory is null) return null;

        var bitmapHandle = IntPtr.Zero;
        try
        {
            var requested = new NativeSize { Width = size, Height = size };
            result = factory.GetImage(requested, mode | Mode.BiggerSizeOk, out bitmapHandle);
            if (result != 0 || bitmapHandle == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmapHandle, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch { return null; }
        finally
        {
            if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
            Marshal.FinalReleaseComObject(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width; public int Height; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, Mode flags, out IntPtr bitmapHandle);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
