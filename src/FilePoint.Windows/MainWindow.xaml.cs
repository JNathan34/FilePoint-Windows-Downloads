using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DokanNet;
using Forms = System.Windows.Forms;
using Icon = System.Drawing.Icon;

namespace FilePoint.Windows;

internal partial class MainWindow : Window
{
    private enum SignInMode { Password, Verification, Token }

    private readonly Icon appIcon = new(Path.Combine(AppContext.BaseDirectory, "Assets", "FilePoint.ico"));
    private readonly Forms.NotifyIcon tray;
    private readonly TrayMenu trayMenu;
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private GitHubUpdate? availableUpdate;
    private Version? notifiedVersion;
    private bool checkingUpdate;
    private bool showingUpdates;
    private bool showingRecycleBin;
    private MountedDrive? drive;
    private SavedConnection? saved;
    private SignInMode signInMode;
    private bool busy;
    private bool exitRequested;
    private bool recoveryPending;

    internal MainWindow()
    {
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Path.Combine(
            AppContext.BaseDirectory, "Assets", "FilePoint.ico")));
        BrandLogo.Source = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Path.Combine(
            AppContext.BaseDirectory, "Assets", "FilePoint.png")));
        UiTheme.StyleCaption(this);

        tray = new Forms.NotifyIcon { Icon = appIcon, Text = "FilePoint for Windows" };
        trayMenu = new TrayMenu(appIcon, ShowWindow, OpenDrive, ShowRecycleBin, QueueUpdates, () => { exitRequested = true; Close(); },
            () => OpenExternal(WritableFile.RecoveryPath));
        trayMenu.Opening += (_, _) => trayMenu.RefreshState(drive?.Path, busy || showingRecycleBin, availableUpdate?.Version,
            Directory.Exists(WritableFile.RecoveryPath), drive?.HasRecycleBin == true);
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            if (recoveryPending)
                OpenExternal(WritableFile.RecoveryPath);
            else
                QueueUpdates();
        });
        tray.Visible = true;

        Loaded += (_, _) =>
        {
            UsernameBox.Focus();
            StartUpdateChecks();
        };
        updateTimer.Tick += async (_, _) => await CheckForUpdate();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                Hide();
        };
        Deactivated += (_, _) => SetPasswordVisibility(false);
        Closing += (_, args) =>
        {
            if (busy)
                args.Cancel = true;
            else if (drive is not null && !exitRequested)
            {
                args.Cancel = true;
                Hide();
            }
        };
        Closed += (_, _) =>
        {
            updateTimer.Stop();
            PasswordInput.Clear();
            RevealedPassword.Clear();
            TokenInput.Clear();
            try { drive?.Dispose(); }
            finally { tray.Dispose(); trayMenu.Dispose(); appIcon.Dispose(); }
        };
        try { saved = SavedConnection.Load(); }
        catch (Exception) { /* Password sign-in remains available if the saved API token cannot be read. */ }
    }

    internal async Task Start(bool startInTray)
    {
        if (startInTray)
            StartUpdateChecks();
        if (saved is not { } connection || !Uri.TryCreate(connection.Address, UriKind.Absolute, out var server))
            return;

        AddressBox.Text = connection.Address;
        UsernameBox.Text = connection.Username ?? "";
        RememberLogin.IsChecked = connection.HasPassword;
        await ConnectToServer(server, apiTokenMode: !connection.HasPassword, connection.Username ?? "", "", "",
            remember: true, usesSavedToken: false, openExplorer: false, reconnect: connection);
    }

    private void StartUpdateChecks()
    {
        if (updateTimer.IsEnabled)
            return;
        updateTimer.Start();
        _ = CheckForUpdate();
    }

    private void OpenOptions(object sender, RoutedEventArgs args)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = 4 };
        AddOption(availableUpdate is null ? "Updates…" : $"Update {availableUpdate.Version} available", QueueUpdates);
        if (drive?.HasRecycleBin == true)
            AddOption("Recycle Bin", ShowRecycleBin);
        var startup = new MenuItem
        {
            Header = "Start with Windows",
            Style = (Style)FindResource(typeof(MenuItem)),
            IsCheckable = true
        };
        try { startup.IsChecked = WindowsStartup.IsEnabled; }
        catch (IOException) { startup.IsEnabled = false; }
        startup.Click += (_, _) =>
        {
            try { WindowsStartup.SetEnabled(startup.IsChecked); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                startup.IsChecked = !startup.IsChecked;
                ShowStatus("Windows couldn’t change FilePoint’s startup setting.");
            }
        };
        menu.Items.Add(startup);
        if (drive is null && signInMode == SignInMode.Password)
            AddOption("Sign in with an API token", ShowTokenSignIn);
        if (saved is not null)
            AddOption("Forget saved sign-in", ForgetSavedSignIn);
        MoreButton.ContextMenu = menu;
        menu.IsOpen = true;

        void AddOption(string title, Action action)
        {
            var item = new MenuItem { Header = title, Style = (Style)FindResource(typeof(MenuItem)) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void ShowTokenSignIn()
    {
        bool hasToken = saved is { HasPassword: false, Token.Length: > 0 };
        if (hasToken && AddressBox.Text == "https://files.plexpoint.uk")
            AddressBox.Text = saved!.Address;
        RememberToken.IsChecked = hasToken;
        SetSignInMode(SignInMode.Token);
        ShowStatus(hasToken ? "A saved API token can be used for its original server." : null, error: false);
        TokenInput.Focus();
    }

    private void ForgetSavedSignIn()
    {
        try
        {
            SavedConnection.Forget();
            saved = null;
            TokenInput.Clear();
            RememberToken.IsChecked = false;
            RememberLogin.IsChecked = false;
            if (signInMode == SignInMode.Verification)
                SetSignInMode(SignInMode.Password);
            ShowStatus("Saved sign-in removed.", error: false);
        }
        catch (IOException) { ShowStatus("Windows couldn’t remove the saved sign-in."); }
        catch (UnauthorizedAccessException) { ShowStatus("Windows couldn’t remove the saved sign-in."); }
    }

    private void SetSignInMode(SignInMode mode)
    {
        SetPasswordVisibility(false);
        signInMode = mode;
        Credentials.Visibility = mode == SignInMode.Verification ? Visibility.Collapsed : Visibility.Visible;
        VerificationFields.Visibility = mode == SignInMode.Verification ? Visibility.Visible : Visibility.Collapsed;
        PasswordFields.Visibility = mode == SignInMode.Password ? Visibility.Visible : Visibility.Collapsed;
        TokenFields.Visibility = mode == SignInMode.Token ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = mode == SignInMode.Password ? Visibility.Collapsed : Visibility.Visible;
        Heading.Text = mode switch
        {
            SignInMode.Verification => "Verify your sign-in",
            SignInMode.Token => "Connect with an API token",
            _ => "Sign in"
        };
        Introduction.Text = mode switch
        {
            SignInMode.Verification => "Enter the code from your authenticator app.",
            SignInMode.Token => "Use a FilePoint API token to connect your account.",
            _ => "Open your FilePoint folders in File Explorer."
        };
        ConnectButton.Content = mode == SignInMode.Verification ? "Verify and connect" : "Connect";
    }

    private void TogglePasswordVisibility(object sender, RoutedEventArgs args) =>
        SetPasswordVisibility(RevealedPassword.Visibility != Visibility.Visible);

    private void SetPasswordVisibility(bool show)
    {
        if (show == (RevealedPassword.Visibility == Visibility.Visible))
            return;

        if (show)
        {
            RevealedPassword.Text = PasswordInput.Password;
            RevealedPassword.CaretIndex = RevealedPassword.Text.Length;
            PasswordInput.Clear();
        }
        else
        {
            PasswordInput.Password = RevealedPassword.Text;
            RevealedPassword.Clear();
        }
        PasswordInput.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        RevealedPassword.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PasswordLabel.Target = show ? RevealedPassword : PasswordInput;
    }

    private void BackToSignIn(object sender, RoutedEventArgs args)
    {
        CodeBox.Clear();
        PasswordInput.Clear();
        TokenInput.Clear();
        SetSignInMode(SignInMode.Password);
        ShowStatus(null);
        UsernameBox.Focus();
    }

    private async void Connect(object sender, RoutedEventArgs args)
    {
        if (busy || drive is not null)
            return;

        SetPasswordVisibility(false);
        string input = AddressBox.Text.Trim();
        if (input.Length > 0 && !input.Contains("://", StringComparison.Ordinal))
            input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(server.Host) || !string.IsNullOrEmpty(server.UserInfo) || server.AbsolutePath != "/"
            || server.Query.Length != 0 || server.Fragment.Length != 0)
        {
            ShowStatus("Enter an HTTPS server address, such as https://files.plexpoint.uk.");
            AddressBox.Focus();
            return;
        }

        bool apiTokenMode = signInMode == SignInMode.Token;
        string user = UsernameBox.Text.Trim();
        string secret = apiTokenMode ? TokenInput.Password.Trim() : PasswordInput.Password;
        bool usesSavedToken = apiTokenMode && secret.Length == 0 &&
            saved is { HasPassword: false, Token.Length: > 0 } && saved.Address == server.AbsoluteUri;
        if (usesSavedToken)
            secret = saved!.Token!;
        if (signInMode == SignInMode.Verification && secret.Length == 0 &&
            saved is { HasPassword: true } connection && connection.Address == server.AbsoluteUri && connection.Username == user)
            secret = connection.Password!;
        string code = signInMode == SignInMode.Verification ? CodeBox.Text.Trim() : "";
        if (!apiTokenMode && user.Length == 0)
        {
            ShowStatus("Enter your username.");
            UsernameBox.Focus();
            return;
        }
        if (secret.Length == 0)
        {
            ShowStatus(apiTokenMode ? "Enter your FilePoint API token." : "Enter your password.");
            if (apiTokenMode)
                TokenInput.Focus();
            else
                PasswordInput.Focus();
            return;
        }
        if (signInMode == SignInMode.Verification && code.Length == 0)
        {
            ShowStatus("Enter your verification code.");
            CodeBox.Focus();
            return;
        }

        bool remember = (apiTokenMode ? RememberToken.IsChecked : RememberLogin.IsChecked) == true;
        await ConnectToServer(server, apiTokenMode, user, secret, code, remember,
            usesSavedToken, openExplorer: true);
    }

    private async Task ConnectToServer(Uri server, bool apiTokenMode, string user, string secret, string code,
        bool remember, bool usesSavedToken, bool openExplorer, SavedConnection? reconnect = null)
    {
        bool focusCode = false;
        SetBusy(true);
        ConnectButton.Content = "Connecting…";
        ShowStatus(null);
        try
        {
            drive = await Task.Run(() => MountedDrive.Connect(server, apiTokenMode, user, secret, code,
                ReportUploadFailure, ReportRecycleFailure, reconnect));
            AddressBox.Text = server.AbsoluteUri.TrimEnd('/');
            PasswordInput.Clear();
            TokenInput.Clear();
            CodeBox.Clear();
            ShowConnected(server.Host);
            try
            {
                if (reconnect is null && remember && !usesSavedToken)
                {
                    var connection = apiTokenMode ? new SavedConnection(server.AbsoluteUri, secret) :
                        new SavedConnection(server.AbsoluteUri, code.Length > 0 ? drive.SessionToken : null, user, secret);
                    connection.Save();
                    saved = connection;
                }
                else if (reconnect is null && !remember)
                {
                    SavedConnection.Forget();
                    saved = null;
                }
            }
            catch (Exception) { ShowStatus("Connected. Windows couldn’t update the saved sign-in."); }
            if (openExplorer)
                OpenExternal(drive.Path);
        }
        catch (VerificationRequiredException)
        {
            SetSignInMode(SignInMode.Verification);
            if (reconnect is not null)
                ShowStatus("Enter a new verification code to reconnect.", error: false);
            focusCode = true;
        }
        catch (Exception error)
        {
            bool missingDriver = error is DllNotFoundException or TypeInitializationException;
            ShowStatus(error switch
            {
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } when signInMode == SignInMode.Verification =>
                    "FilePoint couldn’t verify that code. Try a new code from your authenticator app.",
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } when reconnect is not null =>
                    "FilePoint couldn’t reuse your saved sign-in. Sign in again to reconnect.",
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } when !apiTokenMode =>
                    "FilePoint didn’t accept that username or password.",
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } when usesSavedToken =>
                    "FilePoint didn’t accept the saved API token. Sign in again to open your drive.",
                HttpRequestException { StatusCode: null } or OperationCanceledException =>
                    "FilePoint couldn’t reach the server. Check the address and your connection.",
                DllNotFoundException or TypeInitializationException =>
                    "FilePoint needs its Windows driver. Run FilePoint Setup to install it.",
                DokanException => "FilePoint couldn’t open its Explorer drive. Check that a drive letter is available.",
                _ => error.Message
            });
            DriverHelp.Visibility = missingDriver ? Visibility.Visible : Visibility.Collapsed;
            if (signInMode == SignInMode.Verification)
            {
                CodeBox.Clear();
                focusCode = true;
            }
        }
        finally
        {
            SetBusy(false);
            ConnectButton.Content = signInMode == SignInMode.Verification ? "Verify and connect" : "Connect";
            if (focusCode)
                CodeBox.Focus();
        }
    }

    private void ShowConnected(string host)
    {
        AccountName.Text = drive!.Username;
        ServerName.Text = host;
        Heading.Text = "Connected";
        Introduction.Text = "Your FilePoint drive is ready.";
        SignInPanel.Visibility = Visibility.Collapsed;
        ConnectedPanel.Visibility = Visibility.Visible;
    }

    private async void Disconnect(object sender, RoutedEventArgs args)
    {
        if (busy || drive is not { } mounted)
            return;
        SetBusy(true);
        DisconnectButton.Content = "Disconnecting…";
        ShowStatus(null);
        try
        {
            await Task.Run(mounted.Dispose);
        }
        catch (Exception)
        {
            ShowStatus("Windows couldn’t finish disconnecting. Close and reopen FilePoint before reconnecting.");
        }
        finally
        {
            drive = null;
            ConnectedPanel.Visibility = Visibility.Collapsed;
            SignInPanel.Visibility = Visibility.Visible;
            DisconnectButton.Content = "Disconnect";
            SetSignInMode(SignInMode.Password);
            SetBusy(false);
            PasswordInput.Focus();
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        Credentials.IsEnabled = VerificationFields.IsEnabled = ConnectButton.IsEnabled =
            DisconnectButton.IsEnabled = BackButton.IsEnabled = MoreButton.IsEnabled = !value;
    }

    private void ShowStatus(string? message, bool error = true)
    {
        DriverHelp.Visibility = Visibility.Collapsed;
        StatusText.Text = message ?? "";
        StatusText.Foreground = (Brush)FindResource(error ? "ErrorBrush" : "MutedBrush");
        StatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowWindow()
    {
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void Browse(object sender, RoutedEventArgs args) => OpenDrive();
    private void OpenSetup(object sender, RoutedEventArgs args) => OpenExternal(GitHubUpdates.ReleasesUrl);

    private void OpenDrive()
    {
        if (drive is { } mounted && !busy)
            OpenExternal(mounted.Path);
    }

    private void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { ShowStatus("Windows couldn’t open that location."); }
    }

    private void ShowRecycleBin()
    {
        if (busy || showingUpdates || showingRecycleBin || drive is not { HasRecycleBin: true } connected)
            return;
        ShowWindow();
        showingRecycleBin = true;
        try { new RecycleBinWindow(this, connected).ShowDialog(); }
        finally { showingRecycleBin = false; }
    }

    private void ReportUploadFailure(string path) => Dispatcher.BeginInvoke(() =>
    {
        if (exitRequested)
            return;
        recoveryPending = true;
        const string message = "FilePoint kept a local copy. Open Unsaved Uploads from the tray menu.";
        ShowStatus($"Couldn’t save {Path.GetFileName(path)} to the server. {message}");
        tray.ShowBalloonTip(8000, "FilePoint save failed", message, Forms.ToolTipIcon.Warning);
    });

    private void ReportRecycleFailure(string name) => Dispatcher.BeginInvoke(() =>
    {
        if (exitRequested)
            return;
        string message = $"FilePoint couldn’t confirm that {name} moved to the Recycle Bin. Refresh Explorer and check FilePoint’s Recycle Bin.";
        ShowStatus(message);
        tray.ShowBalloonTip(8000, "FilePoint delete failed", message, Forms.ToolTipIcon.Warning);
    });

    private async Task CheckForUpdate()
    {
        if (checkingUpdate)
            return;
        checkingUpdate = true;
        try
        {
            using var github = new GitHubUpdates();
            var update = await github.CheckAsync(GitHubUpdates.CurrentVersion, CancellationToken.None);
            if (exitRequested)
                return;
            SetUpdate(update);
            if (tray.Visible && update is not null && update.Version != notifiedVersion)
            {
                notifiedVersion = update.Version;
                recoveryPending = false;
                tray.ShowBalloonTip(5000, "FilePoint update", $"Version {update.Version} is available.", Forms.ToolTipIcon.Info);
            }
        }
        catch (Exception)
        {
            // A failed background check must never interrupt FilePoint or its mount.
        }
        finally
        {
            checkingUpdate = false;
        }
    }

    private void SetUpdate(GitHubUpdate? update)
    {
        availableUpdate = update;
        MoreButton.ToolTip = update is null ? "Options" : $"Update {update.Version} available";
    }

    private void QueueUpdates()
    {
        // Let the tray or Options menu release its input before entering the WPF modal loop.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowUpdates));
    }

    private void ShowUpdates()
    {
        if (exitRequested || busy || showingUpdates || showingRecycleBin)
            return;
        ShowWindow();
        var window = new UpdateWindow(this, availableUpdate, SetUpdate);
        showingUpdates = true;
        try
        {
            if (window.ShowDialog() != true)
                return;
        }
        finally
        {
            showingUpdates = false;
        }

        if (window.InstallerPath is not { } installer)
            return;
        try
        {
            UpdateInstall.Start(installer);
            exitRequested = true;
            Close();
        }
        catch (Exception error)
        {
            MessageBox.Show($"Setup could not start. {error.Message}", "FilePoint update",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
