using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FilePoint.Windows;

internal static partial class UiTheme
{
    internal static void Load(Application application)
    {
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/FilePoint.Windows;component/UiTheme.xaml")
        });
    }

    internal static void StyleCaption(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (!SystemParameters.HighContrast)
                DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, 1, sizeof(int));
        };
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, in int value, int size);
}
