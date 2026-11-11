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

internal sealed class MountedDrive(DriveMount host, FilePointApi api, Account account, FilePointDrive fileSystem,
    bool hasRecycleBin) : IDisposable
{
    internal string Username => account.Username;
    internal string Path => host.Path;
    internal bool HasRecycleBin { get; } = hasRecycleBin;
    internal bool CanRestore => account.CanRestore;
    internal bool CanDelete => account.CanDelete;

    internal RecycledItem[] ListRecycledItems() => api.ListRecycledItems();

    internal void ChangeRecycledItem(string id, bool restore)
    {
        api.ChangeRecycledItem(id, restore);
        if (restore)
            fileSystem.RefreshListings();
    }

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
            bool hasRecycleBin = api.SupportsRecycleBin();
            var fileSystem = new FilePointDrive(api, new FolderIcons(), identity.User!, sources, uploadFailed,
                hasRecycleBin && account.CanDelete, recycleFailed, hasRecycleBin);
            host = new DriveMount(fileSystem);
            ExplorerIntegration.Register(host.Path);
            return new MountedDrive(host, api, account, fileSystem, hasRecycleBin);
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
