using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SharedQuickSearch.Setup;

/// <summary>用系统的 ShellLink 组件创建 .lnk 快捷方式（桌面、开始菜单）。</summary>
internal static class ShellLink
{
    public static void Create(string linkPath, string targetPath, string workingDirectory, string description, string? arguments = null)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);
            if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /// <summary>读出快捷方式指向的程序（卸载时只删指向我们自己的快捷方式）。</summary>
    public static string? GetTarget(string linkPath)
    {
        if (!File.Exists(linkPath)) return null;
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            ((IPersistFile)link).Load(linkPath, 0);
            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            return buffer.ToString();
        }
        catch { return null; }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
