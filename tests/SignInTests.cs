using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FilePoint.Windows;

internal static class SignInTests
{
    internal static void Run(string[] arguments)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        UiTheme.Load(app);
        var window = new MainWindow();
        try
        {
            Require(!window.ShowInTaskbar, "FilePoint uses the notification area without a taskbar button");
            Require(Control<TextBox>("AddressBox").Text == "https://files.plexpoint.uk", "Prefilled server URL");
            Require(Control<StackPanel>("PasswordFields").Visibility == Visibility.Visible, "Password sign-in by default");
            Require(Control<StackPanel>("TokenFields").Visibility == Visibility.Collapsed, "API token hidden by default");
            Require(Control<StackPanel>("VerificationFields").Visibility == Visibility.Collapsed, "Verification hidden by default");
            Require(Control<StackPanel>("ConnectedPanel").Visibility == Visibility.Collapsed, "Connected actions hidden on sign-in");
            Require(Control<Image>("BrandLogo").Source is BitmapSource { PixelWidth: 512, PixelHeight: 512 },
                "FilePoint web-app artwork loads in the sign-in window");
            var connect = Control<Button>("ConnectButton");
            Require(connect.Content.ToString() == "Connect" && connect.IsDefault, "Single default Connect button");
            var password = Control<PasswordBox>("PasswordInput");
            var revealed = Control<TextBox>("RevealedPassword");
            var toggle = Control<Button>("PasswordVisibilityButton");
            Require(password.Visibility == Visibility.Visible && revealed.Visibility == Visibility.Collapsed,
                "Password is masked by default");

            int snapshot = Array.IndexOf(arguments, "--render-sign-in");
            if (snapshot >= 0)
                Render(window, arguments[snapshot + 1]);

            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Control<TextBlock>("StatusText").Text == "Enter your username.", "Inline username validation");
            Control<TextBox>("UsernameBox").Text = "Test user";
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Control<TextBlock>("StatusText").Text == "Enter your password.", "Inline password validation");

            password.Password = "demo password";
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(password.Visibility == Visibility.Collapsed && revealed.Visibility == Visibility.Visible &&
                revealed.Text == "demo password", "Show reveals the password");
            revealed.Text = " edited password ";
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(password.Password == " edited password " && revealed.Text.Length == 0,
                "Hide keeps edits verbatim and clears the visible editor");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<TextBox>("UsernameBox").Clear();
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(password.Visibility == Visibility.Visible && password.Password == " edited password " &&
                revealed.Visibility == Visibility.Collapsed && revealed.Text.Length == 0,
                "Connect masks the password before validation");
            Console.WriteLine("Passed Windows sign-in layout and input validation.");
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }

        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
    }

    private static void Render(Window window, string fileName)
    {
        int width = (int)window.Width;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, double.PositiveInfinity));
        int height = (int)Math.Ceiling(content.DesiredSize.Height);
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(fileName);
        encoder.Save(stream);
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }
}
