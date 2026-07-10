using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DreamOfElectricStorage.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// "--demo": run on deterministic synthetic volumes, no elevation needed.
    /// Exists for automated UI verification (screenshots + scripted input).
    /// </summary>
    public static bool DemoMode { get; } =
        System.Linq.Enumerable.Contains(System.Environment.GetCommandLineArgs(), "--demo", System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "--background": show without activating and at the bottom of the z-order, so
    /// harness runs never steal focus or cover the user's windows. WGC captures
    /// occluded windows, so the harness sees it regardless.
    /// </summary>
    public static bool BackgroundMode { get; } =
        System.Linq.Enumerable.Contains(System.Environment.GetCommandLineArgs(), "--background", System.StringComparer.OrdinalIgnoreCase);

    private Window? _window;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        if (BackgroundMode)
        {
            _window.AppWindow.Show(activateWindow: false);
            SendToBottom(_window);
        }
        else
        {
            _window.Activate();
        }
    }

    private static void SendToBottom(Window window)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private const nint HWND_BOTTOM = 1;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
