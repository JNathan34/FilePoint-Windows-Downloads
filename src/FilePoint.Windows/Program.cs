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
        bool startInTray = Array.Exists(args, argument => argument == WindowsStartup.Argument);
        using var instance = new Mutex(true, @"Local\FilePoint.Windows.5EF65D30", out bool owner);
        if (!owner)
        {
            if (!startInTray && !Array.Exists(args, argument =>
                argument is "--remove-integration" or UpdateInstall.ReopenArgument))
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
        var window = new MainWindow();
        app.Startup += async (_, _) => await window.Start(startInTray);
        if (!startInTray)
        {
            app.Run(window);
            return;
        }

        app.MainWindow = window;
        app.Run();
    }
}

internal sealed class MountedDrive(DriveMount host, FilePointApi api, Account account, FilePointDrive fileSystem,
    Func<FilePointDrive> createFileSystem, bool hasRecycleBin, string? sessionToken) : IDisposable
{
    private DriveMount? mount = host;
    private string preferredPath = host.Path;

    internal string Username => account.Username;
    internal string? Path => mount?.Path;
    internal bool IsRunning => mount?.IsRunning == true;
    internal bool HasRecycleBin { get; } = hasRecycleBin;
    internal bool CanRestore => account.CanRestore;
    internal bool CanDelete => account.CanDelete;
    internal string? SessionToken { get; } = sessionToken;

    internal RecycledItem[] ListRecycledItems() => api.ListRecycledItems();

    internal void ChangeRecycledItem(string id, bool restore)
    {
        api.ChangeRecycledItem(id, restore);
        if (restore)
            fileSystem.RefreshListings();
    }

    internal static MountedDrive Connect(Uri server, bool apiTokenMode, string name, string secret, string code,
        Action<string> uploadFailed, Action<string> recycleFailed, SavedConnection? reconnect = null)
    {
        var api = new FilePointApi(server);
        DriveMount? host = null;
        try
        {
            string? sessionToken = null;
            Account account;
            if (reconnect is not null)
                account = api.RestoreAccount(reconnect);
            else
            {
                if (apiTokenMode)
                    api.UseToken(secret);
                else
                    sessionToken = api.Login(name, secret, code);
                account = api.GetAccount();
            }
            if (account.Scopes is not { Length: > 0 })
                throw new UnauthorizedAccessException("This FilePoint account has no storage sources.");
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User!;
            var sources = api.GetWritableSources();
            bool hasRecycleBin = api.SupportsRecycleBin();
            var icons = new FolderIcons();
            FilePointDrive CreateFileSystem() => new(api, icons, user, sources, uploadFailed,
                hasRecycleBin && account.CanDelete, recycleFailed, hasRecycleBin);
            var fileSystem = CreateFileSystem();
            host = new DriveMount(fileSystem);
            ExplorerIntegration.Register(host.Path);
            return new MountedDrive(host, api, account, fileSystem, CreateFileSystem, hasRecycleBin, sessionToken);
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

    internal void RestoreMount()
    {
        var previous = mount;
        mount = null;
        try { ExplorerIntegration.Unregister(); }
        finally { previous?.Dispose(); }

        var restoredFileSystem = createFileSystem();
        var restored = new DriveMount(restoredFileSystem, preferredPath);
        try { ExplorerIntegration.Register(restored.Path); }
        catch
        {
            restored.Dispose();
            throw;
        }
        fileSystem = restoredFileSystem;
        preferredPath = restored.Path;
        mount = restored;
    }

    public void Dispose()
    {
        try { ExplorerIntegration.Unregister(); }
        finally
        {
            mount?.Dispose();
            api.Dispose();
        }
    }
}
