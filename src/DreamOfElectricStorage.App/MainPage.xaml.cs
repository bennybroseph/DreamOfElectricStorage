using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Security.Principal;
using System.Threading.Tasks;
using DreamOfElectricStorage.Core;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DreamOfElectricStorage.App;

/// <summary>
/// Hosts the graph canvas: elevation gate → machine indexing → level navigation.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly GraphView _graph = new();
    private bool _isPointerDown;
    private bool _pointerMoved;
    private Vector2 _lastPointer;

    public MainPage()
    {
        InitializeComponent();
        _graph.LevelChanged += OnGraphLevelChanged;
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!IsElevated())
        {
            ElevationBar.IsOpen = true;
            StatusText.Text = "not elevated — indexing unavailable";
            return;
        }

        await BuildIndexAsync();
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async Task BuildIndexAsync()
    {
        StatusText.Text = "indexing…";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Progress<T> is created on the UI thread, so reports marshal back here.
            var progress = new Progress<VolumeIndexProgress>(p =>
                StatusText.Text = p.Completed
                    ? $"{p.Volume} done ({p.NodesIndexed:N0})"
                    : $"{p.Volume} …{p.NodesIndexed:N0}");

            MachineIndex machine = await MachineIndex.BuildAsync(new NtfsDriveIndexer(), progress: progress);
            stopwatch.Stop();

            string skipped = machine.Skipped.Count > 0
                ? $" — skipped: {string.Join(", ", machine.Skipped.Select(s => s.Volume))}"
                : "";
            StatusText.Text = $"{machine.TotalCount:N0} nodes / {machine.Volumes.Count} volumes in {stopwatch.Elapsed.TotalSeconds:F1}s{skipped}";

            _graph.SetIndex(machine);
        }
        catch (UnauthorizedAccessException)
        {
            ElevationBar.IsOpen = true;
            StatusText.Text = "volume access denied";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"indexing failed: {ex.Message}";
        }
    }

    private void OnRestartElevatedClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = true,
                Verb = "runas",
            });
            Application.Current.Exit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User canceled the UAC prompt — stay open, unelevated.
        }
    }

    private void OnGraphLevelChanged()
    {
        BreadcrumbText.Text = _graph.Breadcrumb;
        UpButton.IsEnabled = _graph.CanGoUp;
        GraphCanvas.Invalidate();
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => _graph.GoUp();

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args) =>
        _graph.Draw(sender, args.DrawingSession);

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDown = true;
        _pointerMoved = false;
        _lastPointer = e.GetCurrentPoint(GraphCanvas).Position.ToVector2();
        GraphCanvas.CapturePointer(e.Pointer);
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerDown)
            return;

        Vector2 current = e.GetCurrentPoint(GraphCanvas).Position.ToVector2();
        Vector2 delta = current - _lastPointer;
        if (delta.LengthSquared() > 4)
            _pointerMoved = true; // a drag, not a click

        _graph.Pan(delta);
        _lastPointer = current;
        GraphCanvas.Invalidate();
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDown = false;
        GraphCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void OnCanvasWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(GraphCanvas);
        _graph.Zoom(point.Properties.MouseWheelDelta, point.Position.ToVector2());
        GraphCanvas.Invalidate();
    }

    private void OnCanvasTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_pointerMoved)
            return; // end of a pan drag, not a node click

        _graph.OnTapped(e.GetPosition(GraphCanvas).ToVector2());
    }
}
