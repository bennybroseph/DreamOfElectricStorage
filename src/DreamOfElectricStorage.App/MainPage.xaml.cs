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

/// <summary>One search hit shown in the AutoSuggestBox dropdown.</summary>
public sealed record SearchResult(string Name, string Path, VolumeIndex Volume, ulong Frn);

/// <summary>
/// Hosts the graph canvas: elevation gate → machine indexing → level navigation,
/// search, and open/reveal actions.
/// </summary>
public sealed partial class MainPage : Page
{
    private const int SearchDebounceMs = 300;
    private const int SearchMaxResults = 100;

    private readonly GraphView _graph = new();
    private bool _isPointerDown;
    private bool _pointerMoved;
    private Vector2 _lastPointer;
    private MachineIndex? _machine;
    private Microsoft.UI.Xaml.DispatcherTimer? _searchDebounce;
    private DateTimeOffset _lastDrill = DateTimeOffset.MinValue;

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

    private CancellationTokenSource? _watchCts;
    private long _liveChanges;

    private async Task BuildIndexAsync()
    {
        // Stop watchers from any previous build (journal-overflow rebuild path).
        _watchCts?.Cancel();
        _watchCts = new CancellationTokenSource();

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

            _machine = machine;
            _graph.SetIndex(machine);
            SearchBox.IsEnabled = true;

            _liveChanges = 0;
            foreach (VolumeIndex volume in machine.Volumes.Where(v => v.Journal is not null))
                _ = WatchVolumeAsync(machine, volume, _watchCts.Token);
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

    /// <summary>
    /// Pumps one volume's journal. Batches are applied on the UI thread — the sole
    /// owner of the index (VolumeIndex is single-writer by contract).
    /// </summary>
    private async Task WatchVolumeAsync(MachineIndex machine, VolumeIndex volume, CancellationToken ct)
    {
        try
        {
            await foreach (UsnChangeBatch batch in machine.Watch(volume, ct))
            {
                if (batch.RequiresRebuild)
                {
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        StatusText.Text = $"{volume.Volume} journal overflow — rebuilding index…";
                        await BuildIndexAsync();
                    });
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    volume.Apply(batch);
                    RefreshChangedSizes(volume, batch);
                    _liveChanges += batch.Entries.Count;
                    StatusText.Text = $"live — {_liveChanges:N0} changes";

                    // Refresh the visible level only when it could be affected.
                    bool visibleTouched = _graph.CurrentVolume == volume &&
                        batch.Entries.Any(e => e.Node.ParentId == _graph.CurrentParentId ||
                                               (_graph.CurrentParentId == VolumeIndex.SyntheticRootId && !volume.TryGetNode(e.Node.ParentId, out _)));
                    if (visibleTouched)
                        _graph.RefreshLevel();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // watcher stopped (rebuild or shutdown)
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => StatusText.Text = $"{volume.Volume} watcher stopped: {ex.Message}");
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

        bool couldDrill = _graph.TryGetNodeAt(e.GetPosition(GraphCanvas).ToVector2()) is { } hit
            && (hit.IsVolumeNode || hit.File.IsDirectory);
        if (couldDrill)
            _lastDrill = DateTimeOffset.UtcNow;

        _graph.OnTapped(e.GetPosition(GraphCanvas).ToVector2());
    }

    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // The first tap of a double-tap already fired Tapped; if it drilled, this event
        // is hitting a node on the NEW level at the same position — ignore it.
        if (DateTimeOffset.UtcNow - _lastDrill < TimeSpan.FromMilliseconds(500))
            return;

        if (_graph.TryGetNodeAt(e.GetPosition(GraphCanvas).ToVector2()) is { IsVolumeNode: false } hit
            && !hit.File.IsDirectory)
        {
            OpenPath(hit.Volume.GetPath(hit.File.Id));
        }
    }

    private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(GraphCanvas);
        if (_graph.TryGetNodeAt(position.ToVector2()) is not { IsVolumeNode: false } hit)
            return;

        string? path = hit.Volume.GetPath(hit.File.Id);
        if (path is null)
            return;

        var menu = new MenuFlyout();

        var open = new MenuFlyoutItem { Text = hit.File.IsDirectory ? "Open in Explorer" : "Open" };
        open.Click += (_, _) => OpenPath(path);
        menu.Items.Add(open);

        var reveal = new MenuFlyoutItem { Text = "Reveal in Explorer" };
        reveal.Click += (_, _) => RevealInExplorer(path);
        menu.Items.Add(reveal);

        var copy = new MenuFlyoutItem { Text = "Copy path" };
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        };
        menu.Items.Add(copy);

        menu.ShowAt(GraphCanvas, position);
    }

    /// <summary>
    /// USN records carry no size, so upserted files land with SizeBytes=0 —
    /// re-stat the trickle of changed files (cheap; dir rollups drift until next rebuild).
    /// </summary>
    private static void RefreshChangedSizes(VolumeIndex volume, UsnChangeBatch batch)
    {
        foreach (UsnJournalEntry entry in batch.Entries)
        {
            if (entry.Node.IsDirectory || !volume.TryGetNode(entry.Node.Id, out FileNode node))
                continue;

            try
            {
                if (volume.GetPath(node.Id) is { } path && new FileInfo(path) is { Exists: true } info)
                    node.SizeBytes = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // transient (file already gone, locked) — size stays as-is
            }
        }
    }

    /// <summary>
    /// Opens via explorer.exe so the child process runs at normal integrity —
    /// this app is elevated, and Process.Start(path) would launch programs as admin.
    /// </summary>
    private void OpenPath(string? path)
    {
        if (path is null)
            return;
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
    }

    private void RevealInExplorer(string path) =>
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });

    // --- search ---

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        _searchDebounce ??= CreateDebounceTimer();
        _searchDebounce.Stop();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            _searchDebounce.Start();
    }

    private Microsoft.UI.Xaml.DispatcherTimer CreateDebounceTimer()
    {
        var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchDebounceMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RunSearch();
        };
        return timer;
    }

    private void RunSearch()
    {
        if (_machine is null || string.IsNullOrWhiteSpace(SearchBox.Text))
            return;

        // Runs on the UI thread by design: the index is single-writer/UI-owned
        // (watcher batches Apply here too), so this can't race. Capped + debounced;
        // measured worst case ~150ms — revisit with a cooperative scan only if felt.
        var results = _machine.Search(SearchBox.Text)
            .Take(SearchMaxResults)
            .Select(hit => new SearchResult(
                hit.Node.Name,
                hit.Volume.GetPath(hit.Node.Id) ?? $@"{hit.Volume.Volume}\…\{hit.Node.Name}",
                hit.Volume,
                hit.Node.Id))
            .ToList();

        SearchBox.ItemsSource = results;
    }

    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchResult result)
            NavigateToResult(result);
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SearchResult chosen)
            NavigateToResult(chosen);
        else if (SearchBox.ItemsSource is List<SearchResult> { Count: > 0 } results)
            NavigateToResult(results[0]);
    }

    private void NavigateToResult(SearchResult result)
    {
        SearchBox.Text = result.Name;
        _graph.NavigateTo(result.Volume, result.Frn);
    }
}
