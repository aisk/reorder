using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Reorder;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Initial size in effective pixels, scaled for the monitor DPI.
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1200 * scale), (int)(780 * scale)));

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));

        AppWindow.Closing += AppWindow_Closing;
    }

    private bool _closeConfirmed;

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || RootFrame.Content is not MainPage page || !page.HasPendingChanges) return;
        args.Cancel = true;
        if (await page.ConfirmDiscardAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
