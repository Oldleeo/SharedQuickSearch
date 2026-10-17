using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SharedQuickSearch;

public static class FileActions
{
    public static void Open(FileEntry entry)
    {
        EnsureExists(entry);
        Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
    }

    public static void Reveal(FileEntry entry)
    {
        EnsureExists(entry);
        if (entry.IsDirectory)
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.FullPath}\"") { UseShellExecute = true });
    }

    public static void CopyItem(FileEntry entry)
    {
        EnsureExists(entry);
        var files = new StringCollection { entry.FullPath };
        System.Windows.Clipboard.SetFileDropList(files);
    }

    public static void CopyItems(IEnumerable<FileEntry> entries)
    {
        // 不逐个检查是否存在：共享盘上每次检查都是一次网络往返，选得多会卡住界面。离线快照里的条目跳过。
        var paths = entries.Where(x => !x.IsOffline).Select(x => x.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new FileNotFoundException("选中的文件所在的共享盘当前离线，连上后再复制。");
        var files = new StringCollection(); files.AddRange(paths); System.Windows.Clipboard.SetFileDropList(files);
    }

    public static void CopyPaths(IEnumerable<FileEntry> entries) => System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, entries.Select(x => x.FullPath)));
    public static void CopyNames(IEnumerable<FileEntry> entries) => System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, entries.Select(x => x.Name)));

    public static void CopyPath(FileEntry entry) => System.Windows.Clipboard.SetText(entry.FullPath);

    private static void EnsureExists(FileEntry entry)
    {
        if (entry.IsDirectory ? !Directory.Exists(entry.FullPath) : !File.Exists(entry.FullPath))
            throw new FileNotFoundException("文件可能已被移动、删除，或共享盘当前不可访问。", entry.FullPath);
    }
}
