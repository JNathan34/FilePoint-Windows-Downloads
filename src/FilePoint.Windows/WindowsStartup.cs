using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FilePoint.Windows;

internal static class WindowsStartup
{
    internal const string Argument = "--startup";
    private const string ShortcutName = "FilePoint for Windows.lnk";

    internal static bool IsEnabled => File.Exists(GetShortcutPath());

    internal static void SetEnabled(bool enabled)
    {
        string path = GetShortcutPath();
        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        string executable = Environment.ProcessPath ?? throw new IOException("FilePoint could not locate its executable.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ??
            throw new IOException("Windows could not open its shortcut service.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(path);
            try
            {
                shortcut.TargetPath = executable;
                shortcut.Arguments = Argument;
                shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
                shortcut.Description = "Start FilePoint in the notification area";
                shortcut.Save();
            }
            finally { Marshal.ReleaseComObject(shortcut); }
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    private static string GetShortcutPath()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(folder))
            throw new IOException("Windows could not locate the Startup folder.");
        return Path.Combine(folder, ShortcutName);
    }
}
