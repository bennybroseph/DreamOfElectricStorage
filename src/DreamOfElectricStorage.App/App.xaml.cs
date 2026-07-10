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

    /// <summary>"--stress=N": adds a flat N-file dir to the demo data (perf probing).</summary>
    public static int StressCount { get; } = ParseStress();

    private static int ParseStress()
    {
        foreach (string arg in System.Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith("--stress=", System.StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg["--stress=".Length..], out int count))
                return count;
        }
        return 0;
    }

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
    private SettingsStore? _settings;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _settings = SettingsStore.Load();
        RestoreWindowPlacement(_window.AppWindow);
        _window.AppWindow.Closing += (s, _) => SaveWindowPlacement(s);

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

    private void RestoreWindowPlacement(Microsoft.UI.Windowing.AppWindow appWindow)
    {
        if (_settings is null)
            return;
        if (_settings.WindowWidth > 0 && _settings.WindowHeight > 0)
        {
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                _settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight));
        }
        if (_settings.WindowMaximized && appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private void SaveWindowPlacement(Microsoft.UI.Windowing.AppWindow appWindow)
    {
        if (_settings is null)
            return;
        bool maximized = appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized };
        _settings.WindowMaximized = maximized;
        if (!maximized) // capture the restored rectangle only when not maximized
        {
            _settings.WindowWidth = appWindow.Size.Width;
            _settings.WindowHeight = appWindow.Size.Height;
            _settings.WindowX = appWindow.Position.X;
            _settings.WindowY = appWindow.Position.Y;
        }
        _settings.Save();
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
