using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FilePoint.Windows;

internal sealed class RecycleBinWindow : Window
{
    private readonly MountedDrive drive;
    private readonly ListView items = new() { SelectionMode = SelectionMode.Single, Margin = new Thickness(0, 14, 0, 14) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 24 };
    private readonly Button refresh;
    private readonly Button restore;
    private readonly Button delete;
    private bool busy;

    internal RecycleBinWindow(Window owner, MountedDrive drive)
    {
        this.drive = drive;
        Owner = owner;
        Icon = owner.Icon;
        Title = "FilePoint Recycle Bin";
        Width = 680;
        Height = 450;
        MinWidth = 560;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("WindowBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        UiTheme.StyleCaption(this);

        var layout = new DockPanel { Margin = new Thickness(24, 20, 24, 22) };
        var heading = new TextBlock { Text = "Recycle Bin", FontSize = 24, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        status.Foreground = (Brush)FindResource("MutedBrush");
        footer.Children.Add(status);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(buttons);
        refresh = AddButton("Refresh", "QuietButton");
        restore = AddButton("Restore", "PrimaryButton");
        delete = AddButton("Delete permanently", "QuietButton");
        restore.Visibility = drive.CanRestore ? Visibility.Visible : Visibility.Collapsed;
        delete.Visibility = drive.CanDelete ? Visibility.Visible : Visibility.Collapsed;

        var columns = new GridView();
        columns.Columns.Add(new GridViewColumn { Header = "Name", Width = 175, DisplayMemberBinding = new Binding(nameof(Row.Name)) });
        columns.Columns.Add(new GridViewColumn { Header = "Original location", Width = 245, DisplayMemberBinding = new Binding(nameof(Row.Location)) });
        columns.Columns.Add(new GridViewColumn { Header = "Deleted", Width = 145, DisplayMemberBinding = new Binding(nameof(Row.Deleted)) });
        items.View = columns;
        items.Background = Background;
        items.Foreground = Foreground;
        var headerStyle = new Style(typeof(GridViewColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Background));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        columns.ColumnHeaderContainerStyle = headerStyle;
        layout.Children.Add(items);
        Content = layout;

        Loaded += async (_, _) => await Run(null, restoreItem: false);
        refresh.Click += async (_, _) => await Run(null, restoreItem: false);
        restore.Click += async (_, _) => await Run((Row?)items.SelectedItem, restoreItem: true);
        delete.Click += async (_, _) =>
        {
            if (items.SelectedItem is Row row && MessageBox.Show(this, $"Permanently delete “{row.Name}”?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                await Run(row, restoreItem: false);
        };
        items.SelectionChanged += (_, _) => UpdateButtons();
        Closing += (_, e) => e.Cancel = busy;
        UpdateButtons();

        Button AddButton(string label, string style)
        {
            var button = new Button { Content = label, Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 10, 0) };
            buttons.Children.Add(button);
            return button;
        }
    }

    private async Task Run(Row? row, bool restoreItem)
    {
        if (busy)
            return;
        busy = true;
        UpdateButtons();
        status.Text = row is null ? "Loading deleted items…" : restoreItem ? "Restoring…" : "Deleting…";
        try
        {
            if (row is not null)
            {
                await Task.Run(() => drive.ChangeRecycledItem(row.Item.Id, restoreItem));
                items.ItemsSource = ((Row[])items.ItemsSource).Where(item => item.Item.Id != row.Item.Id).ToArray();
            }
            var listing = await Task.Run(drive.ListRecycledItems);
            items.ItemsSource = listing.Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .OrderByDescending(item => item.DeletedAt).Select(item => new Row(item)).ToArray();
            status.Text = row is not null ? (restoreItem ? "Item restored." : "Item permanently deleted.") :
                items.Items.Count switch
                {
                    0 => "The Recycle Bin is empty.",
                    1 => "1 deleted item",
                    var count => $"{count:N0} deleted items"
                };
        }
        catch (Exception error)
        {
            status.Text = error switch
            {
                HttpRequestException { StatusCode: HttpStatusCode.Conflict } when restoreItem =>
                    "FilePoint couldn’t restore the item. Check that its original location is available and the name is free.",
                HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } =>
                    "Your FilePoint account does not have permission for this action.",
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } when row is not null =>
                    "This item is no longer in the Recycle Bin. Refresh the list.",
                _ => "FilePoint couldn’t complete the request. Refresh the list to check the current state."
            };
        }
        finally
        {
            busy = false;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        refresh.IsEnabled = !busy;
        restore.IsEnabled = !busy && drive.CanRestore && items.SelectedItem is Row;
        delete.IsEnabled = !busy && drive.CanDelete && items.SelectedItem is Row;
    }

    private sealed record Row(RecycledItem Item)
    {
        public string Name => Item.Name;
        public string Location => Item.Source + Item.Path?.Replace('/', '\\');
        public string Deleted => Item.DeletedAt == default ? "" : Item.DeletedAt.ToLocalTime().ToString("g");
    }
}
