using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FilePoint.Windows;

internal static partial class ExplorerIntegration
{
    internal static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilePoint.Windows");
    private const string Id = "{5EF65D30-CF81-48D7-9A7D-3E0F113124D8}";
    private const string Classes = @"Software\Classes\CLSID\" + Id;
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";
    private const string NameSpace = Explorer + @"Desktop\NameSpace\" + Id;
    private const string Hidden = Explorer + @"HideDesktopIcons\NewStartPanel";
    private const string Target = Classes + @"\Instance\InitPropertyBag";
    private static readonly string IconLocation = Path.Combine(AppContext.BaseDirectory, "Assets", "FilePoint.ico") + ",0";

    internal static void Register(string mountPath)
    {
        Set(Classes, null, "FilePoint");
        Set(Classes, "System.IsPinnedToNameSpaceTree", 1);
        Set(Classes, "SortOrderIndex", 0x42);
        Set(Classes + @"\DefaultIcon", null, IconLocation);
        Set(Classes + @"\InProcServer32", null, @"%SystemRoot%\System32\shell32.dll", RegistryValueKind.ExpandString);
        Set(Classes + @"\Instance", "CLSID", "{0E5AAE11-A475-4C5B-AB00-C66DE400274E}");
        Set(Target, "Attributes", 0x11);
        Set(Target, "TargetFolderPath", mountPath);
        Set(Classes + @"\ShellFolder", "FolderValueFlags", 0x28);
        Set(Classes + @"\ShellFolder", "Attributes", unchecked((int)0xF080004D));
        Set(Hidden, Id, 1);
        Set(NameSpace, null, "FilePoint");
        RegisterDriveIcon(mountPath);
        Refresh();
    }

    internal static void Unregister()
    {
        using (var target = Registry.CurrentUser.OpenSubKey(Target))
            if (target?.GetValue("TargetFolderPath") is string mountPath)
                UnregisterDriveIcon(mountPath);
        Registry.CurrentUser.DeleteSubKeyTree(NameSpace, false);
        Registry.CurrentUser.DeleteSubKeyTree(Classes, false);
        using var key = Registry.CurrentUser.OpenSubKey(Hidden, writable: true);
        key?.DeleteValue(Id, false);
        Refresh();
    }

    internal static void RegisterDriveIcon(string mountPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(DriveIconKey(mountPath));
        if (key.GetValue(null) is null)
            key.SetValue(null, IconLocation);
        Refresh();
    }

    internal static void UnregisterDriveIcon(string mountPath)
    {
        if (mountPath.Length != 3 || mountPath[1] != ':' || mountPath[2] != '\\' || !char.IsAsciiLetter(mountPath[0]))
            return;
        using var key = Registry.CurrentUser.OpenSubKey(DriveIconKey(mountPath), writable: true);
        if (key?.GetValue(null) is string value && value.Equals(IconLocation, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue(string.Empty, false);
        Refresh();
    }

    private static string DriveIconKey(string mountPath) =>
        $@"Software\Classes\Applications\Explorer.exe\Drives\{mountPath[0]}\DefaultIcon";

    private static void Set(string path, string? name, object value, RegistryValueKind kind = RegistryValueKind.Unknown)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value, kind);
    }

    private static void Refresh() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(uint eventId, uint flags, IntPtr first, IntPtr second);
}
