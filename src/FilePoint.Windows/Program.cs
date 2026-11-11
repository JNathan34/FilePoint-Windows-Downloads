using System;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace FilePoint.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new Mutex(true, @"Local\FilePoint.Windows.5EF65D30", out bool owner);
        if (!owner)
        {
            if (!Array.Exists(args, argument => argument == "--remove-integration"))
                MessageBox.Show("FilePoint is already running. Open it from the notification area.",
                    "FilePoint", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ExplorerIntegration.Unregister();
        if (Array.Exists(args, argument => argument == "--remove-integration"))
        {
            SavedConnection.Forget();
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        UiTheme.Load(app);
        app.Run(new MainWindow());
    }
}

internal sealed class MountedDrive(DriveMount host, FilePointApi api, string username) : IDisposable
{
    internal string Username { get; } = username;
    internal string Path => host.Path;

    internal static MountedDrive Connect(Uri server, bool apiTokenMode, string name, string secret, string code,
        Action<string> uploadFailed, Action<string> recycleFailed)
    {
        var api = new FilePointApi(server);
        DriveMount? host = null;
        try
        {
            if (apiTokenMode)
                api.UseToken(secret);
            else
                api.Login(name, secret, code);
            var account = api.GetAccount();
            if (account.Scopes is not { Length: > 0 })
                throw new UnauthorizedAccessException("This FilePoint account has no storage sources.");
            using var identity = WindowsIdentity.GetCurrent();
            var sources = api.GetWritableSources();
            var fileSystem = new FilePointDrive(api, new FolderIcons(), identity.User!, sources, uploadFailed,
                sources.Length > 0 && api.CanRecycle(account), recycleFailed);
            host = new DriveMount(fileSystem);
            ExplorerIntegration.Register(host.Path);
            return new MountedDrive(host, api, account.Username);
        }
        catch
        {
            try { ExplorerIntegration.Unregister(); }
            finally
            {
                host?.Dispose();
                api.Dispose();
            }
            throw;
        }
    }

    public void Dispose()
    {
        try { ExplorerIntegration.Unregister(); }
        finally
        {
            host.Dispose();
            api.Dispose();
        }
    }
}
