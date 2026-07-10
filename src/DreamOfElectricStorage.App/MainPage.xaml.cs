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

/// <summary>One search hit shown in the AutoSuggestBox dropdown (also reused by the Related panel).</summary>
public sealed record SearchResult(string Name, string Path, VolumeIndex Volume, ulong Frn);

/// <summary>One legend swatch for the active color mode.</summary>
public sealed record LegendEntry(string Label, Microsoft.UI.Xaml.Media.SolidColorBrush Brush);

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
        _graph.RedrawNeeded += () => GraphCanvas.Invalidate(); // animation clock → repaint
        _graph.Images.Ready += () => DispatcherQueue.TryEnqueue(() => GraphCanvas.Invalidate()); // icons arrive off-thread
        Loaded += OnPageLoaded;
        RefreshLegend();

        var homeKey = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Home };
        homeKey.Invoked += (_, args) =>
        {
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is TextBox)
                return;
            args.Handled = true;
            _graph.ZoomHome();
        };
        KeyboardAccelerators.Add(homeKey);

        var deleteKey = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Delete };
        deleteKey.Invoked += async (_, args) =>
        {
            // Don't hijack Delete while the user is editing text (search box, rename dialog).
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is TextBox)
                return;

            args.Handled = true;
            if (_graph.SelectedFrn is { } frn && _graph.CurrentVolume is { } volume
                && volume.TryGetNode(frn, out FileNode node) && volume.GetPath(frn) is { } path)
            {
                await ConfirmAndDeleteAsync(node.Name, path);
            }
        };
        KeyboardAccelerators.Add(deleteKey);
    }

    private void OnColorModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LegendItems is null)
            return; // fires during InitializeComponent

        _graph.ColorMode = (GraphColorMode)ColorModeCombo.SelectedIndex;
        RefreshLegend();
        GraphCanvas.Invalidate();
    }

    private void RefreshLegend()
    {
        LegendItems.ItemsSource = _graph.ColorMode switch
        {
            GraphColorMode.Type => GraphView.TypePalette
                .Select(p => new LegendEntry(p.Label, new Microsoft.UI.Xaml.Media.SolidColorBrush(p.Color))).ToList(),
            GraphColorMode.Age => GraphView.AgePalette
                .Select(p => new LegendEntry(p.Label, new Microsoft.UI.Xaml.Media.SolidColorBrush(p.Color))).ToList(),
            _ => new List<LegendEntry>(),
        };
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (App.DemoMode)
        {
            _testPipe ??= new TestPipeServer(DispatchTestCommandAsync); // harness command channel
            await BuildIndexAsync(); // synthetic volumes, no elevation needed
            return;
        }

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

            MachineIndex machine = App.DemoMode
                ? await MachineIndex.BuildAsync(new DemoDriveIndexer(App.StressCount), DemoDriveIndexer.Volumes, progress: progress)
                : await MachineIndex.BuildAsync(new NtfsDriveIndexer(), progress: progress);
            stopwatch.Stop();

            string skipped = machine.Skipped.Count > 0
                ? $" — skipped: {string.Join(", ", machine.Skipped.Select(s => s.Volume))}"
                : "";
            StatusText.Text = $"{machine.TotalCount:N0} nodes / {machine.Volumes.Count} volumes in {stopwatch.Elapsed.TotalSeconds:F1}s{skipped}"
                + (App.DemoMode ? " — DEMO DATA" : "");

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
                    _graph.InvalidatePreviews(); // children/sizes changed — child packs are stale
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
        BreadcrumbTrail.ItemsSource = _graph.BreadcrumbSegments;
        UpButton.IsEnabled = _graph.CanGoUp;
        GraphCanvas.Invalidate();
    }

    private void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args) =>
        _graph.NavigateToSegment(args.Index);

    private void OnUpClick(object sender, RoutedEventArgs e) => _graph.GoUp();

    private void OnHomeClick(object sender, RoutedEventArgs e) => _graph.ZoomHome();

    // --- pinned places ---

    private readonly PinStore _pins = new();

    private void OnPinsFlyoutOpening(object sender, object e)
    {
        PinsFlyout.Items.Clear();

        string? current = _graph.CanGoUp || _graph.CurrentVolume is not null ? _graph.Breadcrumb : null;
        var toggle = new MenuFlyoutItem
        {
            Text = current is not null && _pins.Contains(current)
                ? $"Unpin  {current}"
                : $"Pin  {current ?? "(nothing to pin here)"}",
            IsEnabled = current is not null,
        };
        toggle.Click += (_, _) => { if (current is not null) _pins.Toggle(current); };
        PinsFlyout.Items.Add(toggle);

        if (_pins.Pins.Count > 0)
            PinsFlyout.Items.Add(new MenuFlyoutSeparator());
        foreach (string pin in _pins.Pins)
        {
            var item = new MenuFlyoutItem { Text = pin };
            item.Click += (_, _) => NavigateToPin(pin);
            PinsFlyout.Items.Add(item);
        }
    }

    private void NavigateToPin(string path)
    {
        if (_machine is null)
            return;
        foreach (VolumeIndex volume in _machine.Volumes)
        {
            if (volume.FindByPath(path) is { } frn)
            {
                _graph.NavigateInto(volume, frn);
                GraphCanvas.Invalidate();
                return;
            }
        }
        StatusText.Text = $"pin not found: {path}";
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args) =>
        _graph.Draw(sender, args.DrawingSession);

    private bool _pressedOnNode;

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Vector2 pressed = e.GetCurrentPoint(GraphCanvas).Position.ToVector2();
        if (_graph.IsInMinimap(pressed))
            return; // minimap click = jump (handled in TapAt); never a pan/drag

        _isPointerDown = true;
        _pointerMoved = false;
        _lastPointer = pressed;
        // Node press = potential drag-move; empty-space press = pan.
        _pressedOnNode = _graph.TryGetNodeAt(_lastPointer) is { IsVolumeNode: false };
        GraphCanvas.CapturePointer(e.Pointer);
    }

    private Vector2 _lastHoverPoint;

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Vector2 current = e.GetCurrentPoint(GraphCanvas).Position.ToVector2();

        if (!_isPointerDown)
        {
            // Hover reveal: re-hit-test only after meaningful movement.
            if ((current - _lastHoverPoint).LengthSquared() > 16)
            {
                _lastHoverPoint = current;
                if (_graph.SetHover(current))
                    GraphCanvas.Invalidate();
            }
            return;
        }

        Vector2 delta = current - _lastPointer;
        if (delta.LengthSquared() > 4)
            _pointerMoved = true; // a drag, not a click

        if (_graph.IsDragging)
        {
            _graph.UpdateDrag(current);
        }
        else if (_pressedOnNode)
        {
            // Node drag starts only past a threshold so plain clicks still work.
            if ((current - _lastPointer).LengthSquared() > 64 && _graph.BeginDrag(_lastPointer))
                _graph.UpdateDrag(current);
        }
        else
        {
            _graph.Pan(delta);
            _lastPointer = current;
        }

        GraphCanvas.Invalidate();
    }

    private async void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDown = false;
        _pressedOnNode = false;
        GraphCanvas.ReleasePointerCapture(e.Pointer);

        if (!_graph.IsDragging)
            return;

        var drop = _graph.EndDrag();
        GraphCanvas.Invalidate();
        if (drop is { } move)
            await ConfirmAndMoveAsync(move.Source, move.Target);
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

        TapAt(e.GetPosition(GraphCanvas).ToVector2());
    }

    /// <summary>Shared tap path (real Tapped event + test channel): drill/select/deselect.</summary>
    private void TapAt(Vector2 point)
    {
        if (_graph.TryMinimapJump(point))
            return;

        bool couldDrill = _graph.TryGetNodeAt(point) is { } hit
            && (hit.IsVolumeNode || hit.File.IsDirectory);
        if (couldDrill)
            _lastDrill = DateTimeOffset.UtcNow;

        GraphView.NodeHit? selected = _graph.OnTapped(point);
        GraphCanvas.Invalidate();

        if (selected is { } fileHit)
            ShowRelatedPanel(fileHit);
        else
            HideRelatedPanel();
    }

    /// <summary>Machine-wide duplicate scan for the selected file → side panel.</summary>
    private void ShowRelatedPanel(GraphView.NodeHit hit)
    {
        if (_machine is null)
            return;

        // UI-thread sync on purpose (single-writer contract); ~search cost, only on click.
        var duplicates = _machine.FindDuplicates(hit.Volume, hit.File);

        RelatedTitle.Text = $"Related — {hit.File.Name}";
        if (duplicates.Count == 0)
        {
            RelatedSummary.Text = "No duplicate candidates on any indexed volume (same name + size).";
            RelatedList.ItemsSource = null;
        }
        else
        {
            long wasted = duplicates.Count * hit.File.SizeBytes;
            RelatedSummary.Text = $"{duplicates.Count} duplicate candidate(s), ~{wasted / (double)(1L << 30):F2} GB duplicated. Click to jump.";
            RelatedList.ItemsSource = duplicates
                .Select(d => new SearchResult(d.Node.Name, d.Volume.GetPath(d.Node.Id) ?? d.Volume.Volume, d.Volume, d.Node.Id))
                .ToList();
        }
        RelatedPanel.Visibility = Visibility.Visible;
    }

    private void HideRelatedPanel()
    {
        RelatedPanel.Visibility = Visibility.Collapsed;
        RelatedList.ItemsSource = null;
    }

    private void OnRelatedItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResult result)
        {
            _graph.NavigateTo(result.Volume, result.Frn);
            GraphCanvas.Invalidate();
        }
    }

    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // The first tap of a double-tap already fired Tapped; if it drilled, this event
        // is hitting a node on the NEW level at the same position — ignore it.
        if (DateTimeOffset.UtcNow - _lastDrill < TimeSpan.FromMilliseconds(500))
            return;

        DoubleTapAt(e.GetPosition(GraphCanvas).ToVector2());
    }

    /// <summary>Shared double-tap path (real event + test channel): open files.</summary>
    private void DoubleTapAt(Vector2 point)
    {
        if (_graph.TryGetNodeAt(point) is { IsVolumeNode: false } hit && !hit.File.IsDirectory)
            OpenPath(hit.Volume.GetPath(hit.File.Id));
    }

    private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        RightTapAt(e.GetPosition(GraphCanvas));
    }

    /// <summary>Shared right-tap path (real event + test channel): node context menu.</summary>
    private void RightTapAt(Windows.Foundation.Point position)
    {
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

        menu.Items.Add(new MenuFlyoutSeparator());

        var rename = new MenuFlyoutItem { Text = "Rename…" };
        rename.Click += async (_, _) => await RenameDialogAsync(hit.File.Name, path);
        menu.Items.Add(rename);

        var delete = new MenuFlyoutItem { Text = "Delete…" };
        delete.Click += async (_, _) => await ConfirmAndDeleteAsync(hit.File.Name, path);
        menu.Items.Add(delete);

        menu.ShowAt(GraphCanvas, position);
    }

    // --- file operations (destructive: always behind a dialog; the USN watcher updates the graph) ---

    private async Task RenameDialogAsync(string currentName, string path)
    {
        var nameBox = new TextBox { Text = currentName, SelectionStart = 0, SelectionLength = Path.GetFileNameWithoutExtension(currentName).Length };
        var dialog = new ContentDialog
        {
            Title = "Rename",
            Content = nameBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || nameBox.Text == currentName)
            return;

        await RunFileOperationAsync(
            () => FileOperations.Rename(path, nameBox.Text),
            $"renamed to {nameBox.Text}");
    }

    private async Task ConfirmAndDeleteAsync(string name, string path)
    {
        string warning = IsSystemLocation(path)
            ? "\n\n⚠ This is a Windows system location — deleting here can break the OS."
            : "";
        var dialog = new ContentDialog
        {
            Title = $"Delete {name}?",
            Content = $"{path}\n\nIt will be sent to the Recycle Bin.{warning}",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await RunFileOperationAsync(
            () => { FileOperations.DeleteToRecycleBin(path); return $"{name} → Recycle Bin"; },
            null);
        HideRelatedPanel();
        _graph.ClearSelection();
        GraphCanvas.Invalidate();
    }

    private async Task ConfirmAndMoveAsync(GraphView.NodeHit source, GraphView.NodeHit target)
    {
        string? sourcePath = source.Volume.GetPath(source.File.Id);
        string? targetPath = target.Volume.GetPath(target.File.Id);
        if (sourcePath is null || targetPath is null)
            return;

        var dialog = new ContentDialog
        {
            Title = $"Move {source.File.Name}?",
            Content = $"{sourcePath}\n→ {targetPath}\\",
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await RunFileOperationAsync(
            () => FileOperations.Move(sourcePath, targetPath),
            $"moved {source.File.Name} → {target.File.Name}");
    }

    /// <summary>Runs a file operation off the UI thread; success → status line, failure → dialog.</summary>
    private async Task RunFileOperationAsync(Func<string> operation, string? successStatus)
    {
        try
        {
            string result = await Task.Run(operation);
            StatusText.Text = successStatus ?? result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or FileNotFoundException or DirectoryNotFoundException or OperationCanceledException)
        {
            await new ContentDialog
            {
                Title = "Operation failed",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            }.ShowAsync();
        }
    }

    private static bool IsSystemLocation(string path) =>
        path.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(@"C:\ProgramData", StringComparison.OrdinalIgnoreCase);

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
                {
                    node.SizeBytes = info.Length;
                    node.LastWriteFileTime = info.LastWriteTimeUtc.ToFileTimeUtc();
                }
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

    // --- test channel (demo mode only) ---

    private TestPipeServer? _testPipe;

    /// <summary>Marshals a pipe command onto the UI thread (single-writer contract).</summary>
    private Task<string> DispatchTestCommandAsync(string line)
    {
        var tcs = new TaskCompletionSource<string>();
        bool queued = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                tcs.SetResult(ExecuteTestCommand(line));
            }
            catch (Exception ex)
            {
                tcs.SetResult($"err {ex.Message}");
            }
        });
        if (!queued)
            tcs.SetResult("err dispatcher queue rejected");
        return tcs.Task;
    }

    /// <summary>
    /// Coordinates are canvas-logical (DIPs, relative to GraphCanvas). `state` reports
    /// canvasOrigin (physical px in the captured window) + scale for converting from
    /// CaptureCli screenshots: canvas = (screenshotPx - origin) / scale.
    /// Commands reuse the real handlers (TapAt/RightTapAt/…) — only XAML's raw
    /// pointer routing is bypassed; cover that with an occasional real-input pass.
    /// </summary>
    private string ExecuteTestCommand(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts[0].ToLowerInvariant();
        float Arg(int i) => float.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        Vector2 Point(int i) => new(Arg(i), Arg(i + 1));

        switch (verb)
        {
            case "tap":
                TapAt(Point(1));
                return "ok";

            case "dbltap":
                DoubleTapAt(Point(1));
                return "ok";

            case "righttap":
                RightTapAt(new Windows.Foundation.Point(Arg(1), Arg(2)));
                return "ok";

            case "hover":
                _graph.SetHover(Point(1));
                GraphCanvas.Invalidate();
                return "ok";

            case "wheel": // wheel <delta> <x> <y>
                _graph.Zoom(Arg(1), Point(2));
                GraphCanvas.Invalidate();
                return "ok";

            case "drag": // drag <x1> <y1> <x2> <y2> — full move flow; confirm dialog opens unawaited
                if (!_graph.BeginDrag(Point(1)))
                    return "ok no-node";
                _graph.UpdateDrag(Point(3));
                var drop = _graph.EndDrag();
                GraphCanvas.Invalidate();
                if (drop is { } move)
                {
                    _ = ConfirmAndMoveAsync(move.Source, move.Target);
                    return $"ok drop={move.Target.File.Name} (dialog open)";
                }
                return "ok no-target";

            case "up":
                _graph.GoUp();
                return "ok";

            case "home":
                _graph.ZoomHome();
                return "ok";

            case "deselect":
                _graph.ClearSelection();
                HideRelatedPanel();
                GraphCanvas.Invalidate();
                return "ok";

            case "colormode": // colormode <type|age|none>
                ColorModeCombo.SelectedIndex = parts[1].ToLowerInvariant() switch
                {
                    "age" => 1, "none" => 2, _ => 0,
                };
                return "ok";

            case "search": // search <text> — runs the real search, returns hits
            {
                SearchBox.Text = string.Join(' ', parts.Skip(1));
                RunSearch();
                if (SearchBox.ItemsSource is not List<SearchResult> results || results.Count == 0)
                    return "0 results";
                return $"{results.Count} results\n" + string.Join('\n', results.Select(r => $"{r.Name} | {r.Path} | {r.Volume.Volume} {r.Frn}"));
            }

            case "searchgo": // searchgo <text> — search + navigate to the first hit
            {
                SearchBox.Text = string.Join(' ', parts.Skip(1));
                RunSearch();
                if (SearchBox.ItemsSource is not List<SearchResult> { Count: > 0 } results)
                    return "0 results";
                NavigateToResult(results[0]);
                return $"ok {results[0].Path}";
            }

            case "nodes": // visible level in canvas coords: name | x,y | screenRadius | dir/file
            {
                var nodes = _graph.GetVisibleNodes();
                return $"{nodes.Count} nodes\n" + string.Join('\n', nodes.Select(n =>
                    $"{n.Name} | {n.ScreenPosition.X:F0},{n.ScreenPosition.Y:F0} | r={n.ScreenRadius:F0} | {(n.IsDirectory ? "dir" : "file")} | {n.Frn}"));
            }

            case "perf":
                return _graph.PerfReport();

            case "crumb": // crumb <index> — clickable-breadcrumb navigation
                _graph.NavigateToSegment(int.Parse(parts[1]));
                return $"ok {string.Join(" > ", _graph.BreadcrumbSegments)}";

            case "pin": // toggle pin on the current location
            {
                string current = _graph.Breadcrumb;
                if (current == "Computer")
                    return "err nothing to pin at machine level";
                return _pins.Toggle(current) ? $"pinned {current}" : $"unpinned {current}";
            }

            case "pins":
                return _pins.Pins.Count == 0 ? "0 pins" : string.Join('\n', _pins.Pins);

            case "pinnav": // pinnav <index>
                NavigateToPin(_pins.Pins[int.Parse(parts[1])]);
                return "ok";

            case "state":
            {
                var origin = GraphCanvas.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                double scale = XamlRoot.RasterizationScale;
                return $"breadcrumb={_graph.Breadcrumb}\ncanUp={_graph.CanGoUp}\n" +
                    $"zoom={_graph.Camera.Zoom:F3} pan={_graph.Camera.Pan.X:F0},{_graph.Camera.Pan.Y:F0} min={_graph.Camera.MinZoom:F3}\n" +
                    $"selected={_graph.SelectedFrn?.ToString() ?? "none"}\n" +
                    $"relatedPanel={RelatedPanel.Visibility}\n" +
                    $"canvasOrigin={origin.X * scale:F0},{origin.Y * scale:F0} scale={scale:F2}\n" +
                    $"status={StatusText.Text}";
            }

            default:
                return $"err unknown command '{verb}' — tap dbltap righttap hover wheel drag up home deselect colormode search searchgo nodes state";
        }
    }
}
