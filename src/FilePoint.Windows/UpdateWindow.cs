using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FilePoint.Windows;

internal sealed class UpdateWindow : Window
{
    private readonly GitHubUpdates github = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Action<GitHubUpdate?> report;
    private readonly TextBlock heading = new() { FontSize = 23, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ProgressBar progress = new() { Height = 5, Maximum = 100, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button action = new() { IsDefault = true, MinWidth = 136 };
    private GitHubUpdate? available;
    private bool busy;

    internal string? InstallerPath { get; private set; }

    internal UpdateWindow(Window owner, GitHubUpdate? available, Action<GitHubUpdate?> report)
    {
        this.available = available;
        this.report = report;
        Owner = owner;
        Title = "FilePoint updates";
        Width = 438;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("WindowBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        UiTheme.StyleCaption(this);
        status.Foreground = (Brush)FindResource("MutedBrush");
        progress.Foreground = (Brush)FindResource("FocusBrush");
        action.Style = (Style)FindResource("PrimaryButton");

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 26) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Installed version {GitHubUpdates.CurrentVersion}", FontSize = 12,
            Foreground = status.Foreground, Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(heading);
        panel.Children.Add(status);
        panel.Children.Add(progress);

        var footer = new DockPanel { Margin = new Thickness(0, 24, 0, 0), LastChildFill = false };
        var notes = new Button
        {
            Content = "Release notes", Style = (Style)FindResource("QuietButton"), Padding = new Thickness(0, 8, 0, 8)
        };
        DockPanel.SetDock(notes, Dock.Left);
        DockPanel.SetDock(action, Dock.Right);
        footer.Children.Add(notes);
        footer.Children.Add(action);
        panel.Children.Add(footer);
        Content = panel;

        notes.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(GitHubUpdates.ReleasesUrl) { UseShellExecute = true }); }
            catch (Exception) { status.Text = "Windows couldn’t open the release notes."; }
        };
        action.Click += async (_, _) =>
        {
            if (available is { } update)
                await Run(() => Download(update));
            else
                await Run(Check);
        };
        Loaded += async (_, _) =>
        {
            if (this.available is null)
                await Run(Check);
        };
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            github.Dispose();
            lifetime.Dispose();
        };
        SetUpdate(available);
    }

    private async Task Check()
    {
        heading.Text = "Checking for updates…";
        status.Text = "Looking for the latest FilePoint release.";
        SetUpdate(await github.CheckAsync(GitHubUpdates.CurrentVersion, lifetime.Token));
        report(available);
    }

    private async Task Download(GitHubUpdate update)
    {
        heading.Text = $"Updating to {update.Version}";
        status.Text = "Downloading FilePoint…";
        progress.Value = 0;
        progress.Visibility = Visibility.Visible;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var downloadProgress = new Progress<int>(percent =>
        {
            progress.Value = percent;
            status.Text = percent == 100 ? "Verifying the download…" : $"Downloading FilePoint · {percent}%";
        });
        InstallerPath = await github.DownloadAsync(update, timeout.Token, downloadProgress);
        DialogResult = true;
    }

    private async Task Run(Func<Task> work)
    {
        if (busy)
            return;
        busy = true;
        action.IsEnabled = false;
        try { await work(); }
        catch (Exception error)
        {
            if (IsVisible)
            {
                heading.Text = "Update unavailable";
                status.Text = error is InvalidDataException ? error.Message :
                    "FilePoint couldn’t complete the request. Try again or use the release notes to download Setup.";
            }
        }
        finally
        {
            progress.Visibility = Visibility.Collapsed;
            busy = false;
            action.IsEnabled = true;
        }
    }

    private void SetUpdate(GitHubUpdate? update)
    {
        available = update;
        heading.Text = update is null ? "FilePoint is up to date" : $"FilePoint {update.Version} is available";
        status.Text = update is null ? "You have the latest stable release." :
            "FilePoint will close during installation and reopen when the update is ready.";
        action.Content = update is null ? "Check again" : "Install update";
    }
}
