using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DokanNet;
using DokanNet.Logging;

namespace FilePoint.Windows;

internal sealed class DriveMount : IDisposable
{
    private readonly Dokan driver;
    private readonly DokanInstance instance;

    internal string Path { get; }

    internal DriveMount(FilePointDrive fileSystem)
    {
        string path = PickPath(Directory.GetLogicalDrives())
            ?? throw new IOException("Windows has no available drive letters.");
        driver = new Dokan(new NullLogger());
        DokanInstance? mounted = null;
        try
        {
            try { mounted = MountAt(path); }
            catch (DokanException error) when (path == @"F:\" &&
                error.ErrorStatus is DokanStatus.DriveLetterError or DokanStatus.MountError)
            {
                path = PickPath([.. Directory.GetLogicalDrives(), @"F:\"]) ?? throw new IOException(
                    "Windows has no fallback drive letter available.", error);
                mounted = MountAt(path);
            }
            Path = path;
            if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                WaitForDrive(Path);
            instance = mounted;
        }
        catch
        {
            try { mounted?.Dispose(); }
            finally { driver.Dispose(); }
            throw;
        }

        DokanInstance MountAt(string candidate) => new DokanInstanceBuilder(driver)
            .ConfigureOptions(options =>
            {
                options.MountPoint = candidate;
                options.Options = DokanOptions.CurrentSession | DokanOptions.WriteProtection;
                options.TimeOut = TimeSpan.FromSeconds(60);
            })
            .Build(fileSystem);
    }

    internal static string? PickPath(string[] occupied) => "FPQRSTUVWXYZONMLKJIHGED"
        .Select(letter => letter + @":\").Except(occupied, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private static void WaitForDrive(string path)
    {
        // Verify directory queries succeed before handing the new drive letter to Explorer.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.GetFileSystemEntries(path);
                return;
            }
            catch (IOException error) when (attempt < 7 && (error.HResult & 0xFFFF) is 2 or 3 or 21 or 87)
            {
                Thread.Sleep(150);
            }
        }
    }

    public void Dispose()
    {
        try { instance.Dispose(); }
        finally { driver.Dispose(); }
    }
}
