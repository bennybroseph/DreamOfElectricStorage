using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DreamOfElectricStorage.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.UI;

namespace DreamOfElectricStorage.App;

public enum GraphColorMode { Type, Age, None }

/// <summary>
/// Canvas-side state and logic for the node graph: current level, layout, animated
/// camera, per-frame animation, drawing, and hit-testing. MainPage owns the
/// CanvasControl and forwards events; it repaints whenever <see cref="RedrawNeeded"/> fires.
/// All animation runs on the UI thread (single-writer index contract).
/// </summary>
public sealed class GraphView
{
    private const float DirectoryRadius = 26f;
    private const float FileRadius = 12f;
    private const float PackPadding = 7f;                  // min world-unit gap between packed nodes

    // Semantic zoom: directory circles past RevealStart px show their children packed
    // inside; zooming until a dir fills the viewport re-roots the level into it.
    private const float RevealStartScreenR = 64f;
    private const float RevealFullScreenR = 96f;
    private const float PreviewFit = 0.88f;                // child pack fills this fraction of the parent circle
    private const int MaxPreviewDepth = 3;
    private const int MaxPreviewChildren = 512;            // biggest N previewed for huge dirs
    private const int PreviewCacheCap = 256;
    private const float AutoDrillViewportFrac = 0.72f;     // node screen radius that triggers re-root in
    private const float AutoUpViewportFrac = 0.33f;        // level screen radius that triggers re-root out
    private const float LabelMinScreenRadius = 9f;         // hide labels when nodes get tiny
    private const float HoverSwell = 0.18f;                // hovered node grows 18%
    private const float FadeOutSeconds = 0.18f;
    private const float EntranceSeconds = 0.45f;
    private const float EntranceStagger = 0.5f;            // fraction of entrance spent staggering
    private const float PulseSeconds = 1.2f;

    private static readonly Color DirectoryColor = Color.FromArgb(255, 79, 209, 255);   // node-graph cyan
    private static readonly Color FileColor = Color.FromArgb(255, 148, 155, 170);
    private static readonly Color LabelColor = Color.FromArgb(255, 230, 234, 240);
    private static readonly Color EdgeColor = Color.FromArgb(60, 79, 209, 255);
    private static readonly Color HighlightColor = Color.FromArgb(255, 255, 196, 0);    // search-hit ring
    private static readonly Color RelationColor = Color.FromArgb(255, 255, 123, 193);   // dup/similar glow
    private static readonly Color DropTargetColor = Color.FromArgb(255, 123, 232, 123); // drag-move target ring

    /// <summary>Type-mode palette. Order/labels surface in the legend.</summary>
    public static readonly IReadOnlyList<(FileTypeCategory Category, string Label, Color Color)> TypePalette =
    [
        (FileTypeCategory.Image, "Images", Color.FromArgb(255, 123, 232, 123)),
        (FileTypeCategory.Video, "Video", Color.FromArgb(255, 255, 169, 77)),
        (FileTypeCategory.Audio, "Audio", Color.FromArgb(255, 183, 140, 255)),
        (FileTypeCategory.Document, "Documents", Color.FromArgb(255, 255, 224, 102)),
        (FileTypeCategory.Code, "Code", Color.FromArgb(255, 77, 212, 192)),
        (FileTypeCategory.Archive, "Archives", Color.FromArgb(255, 201, 162, 39)),
        (FileTypeCategory.Executable, "Executables", Color.FromArgb(255, 255, 107, 107)),
        (FileTypeCategory.Model, "AI models", Color.FromArgb(255, 255, 123, 193)),
        (FileTypeCategory.GameData, "Game data", Color.FromArgb(255, 107, 155, 255)),
        (FileTypeCategory.System, "System", Color.FromArgb(255, 138, 143, 152)),
        (FileTypeCategory.Other, "Other", Color.FromArgb(255, 181, 188, 201)),
    ];

    /// <summary>Age-mode ramp, hot→cold. Thresholds in days; -1 = unknown bucket.</summary>
    public static readonly IReadOnlyList<(double MaxAgeDays, string Label, Color Color)> AgePalette =
    [
        (1, "Today", Color.FromArgb(255, 255, 107, 107)),
        (7, "This week", Color.FromArgb(255, 255, 169, 77)),
        (31, "This month", Color.FromArgb(255, 255, 224, 102)),
        (366, "This year", Color.FromArgb(255, 77, 212, 192)),
        (double.MaxValue, "Older", Color.FromArgb(255, 107, 114, 128)),
        (-1, "Unknown", Color.FromArgb(255, 181, 188, 201)),
    ];

    private static readonly CanvasTextFormat LabelFormat = new()
    {
        FontSize = 12,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Top,
    };

    // Category/Path are cached at build time — string work is too hot for the draw loop.
    // Path is null past MaxLevelPaths (tail nodes are too small for icons anyway).
    private readonly record struct GraphNode(FileNode File, VolumeIndex Volume, Vector2 Position, float Radius, string Label, FileTypeCategory Category, string? Path);

    /// <summary>A node the pointer landed on. IsVolumeNode = machine-level drive circle (no real FRN).</summary>
    public readonly record struct NodeHit(VolumeIndex Volume, FileNode File, bool IsVolumeNode);

    /// <summary>Visible-level node in screen coordinates — lets the test harness aim without reading pixels.</summary>
    public readonly record struct VisibleNode(string Name, ulong Frn, Vector2 ScreenPosition, float ScreenRadius, bool IsDirectory);

    private MachineIndex? _machine;
    private readonly List<GraphNode> _nodes = [];
    private float _levelRadius;                              // enclosing circle of the packed level
    private ulong? _highlightedFrn;

    // Child-pack previews: lazily computed per dir, budgeted per frame so a wall of
    // dirs crossing the reveal threshold can't hitch a frame.
    private sealed record ChildPack(FileNode[] Files, Vector2[] Positions, float[] Radii, FileTypeCategory[] Categories, string?[] Paths, float PackRadius);
    private readonly Dictionary<(VolumeIndex Volume, ulong Frn), ChildPack> _previewCache = [];
    private int _previewBudget;

    // Relationship reveal: selection is sticky, hover is transient; selection wins as anchor.
    private ulong? _hoveredFrn;
    private ulong? _selectedFrn;
    private readonly HashSet<ulong> _relatedFrns = [];

    // Drag-move: dragged node ghost + current directory drop target.
    private NodeHit? _dragSource;
    private Vector2 _dragGhostScreen;
    private Vector2 _dragGhostTarget;
    private ulong? _dropTargetFrn;

    // Animation state (all UI-thread).
    private readonly AnimationClock _clock = new();
    public GraphCamera Camera { get; } = new();
    private readonly Tween _fadeOut = new(FadeOutSeconds, Easings.CubicInOut);
    private readonly Tween _entrance = new(EntranceSeconds, Easings.CubicOut);
    private Action? _pendingLevelSwap;                       // runs when fade-out completes
    private readonly Dictionary<ulong, float> _swell = [];   // per-node hover swell 0..1
    private float _revealAlpha;                              // relation edges/rings fade
    private float _pulseElapsed = PulseSeconds;              // selection/highlight ring pulse
    private Vector2 _lastViewport = new(1200, 800);
    private bool _hasDrawn;
    private bool _pendingInitialFit;
    private readonly List<Vector4> _labelRects = [];         // per-frame world-space label rects (L,T,R,B)

    // Perf probe: Draw wall time per frame + last level rebuild/pack time.
    private readonly System.Diagnostics.Stopwatch _drawTimer = new();
    private readonly List<double> _drawSamples = new(1024);
    private double _lastRebuildMs;
    private int _drawnCircles;                               // circles actually drawn last frame (post-cull)

    // Sprite-batched circles (V4): one white AA circle texture, thousands of tinted
    // instances per frame. Circles bigger than VectorCircleScreenR px stay crisp
    // vector fills (sprites are never upscaled past their source resolution).
    private const float SpriteSourceRadius = 64f;
    private const float SpriteSourceSize = 144f;             // circle + AA padding
    private const float VectorCircleScreenR = 64f;
    private const float MinDrawScreenR = 0.35f;              // sub-pixel cutoff (radii descend → break)
    private const int MaxCirclesPerFrame = 24000;            // biggest-first budget — only sub-px dust drops
    private CanvasRenderTarget? _circleSprite;
    private CanvasRenderTarget? _squircleSprite;             // shape-by-kind: files render as rounded squares
    private readonly List<(Vector2 Pos, float Radius, Color Color, float Stroke)> _ringQueue = [];
    private readonly List<(int Index, float Radius, bool Force)> _labelQueue = [];

    // Node identity (V5): shell icons/thumbnails + child-count badges.
    private const float IconMinScreenR = 20f;                // node must be this big for an icon/thumb
    private const float BadgeMinScreenR = 24f;               // dir count badge window: [this, RevealStart)
    private const int MaxLevelPaths = 2000;                  // paths resolved at rebuild (biggest-first; icons only matter big)
    private static readonly Color BadgeColor = Color.FromArgb(215, 235, 240, 248);
    private static readonly CanvasTextFormat BadgeFormat = new()
    {
        FontSize = 12,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center,
    };
    private readonly List<(Vector2 WorldPos, string Text)> _badgeQueue = [];

    /// <summary>Shell image source. MainPage marshals its Ready event to a canvas invalidate.</summary>
    public ShellImageLoader Images { get; } = new();

    // Minimap (V6): appears when the level overflows the viewport; click to jump.
    private const float MinimapSize = 176f;
    private const float MinimapMargin = 16f;
    private const int MinimapMaxDots = 600;
    private float _minimapAlpha;                             // eased toward Show target in Advance
#if DEBUG
    private long _frameCounter;
#endif

    // Navigation state: null volume = machine level (volumes as nodes).
    private VolumeIndex? _currentVolume;
    private readonly Stack<ulong> _parentTrail = new();

    public event Action? LevelChanged;

    /// <summary>Fires every animation frame; the owner should invalidate the canvas.</summary>
    public event Action? RedrawNeeded;

    public GraphColorMode ColorMode { get; set; } = GraphColorMode.Type;

    public ulong? SelectedFrn => _selectedFrn;

    public GraphView()
    {
        _clock.Tick += Advance;
        _clock.IsIdle = () =>
            Camera.Settled && !_fadeOut.Running && !_entrance.Running && _pendingLevelSwap is null
            && _swell.Count == 0 && RevealSettled && _pulseElapsed >= PulseSeconds && _dragSource is null
            && MinimapSettled;
    }

    private bool MinimapSettled
    {
        get
        {
            float target = _levelRadius * Camera.Zoom > 0.9f * MathF.Min(_lastViewport.X, _lastViewport.Y) ? 1f : 0f;
            return MathF.Abs(_minimapAlpha - target) < 0.01f;
        }
    }

    private bool RevealSettled
    {
        get
        {
            float target = (_selectedFrn ?? _hoveredFrn) is not null ? 1f : 0f;
            return MathF.Abs(_revealAlpha - target) < 0.01f;
        }
    }

    private void RequestFrames() => _clock.RequestFrames();

    /// <summary>Per-frame update. Runs on the UI thread via CompositionTarget.Rendering.</summary>
    private void Advance(float dt)
    {
        Camera.Advance(dt);
        _fadeOut.Advance(dt);
        _entrance.Advance(dt);

        if (!_fadeOut.Running && _pendingLevelSwap is { } swap)
        {
            _pendingLevelSwap = null;
            swap(); // rebuilds the level, starts the entrance
        }

        // Hover swell eases toward 1 for the hovered node, back to 0 for everything else.
        float approach = Easings.Approach(14f, dt);
        List<ulong>? done = null;
        foreach (ulong frn in _swell.Keys)
        {
            float target = frn == _hoveredFrn ? 1f : 0f;
            float value = _swell[frn] + (target - _swell[frn]) * approach;
            if (target == 0f && value < 0.02f)
                (done ??= []).Add(frn);
            else
                _swell[frn] = value;
        }
        if (done is not null)
            foreach (ulong frn in done)
                _swell.Remove(frn);

        float revealTarget = (_selectedFrn ?? _hoveredFrn) is not null ? 1f : 0f;
        _revealAlpha += (revealTarget - _revealAlpha) * Easings.Approach(12f, dt);
        if (RevealSettled)
            _revealAlpha = revealTarget;

        if (_pulseElapsed < PulseSeconds)
            _pulseElapsed += dt;

        if (_dragSource is not null)
            _dragGhostScreen += (_dragGhostTarget - _dragGhostScreen) * Easings.Approach(20f, dt);

        // Minimap fades in when the level overflows the viewport (you can be lost).
        float minimapTarget = _levelRadius * Camera.Zoom > 0.9f * MathF.Min(_lastViewport.X, _lastViewport.Y) ? 1f : 0f;
        _minimapAlpha += (minimapTarget - _minimapAlpha) * Easings.Approach(10f, dt);
        if (MathF.Abs(_minimapAlpha - minimapTarget) < 0.01f)
            _minimapAlpha = minimapTarget;

        EvaluateAutoNavigation();

        RedrawNeeded?.Invoke();
    }

    /// <summary>
    /// Trail as clickable segments: ["Computer"], then the volume, then each dir.
    /// Index into this list with <see cref="NavigateToSegment"/>.
    /// </summary>
    public IReadOnlyList<string> BreadcrumbSegments
    {
        get
        {
            var segments = new List<string> { "Computer" };
            if (_currentVolume is null)
                return segments;
            segments.Add(_currentVolume.Volume);
            foreach (ulong frn in _parentTrail.Reverse()) // stack enumerates top-first
                segments.Add(_currentVolume.TryGetNode(frn, out FileNode node) ? node.Name : "?");
            return segments;
        }
    }

    /// <summary>Jump to an ancestor level by breadcrumb index (0 = Computer, 1 = volume root…).</summary>
    public void NavigateToSegment(int index)
    {
        int current = 1 + (_currentVolume is null ? -1 : _parentTrail.Count);
        if (_pendingLevelSwap is not null || index >= current || index < 0)
            return;

        _highlightedFrn = null;
        Camera.FlyTo(Camera.Pan + (_lastViewport / 2f - Camera.Pan) * 0.5f, Camera.Zoom * 0.45f); // recede
        BeginLevelSwap(() =>
        {
            if (index == 0)
            {
                _currentVolume = null;
                _parentTrail.Clear();
            }
            else
            {
                while (_parentTrail.Count > index - 1)
                    _parentTrail.Pop();
            }
            RebuildLevel();
        });
    }

    /// <summary>Lands INSIDE a directory (pins are places to be, not things to look at).</summary>
    public void NavigateInto(VolumeIndex volume, ulong frn)
    {
        if (frn == VolumeIndex.SyntheticRootId)
        {
            BeginLevelSwap(() =>
            {
                _currentVolume = volume;
                _parentTrail.Clear();
                _highlightedFrn = null;
                RebuildLevel();
            });
            return;
        }

        if (!volume.TryGetNode(frn, out FileNode node))
            return;
        if (!node.IsDirectory)
        {
            NavigateTo(volume, frn); // files: highlight in their parent as before
            return;
        }

        BeginLevelSwap(() =>
        {
            _currentVolume = volume;
            _parentTrail.Clear();
            foreach (ulong ancestor in BuildTrailTo(volume, node))
                _parentTrail.Push(ancestor);
            _parentTrail.Push(frn);
            _highlightedFrn = null;
            RebuildLevel();
        });
    }

    /// <summary>Ancestor FRNs root-first (same guards as GetPath).</summary>
    private static List<ulong> BuildTrailTo(VolumeIndex volume, FileNode node)
    {
        var ancestors = new List<ulong>();
        FileNode? current = node;
        for (int depth = 0; current is not null && depth < 512; depth++)
        {
            if (current.ParentId == current.Id || !volume.TryGetNode(current.ParentId, out FileNode parent))
                break;
            ancestors.Add(parent.Id);
            current = parent;
        }
        ancestors.Reverse();
        return ancestors;
    }

    /// <summary>Human-readable location, e.g. "Computer" or "D:\Documents\GitHub".</summary>
    public string Breadcrumb
    {
        get
        {
            if (_currentVolume is null)
                return "Computer";
            if (_parentTrail.Count == 0)
                return _currentVolume.Volume + @"\";
            return _currentVolume.GetPath(_parentTrail.Peek()) ?? _currentVolume.Volume;
        }
    }

    public bool CanGoUp => _currentVolume is not null;

    /// <summary>FRN whose children are currently displayed, or null at machine/root level.</summary>
    public ulong? CurrentParentId => _currentVolume is null ? null
        : _parentTrail.Count == 0 ? VolumeIndex.SyntheticRootId : _parentTrail.Peek();

    public VolumeIndex? CurrentVolume => _currentVolume;

    public void SetIndex(MachineIndex machine)
    {
        _machine = machine;
        _currentVolume = null;
        _parentTrail.Clear();
        _previewCache.Clear();
        RebuildLevel();
    }

    /// <summary>Drops cached child packs — live updates mutate children lists and sizes.</summary>
    public void InvalidatePreviews() => _previewCache.Clear();

    /// <summary>Draw-time stats since the previous call (samples are cleared on read).</summary>
    public string PerfReport()
    {
        if (_drawSamples.Count == 0)
            return "no frames sampled (canvas idle — animate to collect)";

        var sorted = _drawSamples.OrderBy(s => s).ToList();
        double avg = sorted.Average();
        double p95 = sorted[(int)(sorted.Count * 0.95)];
        double max = sorted[^1];
        string report = $"frames={sorted.Count} avgDraw={avg:F2}ms p95={p95:F2}ms max={max:F2}ms " +
            $"nodes={_nodes.Count} drawn={_drawnCircles} lastRebuild={_lastRebuildMs:F1}ms";
        _drawSamples.Clear();
        return report;
    }

    /// <summary>Re-pulls the current level from the (mutated) index, keeping camera + no re-entrance.</summary>
    public void RefreshLevel()
    {
        RebuildLevel(resetCamera: false, animateEntrance: false);
    }

    /// <summary>Fly the camera to frame the whole current level (Home).</summary>
    public void ZoomHome()
    {
        Camera.ZoomToFit(_lastViewport, ContentExtent(), fly: true);
        RequestFrames();
    }

    // --- navigation ---

    public void GoUp()
    {
        if (_currentVolume is null || _pendingLevelSwap is not null)
            return;

        _highlightedFrn = null;
        Camera.FlyTo(Camera.Pan + (_lastViewport / 2f - Camera.Pan) * 0.5f, Camera.Zoom * 0.45f); // recede
        BeginLevelSwap(() =>
        {
            if (_parentTrail.Count == 0)
                _currentVolume = null;
            else
                _parentTrail.Pop();
            RebuildLevel();
        });
    }

    /// <summary>
    /// Tap: drill into volumes/directories (camera flies into the node); select files.
    /// Returns the hit when a file was selected, null otherwise.
    /// </summary>
    public NodeHit? OnTapped(Vector2 screenPoint)
    {
        if (_pendingLevelSwap is not null)
            return null;

        if (TryGetNodeAt(screenPoint) is not { } hit)
        {
            ClearSelection();
            return null;
        }

        _highlightedFrn = null;
        if (hit.IsVolumeNode || hit.File.IsDirectory)
        {
            // Fly INTO the node while the old level fades away.
            Vector2 nodePos = _nodes.First(n => n.File.Id == hit.File.Id && n.Volume == hit.Volume).Position;
            float targetZoom = Camera.Zoom * 5f;
            Camera.FlyTo(_lastViewport / 2f - nodePos * targetZoom, targetZoom);

            BeginLevelSwap(() =>
            {
                if (hit.IsVolumeNode)
                {
                    _currentVolume = hit.Volume;
                    _parentTrail.Clear();
                }
                else
                {
                    _parentTrail.Push(hit.File.Id);
                }
                RebuildLevel();
            });
            return null;
        }

        _selectedFrn = hit.File.Id;
        StartPulse();
        RecomputeRelated();
        RequestFrames();
        return hit;
    }

    /// <summary>Jumps to the level containing <paramref name="frn"/>, highlights it, flies to it.</summary>
    public void NavigateTo(VolumeIndex volume, ulong frn)
    {
        if (!volume.TryGetNode(frn, out FileNode node))
            return;

        BeginLevelSwap(() =>
        {
            _currentVolume = volume;
            _parentTrail.Clear();
            foreach (ulong ancestor in BuildTrailTo(volume, node))
                _parentTrail.Push(ancestor);

            _highlightedFrn = frn;
            RebuildLevel();
            StartPulse();

            // Settle centered on the found node, slightly closer than fit.
            GraphNode target = _nodes.FirstOrDefault(n => n.File.Id == frn);
            if (target.File is not null)
            {
                float zoom = Camera.Zoom * 1.6f;
                Camera.FlyTo(_lastViewport / 2f - target.Position * zoom, zoom);
            }
        });
    }

    public void ClearSelection()
    {
        _selectedFrn = null;
        RecomputeRelated();
        RequestFrames();
    }

    private void BeginLevelSwap(Action swap)
    {
        _pendingLevelSwap = swap;
        _fadeOut.Start();
        RequestFrames();
    }

    private void StartPulse()
    {
        _pulseElapsed = 0f;
        RequestFrames();
    }

    // --- drag-move ---

    public bool IsDragging => _dragSource is not null;

    public bool BeginDrag(Vector2 screenPoint)
    {
        if (TryGetNodeAt(screenPoint) is not { IsVolumeNode: false } hit)
            return false;

        _dragSource = hit;
        _dragGhostScreen = _dragGhostTarget = screenPoint;
        _dropTargetFrn = null;
        RequestFrames();
        return true;
    }

    public void UpdateDrag(Vector2 screenPoint)
    {
        if (_dragSource is not { } source)
            return;

        _dragGhostTarget = screenPoint;
        _dropTargetFrn = TryGetNodeAt(screenPoint) is { IsVolumeNode: false } hit
            && hit.File.IsDirectory
            && hit.File.Id != source.File.Id
                ? hit.File.Id
                : null;
        RequestFrames();
    }

    public (NodeHit Source, NodeHit Target)? EndDrag()
    {
        (NodeHit, NodeHit)? result = null;
        if (_dragSource is { } source && _dropTargetFrn is { } targetFrn
            && _currentVolume is { } volume && volume.TryGetNode(targetFrn, out FileNode target))
        {
            result = (source, new NodeHit(volume, target, IsVolumeNode: false));
        }

        _dragSource = null;
        _dropTargetFrn = null;
        return result;
    }

    public void CancelDrag()
    {
        _dragSource = null;
        _dropTargetFrn = null;
    }

    // --- camera input (MainPage forwards pointer events) ---

    public void Zoom(float wheelDelta, Vector2 screenCenter)
    {
        Camera.ZoomAboutPoint(wheelDelta, screenCenter);
        RequestFrames();
    }

    public void Pan(Vector2 screenDelta) => Camera.PanBy(screenDelta);

    // --- hover & hit-testing ---

    public NodeHit? TryGetNodeAt(Vector2 screenPoint)
    {
        Vector2 world = Camera.ScreenToWorld(screenPoint);
        // Nodes are laid out sparsely; linear nearest-hit is fine at level sizes.
        foreach (GraphNode node in _nodes)
        {
            if (Vector2.DistanceSquared(world, node.Position) <= node.Radius * node.Radius)
                return new NodeHit(node.Volume, node.File, IsVolumeNode: _currentVolume is null);
        }
        return null;
    }

    public IReadOnlyList<VisibleNode> GetVisibleNodes() =>
        _nodes.Select(n => new VisibleNode(
            n.File.Name, n.File.Id, Camera.WorldToScreen(n.Position), n.Radius * Camera.Zoom, n.File.IsDirectory)).ToList();

    /// <summary>Transient hover reveal + swell. Returns true when the visual state changed.</summary>
    public bool SetHover(Vector2 screenPoint)
    {
        ulong? frn = TryGetNodeAt(screenPoint) is { IsVolumeNode: false } hit ? hit.File.Id : null;
        if (frn == _hoveredFrn)
            return false;

        _hoveredFrn = frn;
        if (frn is { } f)
            _swell.TryAdd(f, _swell.GetValueOrDefault(f));
        RecomputeRelated();
        RequestFrames();
        return true;
    }

    /// <summary>Visible-level relatives of the anchor (selected wins over hovered): duplicates or similar names.</summary>
    private void RecomputeRelated()
    {
        _relatedFrns.Clear();
        ulong? anchorFrn = _selectedFrn ?? _hoveredFrn;
        if (anchorFrn is not { } frn)
            return;

        FileNode? anchor = _nodes.FirstOrDefault(n => n.File.Id == frn).File;
        if (anchor is null)
            return;

        foreach (GraphNode node in _nodes)
        {
            if (node.File.Id == frn)
                continue;

            bool duplicate = !node.File.IsDirectory && !anchor.IsDirectory
                && node.File.SizeBytes > 0 && node.File.SizeBytes == anchor.SizeBytes
                && string.Equals(node.File.Name, anchor.Name, StringComparison.OrdinalIgnoreCase);

            if (duplicate || NameStem.AreSimilar(anchor.Name, node.File.Name))
                _relatedFrns.Add(node.File.Id);
        }
    }

    // --- level building ---

    private void RebuildLevel(bool resetCamera = true, bool animateEntrance = true)
    {
        var rebuildTimer = System.Diagnostics.Stopwatch.StartNew();
        _nodes.Clear();
        _hoveredFrn = null;
        _selectedFrn = null;
        _relatedFrns.Clear();
        _swell.Clear();
        if (_machine is null)
            return;

        // Two-phase: collect entries (biggest-first), then circle-pack them — dense,
        // deterministic, overlap-free, with the enclosing circle as the level boundary.
        var entries = new List<(FileNode File, VolumeIndex Volume, float Radius, string Label)>();
        if (_currentVolume is null)
        {
            foreach (VolumeIndex volume in _machine.Volumes)
            {
                long totalBytes = volume.RootEntries.Sum(n => n.SizeBytes);
                entries.Add((
                    new FileNode(0, 0, volume.Volume, totalBytes, IsDirectory: true),
                    volume,
                    DirectoryRadius * 2,
                    totalBytes > 0
                        ? $"{volume.Volume}  ({volume.Count:N0} items, {FormatSize(totalBytes)})"
                        : $"{volume.Volume}  ({volume.Count:N0})"));
            }
        }
        else
        {
            IReadOnlyList<FileNode> children = _parentTrail.Count == 0
                ? _currentVolume.RootEntries
                : _currentVolume.GetChildren(_parentTrail.Peek());

            // Biggest first packs densest; same ordering as previews (seamless re-root).
            foreach (FileNode child in OrderChildren(children))
            {
                entries.Add((child, _currentVolume, SizeScaledRadius(child),
                    child.SizeBytes > 0 ? $"{child.Name}  ({FormatSize(child.SizeBytes)})" : child.Name));
            }
        }

        var circles = new CirclePacker.Circle[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            circles[i] = new CirclePacker.Circle(entries[i].Radius);
        _levelRadius = (float)CirclePacker.Pack(circles, PackPadding);

        for (int i = 0; i < entries.Count; i++)
        {
            var (file, volume, radius, label) = entries[i];
            string? path = i >= MaxLevelPaths ? null
                : _currentVolume is null ? volume.Volume + @"\"
                : volume.GetPath(file.Id);
            _nodes.Add(new GraphNode(file, volume,
                new Vector2((float)circles[i].X, (float)circles[i].Y), radius, label,
                file.IsDirectory ? FileTypeCategory.Other : FileTypeClassifier.Classify(file.Name), path));
        }

        if (resetCamera)
        {
            if (_hasDrawn)
                Camera.ZoomToFit(_lastViewport, ContentExtent(), fly: false);
            else
                _pendingInitialFit = true; // canvas not measured yet (demo builds in ms) — fit on first draw
        }

        if (animateEntrance)
            _entrance.Start();
        else
            _entrance.Finish();

        _lastRebuildMs = rebuildTimer.Elapsed.TotalMilliseconds;
        RequestFrames();
        LevelChanged?.Invoke();
    }

    private static IEnumerable<FileNode> OrderChildren(IEnumerable<FileNode> children) => children
        .OrderByDescending(c => c.SizeBytes)
        .ThenByDescending(c => c.IsDirectory)
        .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Cached deterministic pack of a dir's children (same ordering/radii as a real level).</summary>
    private ChildPack? GetPreview(VolumeIndex volume, ulong parentFrn)
    {
        if (_previewCache.TryGetValue((volume, parentFrn), out ChildPack? cached))
            return cached;

        if (_previewBudget <= 0)
        {
            RequestFrames(); // finish building over the next frames
            return null;
        }
        _previewBudget--;

        IReadOnlyList<FileNode> children = parentFrn == VolumeIndex.SyntheticRootId
            ? volume.RootEntries
            : volume.GetChildren(parentFrn);
        FileNode[] files = OrderChildren(children).Take(MaxPreviewChildren).ToArray();

        var circles = new CirclePacker.Circle[files.Length];
        for (int i = 0; i < files.Length; i++)
            circles[i] = new CirclePacker.Circle(SizeScaledRadius(files[i]));
        float packRadius = (float)CirclePacker.Pack(circles, PackPadding);

        var positions = new Vector2[files.Length];
        var radii = new float[files.Length];
        var categories = new FileTypeCategory[files.Length];
        var paths = new string?[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            positions[i] = new Vector2((float)circles[i].X, (float)circles[i].Y);
            radii[i] = (float)circles[i].R;
            categories[i] = files[i].IsDirectory ? FileTypeCategory.Other : FileTypeClassifier.Classify(files[i].Name);
            paths[i] = volume.GetPath(files[i].Id);
        }

        if (_previewCache.Count >= PreviewCacheCap)
            _previewCache.Clear();
        var pack = new ChildPack(files, positions, radii, categories, paths, packRadius);
        _previewCache[(volume, parentFrn)] = pack;
        return pack;
    }

    // --- continuous zoom navigation (semantic zoom re-rooting) ---

    /// <summary>
    /// Zooming INTO a dir until it fills the viewport re-roots the level to it; zooming
    /// the whole level down to a speck re-roots out to the parent. Camera remaps are
    /// screen-invariant, so the world never visibly jumps — this is what makes the
    /// universe continuously zoomable at any depth.
    /// </summary>
    private void EvaluateAutoNavigation()
    {
        if (_machine is null || _pendingLevelSwap is not null || _dragSource is not null
            || !_hasDrawn || Camera.Settled)
            return;

        float minVp = MathF.Min(_lastViewport.X, _lastViewport.Y);
        float zoom = Camera.Zoom;

        if (Camera.ZoomTarget > zoom)
        {
            Vector2 centerWorld = Camera.ScreenToWorld(_lastViewport / 2f);
            foreach (GraphNode node in _nodes)
            {
                if (!node.File.IsDirectory
                    || node.Radius * zoom < AutoDrillViewportFrac * minVp
                    || Vector2.DistanceSquared(centerWorld, node.Position) > node.Radius * node.Radius)
                    continue;
                AutoDrillInto(node);
                return;
            }
        }
        else if (Camera.ZoomTarget < zoom && _currentVolume is not null
            && _levelRadius * zoom < AutoUpViewportFrac * minVp)
        {
            AutoGoUp();
        }
    }

    private void AutoDrillInto(GraphNode node)
    {
        Vector2 nodePos = node.Position;
        float nodeRadius = node.Radius;

        if (_currentVolume is null)
        {
            _currentVolume = node.Volume;
            _parentTrail.Clear();
        }
        else
        {
            _parentTrail.Push(node.File.Id);
        }

        _highlightedFrn = null;
        RebuildLevel(resetCamera: false, animateEntrance: false);
        if (_levelRadius > 0)
            Camera.RemapDown(nodePos, nodeRadius * PreviewFit / _levelRadius);
        Camera.UpdateMinZoom(_lastViewport, ContentExtent());
    }

    private void AutoGoUp()
    {
        float oldLevelRadius = _levelRadius;
        VolumeIndex nodeVolume = _currentVolume!;
        ulong nodeFrn = 0;

        if (_parentTrail.Count == 0)
            _currentVolume = null;              // back out to the machine level
        else
            nodeFrn = _parentTrail.Pop();

        _highlightedFrn = null;
        RebuildLevel(resetCamera: false, animateEntrance: false);

        GraphNode host = _nodes.FirstOrDefault(n =>
            _currentVolume is null ? n.Volume == nodeVolume : n.File.Id == nodeFrn);
        if (host.File is not null && oldLevelRadius > 0)
            Camera.RemapUp(host.Position, host.Radius * PreviewFit / oldLevelRadius);
        Camera.UpdateMinZoom(_lastViewport, ContentExtent());
    }

    private float ContentExtent()
    {
        if (_nodes.Count == 0)
            return 100f;
        float extent = _levelRadius;
        foreach (GraphNode node in _nodes)
            extent = MathF.Max(extent, node.Position.Length() + node.Radius);
        return extent + 30f; // label breathing room
    }

    /// <summary>Log-scaled radius: 1 KB ≈ base, 1 GB+ visibly dominates, clamped for layout sanity.</summary>
    private static float SizeScaledRadius(FileNode node)
    {
        float baseRadius = node.IsDirectory ? 14f : 6f;
        if (node.SizeBytes <= 0)
            return baseRadius;
        float grown = baseRadius + 1.6f * MathF.Log2(1 + node.SizeBytes / 1024f);
        return MathF.Min(grown, node.IsDirectory ? 52f : 40f);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):F1} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };

    // --- drawing ---

    public void Draw(CanvasControl canvas, CanvasDrawingSession session)
    {
        _drawTimer.Restart();
        _drawnCircles = 0;
        _lastViewport = new Vector2((float)canvas.ActualWidth, (float)canvas.ActualHeight);
        if (!_hasDrawn)
            _hasDrawn = true;
        if (_pendingInitialFit)
        {
            Camera.ZoomToFit(_lastViewport, ContentExtent(), fly: false);
            _pendingInitialFit = false;
        }

        session.Transform = Camera.Transform;
        float zoom = Camera.Zoom;
        long nowFileTime = DateTime.UtcNow.ToFileTimeUtc();
        _labelRects.Clear();
        _badgeQueue.Clear();
        _previewBudget = 2; // at most 2 fresh child packs per frame — no hitches
        Images.DrainReady(canvas.Device, 6); // finished shell images → device bitmaps, budgeted

        // Level fade: fading out toward a pending swap, otherwise fully opaque (entrance scales instead).
        float levelAlpha = _pendingLevelSwap is not null ? 1f - _fadeOut.Value : 1f;

        // Level boundary: the parent circle the level is packed inside (replaces the
        // old hierarchy spokes — in a packed layout the container IS the hierarchy).
        if (_levelRadius > 0)
            session.DrawCircle(Vector2.Zero, _levelRadius, WithAlpha(EdgeColor, levelAlpha), 2f / zoom);

        // Relation edges: anchor → each visible relative (fade with reveal alpha).
        ulong? anchorFrn = _selectedFrn ?? _hoveredFrn;
        Vector2? anchorPos = null;
        if (anchorFrn is { } af && _nodes.FirstOrDefault(n => n.File.Id == af) is { File: not null } anchorNode)
            anchorPos = anchorNode.Position;
        if (anchorPos is { } anchor && _relatedFrns.Count > 0 && _revealAlpha > 0.01f)
        {
            foreach (GraphNode node in _nodes)
            {
                if (_relatedFrns.Contains(node.File.Id))
                    session.DrawLine(anchor, node.Position, WithAlpha(RelationColor, _revealAlpha * levelAlpha), 2.5f / zoom);
            }
        }

        float pulse = _pulseElapsed < PulseSeconds
            ? 1f + 0.25f * MathF.Sin(_pulseElapsed * 10f) * MathF.Exp(-_pulseElapsed * 2.5f)
            : 1f;

        // Circles go through a sprite batch (rendered when the batch is disposed);
        // rings and labels are queued and drawn after so they sit on top.
        if (_circleSprite is null || _circleSprite.Device != canvas.Device)
        {
            _circleSprite = CreateShapeSprite(canvas, squircle: false);
            _squircleSprite = CreateShapeSprite(canvas, squircle: true);
        }
        _ringQueue.Clear();
        _labelQueue.Clear();

        CanvasSpriteBatch? sprites = CanvasSpriteBatch.IsSupported(canvas.Device)
            ? session.CreateSpriteBatch(CanvasSpriteSortMode.None)
            : null;
        try
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                GraphNode node = _nodes[i];

                if (node.Radius * zoom < MinDrawScreenR || _drawnCircles >= MaxCirclesPerFrame)
                    break; // radii descend — what's left is sub-pixel or over budget

                // Viewport cull (margin covers swell + rings).
                Vector2 screenPos = Camera.WorldToScreen(node.Position);
                float screenR = node.Radius * zoom + 48f;
                if (screenPos.X < -screenR || screenPos.Y < -screenR
                    || screenPos.X > _lastViewport.X + screenR || screenPos.Y > _lastViewport.Y + screenR)
                    continue;

                float swell = _swell.Count > 0 ? _swell.GetValueOrDefault(node.File.Id) : 0f;
                float radius = node.Radius * EntranceScale(i) * (1f + HoverSwell * swell);
                if (radius <= 0.01f)
                    continue;

                // Semantic zoom: a big-enough container hollows out (fill fades, ring
                // appears) and its children render packed inside.
                Color nodeColor = ResolveColor(node.File, node.Category, nowFileTime);
                float reveal = node.File.IsDirectory
                    ? Math.Clamp((radius * zoom - RevealStartScreenR) / (RevealFullScreenR - RevealStartScreenR), 0f, 1f)
                    : 0f;
                bool circleShape = node.File.IsDirectory || node.Category is FileTypeCategory.Image or FileTypeCategory.Video;
                DrawNodeShape(sprites, session, node.Position, radius, WithAlpha(nodeColor, levelAlpha * (1f - 0.72f * reveal)), zoom, circleShape);
                if (reveal > 0f)
                {
                    _ringQueue.Add((node.Position, radius, WithAlpha(nodeColor, levelAlpha * (0.35f + 0.4f * reveal)), 2f / zoom));
                    DrawPreview(sprites, session, node.Volume,
                        _currentVolume is null ? VolumeIndex.SyntheticRootId : node.File.Id,
                        node.Position, radius, levelAlpha * reveal, zoom, nowFileTime, depth: 0);
                }
                else if (node.File.IsDirectory)
                {
                    QueueDirBadge(node.Volume, node.File, node.Position, radius * zoom, _currentVolume is null);
                }

                if (!node.File.IsDirectory)
                    DrawIdentity(sprites, node.Position, radius, zoom, node.Path, node.Category);

                if (_highlightedFrn == node.File.Id && _currentVolume is not null)
                    _ringQueue.Add((node.Position, (radius + 6) * pulse, WithAlpha(HighlightColor, levelAlpha), 3f / zoom));

                if (node.File.Id == anchorFrn)
                    _ringQueue.Add((node.Position, (radius + 4) * (node.File.Id == _selectedFrn ? pulse : 1f),
                        WithAlpha(RelationColor, MathF.Max(_revealAlpha, 0.4f) * levelAlpha),
                        (_selectedFrn is not null ? 3f : 1.5f) / zoom));
                else if (_relatedFrns.Count > 0 && _relatedFrns.Contains(node.File.Id))
                    _ringQueue.Add((node.Position, radius + 4, WithAlpha(RelationColor, _revealAlpha * levelAlpha), 2f / zoom));

                if (_dropTargetFrn == node.File.Id)
                    _ringQueue.Add((node.Position, radius + 8, DropTargetColor, 4f / zoom));

                if (radius * zoom >= LabelMinScreenRadius)
                    _labelQueue.Add((i, radius, node.File.Id == anchorFrn || node.File.Id == _highlightedFrn));
            }
        }
        finally
        {
            sprites?.Dispose(); // sprites render here, above the edges, below rings/labels
        }

        foreach (var (pos, r, color, stroke) in _ringQueue)
            session.DrawCircle(pos, r, color, stroke);

        // Greedy label declutter: queue is biggest-first, so big nodes claim label
        // space and overlapping labels are skipped. Anchor/highlight always draws.
        foreach (var (index, radius, force) in _labelQueue)
            TryDrawLabel(session, index, radius, levelAlpha, force, zoom);

        // Child-count badges render at fixed screen size (world text would balloon).
        if (_badgeQueue.Count > 0)
        {
            session.Transform = Matrix3x2.Identity;
            foreach (var (worldPos, text) in _badgeQueue)
                session.DrawText(text, Camera.WorldToScreen(worldPos), BadgeColor, BadgeFormat);
            session.Transform = Camera.Transform;
        }

#if DEBUG
        // Diagnostic overlay (debug builds only): live camera/clock numbers for the harness.
        session.Transform = Matrix3x2.Identity;
        session.DrawText(
            $"vp={_lastViewport.X:F0}x{_lastViewport.Y:F0} zoom={Camera.Zoom:F3} pan={Camera.Pan.X:F0},{Camera.Pan.Y:F0} min={Camera.MinZoom:F3} nodes={_nodes.Count} pv={_previewCache.Count} frame={_frameCounter++}",
            new Vector2(8, 8), Color.FromArgb(255, 255, 255, 0),
            new CanvasTextFormat { FontSize = 12, HorizontalAlignment = CanvasHorizontalAlignment.Left });
        session.Transform = Camera.Transform;
#endif

        // Drag ghost rides in screen space above everything, easing after the cursor.
        if (_dragSource is { } drag)
        {
            session.Transform = Matrix3x2.Identity;
            Color ghost = Color.FromArgb(140, DirectoryColor.R, DirectoryColor.G, DirectoryColor.B);
            session.FillCircle(_dragGhostScreen, 14, ghost);
            session.DrawText(drag.File.Name, _dragGhostScreen + new Vector2(0, 18), LabelColor, LabelFormat);
        }

        DrawMinimap(session);

        _drawTimer.Stop();
        if (_drawSamples.Count < 4096)
            _drawSamples.Add(_drawTimer.Elapsed.TotalMilliseconds);
    }

    // --- minimap ---

    private (Vector2 Center, float Scale) MinimapTransform()
    {
        Vector2 center = new(
            _lastViewport.X - MinimapMargin - MinimapSize / 2f,
            _lastViewport.Y - MinimapMargin - MinimapSize / 2f);
        float scale = (MinimapSize / 2f - 10f) / MathF.Max(ContentExtent(), 1f);
        return (center, scale);
    }

    private void DrawMinimap(CanvasDrawingSession session)
    {
        if (_minimapAlpha <= 0.02f || _nodes.Count == 0)
            return;

        session.Transform = Matrix3x2.Identity;
        var (center, scale) = MinimapTransform();
        float half = MinimapSize / 2f;

        session.FillRoundedRectangle(center.X - half, center.Y - half, MinimapSize, MinimapSize, 10, 10,
            WithAlpha(Color.FromArgb(230, 16, 21, 29), _minimapAlpha));
        session.DrawRoundedRectangle(center.X - half, center.Y - half, MinimapSize, MinimapSize, 10, 10,
            WithAlpha(Color.FromArgb(90, 122, 168, 210), _minimapAlpha), 1f);
        session.DrawCircle(center, _levelRadius * scale, WithAlpha(EdgeColor, _minimapAlpha), 1f);

        int dots = Math.Min(_nodes.Count, MinimapMaxDots);
        for (int i = 0; i < dots; i++)
        {
            GraphNode node = _nodes[i];
            session.FillCircle(center + node.Position * scale, MathF.Max(node.Radius * scale, 1f),
                WithAlpha(ResolveColor(node.File, node.Category, 0), _minimapAlpha * 0.85f));
        }

        // Viewport rectangle in level space (clamped to the panel).
        Vector2 topLeft = center + Camera.ScreenToWorld(Vector2.Zero) * scale;
        Vector2 bottomRight = center + Camera.ScreenToWorld(_lastViewport) * scale;
        float l = Math.Clamp(topLeft.X, center.X - half, center.X + half);
        float t = Math.Clamp(topLeft.Y, center.Y - half, center.Y + half);
        float r = Math.Clamp(bottomRight.X, center.X - half, center.X + half);
        float b = Math.Clamp(bottomRight.Y, center.Y - half, center.Y + half);
        session.DrawRectangle(l, t, r - l, b - t,
            WithAlpha(Color.FromArgb(200, 235, 240, 248), _minimapAlpha), 1.5f);

        session.Transform = Camera.Transform;
    }

    /// <summary>True (and camera flies) when the point lands on the visible minimap.</summary>
    public bool TryMinimapJump(Vector2 canvasPoint)
    {
        if (!IsInMinimap(canvasPoint))
            return false;

        var (center, scale) = MinimapTransform();
        Vector2 world = (canvasPoint - center) / scale;
        Camera.FlyTo(_lastViewport / 2f - world * Camera.Zoom, Camera.Zoom);
        RequestFrames();
        return true;
    }

    public bool IsInMinimap(Vector2 canvasPoint)
    {
        if (_minimapAlpha < 0.5f)
            return false;
        var (center, _) = MinimapTransform();
        float half = MinimapSize / 2f;
        return canvasPoint.X >= center.X - half && canvasPoint.X <= center.X + half
            && canvasPoint.Y >= center.Y - half && canvasPoint.Y <= center.Y + half;
    }

    /// <summary>
    /// One tinted node shape: sprite instance when small, crisp vector when big.
    /// Shape-by-kind: containers and media (thumbnail-ready) are circles, other files
    /// are rounded squares — leaves read differently from containers at a glance.
    /// </summary>
    private void DrawNodeShape(CanvasSpriteBatch? sprites, CanvasDrawingSession session,
        Vector2 pos, float radius, Color color, float zoom, bool circle)
    {
        _drawnCircles++;
        CanvasRenderTarget? sprite = circle ? _circleSprite : _squircleSprite;
        if (sprites is null || sprite is null || radius * zoom >= VectorCircleScreenR)
        {
            if (circle)
                session.FillCircle(pos, radius, color);
            else
                session.FillRoundedRectangle(pos.X - 0.85f * radius, pos.Y - 0.85f * radius,
                    1.7f * radius, 1.7f * radius, 0.5f * radius, 0.5f * radius, color);
            return;
        }

        // Dest rect sized so the texture's shape (not its padding) lands at `radius`.
        float extent = radius * (SpriteSourceSize / 2f) / SpriteSourceRadius;
        sprites.Draw(sprite,
            new Windows.Foundation.Rect(pos.X - extent, pos.Y - extent, extent * 2, extent * 2),
            new Vector4(color.R, color.G, color.B, color.A) / 255f);
    }

    private static CanvasRenderTarget CreateShapeSprite(CanvasControl canvas, bool squircle)
    {
        var target = new CanvasRenderTarget(canvas.Device, SpriteSourceSize, SpriteSourceSize, 96);
        using CanvasDrawingSession session = target.CreateDrawingSession();
        session.Clear(Color.FromArgb(0, 0, 0, 0));
        Color white = Color.FromArgb(255, 255, 255, 255);
        float c = SpriteSourceSize / 2f;
        if (squircle)
        {
            float half = SpriteSourceRadius * 0.85f;
            session.FillRoundedRectangle(c - half, c - half, half * 2, half * 2,
                half * 0.58f, half * 0.58f, white);
        }
        else
        {
            session.FillCircle(c, c, SpriteSourceRadius, white);
        }
        return target;
    }

    /// <summary>Shell icon / circle-cropped thumbnail on top of the base shape (files only).</summary>
    private void DrawIdentity(CanvasSpriteBatch? sprites, Vector2 pos, float radius, float zoom,
        string? path, FileTypeCategory category)
    {
        if (sprites is null || path is null || radius * zoom < IconMinScreenR)
            return;

        if (category is FileTypeCategory.Image or FileTypeCategory.Video)
        {
            string thumbKey = "T|" + path;
            CanvasBitmap? thumb = Images.TryGet(thumbKey);
            if (thumb is not null)
            {
                float scale = radius * 2f / Math.Min(thumb.SizeInPixels.Width, thumb.SizeInPixels.Height);
                float w = thumb.SizeInPixels.Width * scale, h = thumb.SizeInPixels.Height * scale;
                sprites.Draw(thumb, new Windows.Foundation.Rect(pos.X - w / 2, pos.Y - h / 2, w, h), Vector4.One);
                return;
            }
            if (!Images.IsResolved(thumbKey))
            {
                Images.Request(thumbKey, path, thumbnail: true);
                return;
            }
            // thumbnail failed → fall through to the generic icon
        }

        string ext = System.IO.Path.GetExtension(path);
        string key = ext is ".exe" or ".lnk" or ".ico" || ext.Length == 0
            ? "P|" + path                       // own-icon files
            : "E|" + ext.ToLowerInvariant();    // one icon per extension
        CanvasBitmap? icon = Images.TryGet(key);
        if (icon is null)
        {
            Images.Request(key, path, thumbnail: false);
            return;
        }
        float side = radius * 1.4f;
        sprites.Draw(icon, new Windows.Foundation.Rect(pos.X - side / 2, pos.Y - side / 2, side, side), Vector4.One);
    }

    private void QueueDirBadge(VolumeIndex volume, FileNode file, Vector2 pos, float screenR, bool isVolumeLevel)
    {
        if (screenR < BadgeMinScreenR || screenR >= RevealStartScreenR)
            return;
        int count = isVolumeLevel ? volume.RootEntries.Count : volume.GetChildren(file.Id).Count;
        if (count > 0)
            _badgeQueue.Add((pos, count.ToString("N0")));
    }

    /// <summary>
    /// Draws a dir's children packed inside its circle, recursing while they stay big
    /// enough on screen. Hard-culled: sub-1.4px children stop the loop (radii descend),
    /// offscreen children are skipped.
    /// </summary>
    private void DrawPreview(CanvasSpriteBatch? sprites, CanvasDrawingSession session, VolumeIndex volume, ulong parentFrn,
        Vector2 center, float drawnRadius, float alpha, float zoom, long nowFileTime, int depth)
    {
        if (depth >= MaxPreviewDepth || alpha <= 0.03f)
            return;
        ChildPack? pack = GetPreview(volume, parentFrn);
        if (pack is null || pack.Files.Length == 0 || pack.PackRadius <= 0)
            return;

        float scale = drawnRadius * PreviewFit / pack.PackRadius;
        for (int i = 0; i < pack.Files.Length; i++)
        {
            float r = pack.Radii[i] * scale;
            float screenR = r * zoom;
            if (screenR < 1.4f)
                break; // radii descend — everything after is smaller

            Vector2 pos = center + pack.Positions[i] * scale;
            Vector2 screen = Camera.WorldToScreen(pos);
            if (screen.X < -screenR || screen.Y < -screenR
                || screen.X > _lastViewport.X + screenR || screen.Y > _lastViewport.Y + screenR)
                continue;

            FileNode file = pack.Files[i];
            FileTypeCategory category = pack.Categories[i];
            Color color = ResolveColor(file, category, nowFileTime);
            float childReveal = file.IsDirectory
                ? Math.Clamp((screenR - RevealStartScreenR) / (RevealFullScreenR - RevealStartScreenR), 0f, 1f)
                : 0f;
            bool circleShape = file.IsDirectory || category is FileTypeCategory.Image or FileTypeCategory.Video;
            DrawNodeShape(sprites, session, pos, r, WithAlpha(color, alpha * (1f - 0.72f * childReveal)), zoom, circleShape);
            if (childReveal > 0f)
            {
                _ringQueue.Add((pos, r, WithAlpha(color, alpha * (0.35f + 0.4f * childReveal)), 2f / zoom));
                DrawPreview(sprites, session, volume, file.Id, pos, r, alpha * childReveal, zoom, nowFileTime, depth + 1);
            }
            else if (file.IsDirectory)
            {
                QueueDirBadge(volume, file, pos, screenR, isVolumeLevel: false);
            }

            if (!file.IsDirectory)
                DrawIdentity(sprites, pos, r, zoom, pack.Paths[i], category);
        }
    }

    private void TryDrawLabel(CanvasDrawingSession session, int index, float radius, float levelAlpha, bool force, float zoom)
    {
        GraphNode node = _nodes[index];
        float width = node.Label.Length * LabelFormat.FontSize * 0.52f; // estimate — cheap and close enough for collision
        float height = LabelFormat.FontSize + 5;

        // Preferred spot is under the node; above is the fallback when a neighbor sits below.
        Span<float> tops = [node.Position.Y + radius + 4, node.Position.Y - radius - 4 - height];
        foreach (float top in tops)
        {
            float left = node.Position.X - width / 2;
            var rect = new Vector4(left, top, left + width, top + height);
            if (force || LabelSpotFree(rect, index, zoom))
            {
                _labelRects.Add(rect);
                session.DrawText(node.Label, new Vector2(node.Position.X, top), WithAlpha(LabelColor, levelAlpha), LabelFormat);
                return;
            }
        }
    }

    private bool LabelSpotFree(Vector4 rect, int index, float zoom)
    {
        foreach (Vector4 other in _labelRects)
        {
            if (rect.X < other.Z && rect.Z > other.X && rect.Y < other.W && rect.W > other.Y)
                return false;
        }

        // Don't run text across neighboring circles (slight edge clipping is fine —
        // circles curve away). Dense levels keep edge labels; hover reveals the rest.
        for (int j = 0; j < _nodes.Count; j++)
        {
            if (_nodes[j].Radius * zoom < 3f)
                break; // radii descend — text over sub-3px specks reads fine
            if (j == index)
                continue;
            Vector2 p = _nodes[j].Position;
            float cx = Math.Clamp(p.X, rect.X, rect.Z);
            float cy = Math.Clamp(p.Y, rect.Y, rect.W);
            float r = _nodes[j].Radius * 0.85f;
            if ((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) < r * r)
                return false;
        }
        return true;
    }

    /// <summary>Staggered scale-in: early spiral indices land first, tail follows.</summary>
    private float EntranceScale(int index)
    {
        if (!_entrance.Running)
            return 1f;
        float offset = _nodes.Count <= 1 ? 0f : EntranceStagger * index / _nodes.Count;
        return Math.Clamp((_entrance.Value - offset) / (1f - EntranceStagger), 0f, 1f);
    }

    private static Color WithAlpha(Color color, float alpha) =>
        alpha >= 0.999f ? color : Color.FromArgb((byte)(color.A * Math.Clamp(alpha, 0f, 1f)), color.R, color.G, color.B);

    private Color ResolveColor(FileNode file, long nowFileTime) =>
        ResolveColor(file, file.IsDirectory ? FileTypeCategory.Other : FileTypeClassifier.Classify(file.Name), nowFileTime);

    private Color ResolveColor(FileNode file, FileTypeCategory category, long nowFileTime)
    {
        if (file.IsDirectory)
            return DirectoryColor;

        switch (ColorMode)
        {
            case GraphColorMode.Type:
                foreach (var (cat, _, color) in TypePalette)
                {
                    if (cat == category)
                        return color;
                }
                return FileColor;

            case GraphColorMode.Age when file.LastWriteFileTime > 0:
                double ageDays = (nowFileTime - file.LastWriteFileTime) / (double)TimeSpan.TicksPerDay;
                foreach (var (maxDays, _, color) in AgePalette)
                {
                    if (maxDays >= 0 && ageDays <= maxDays)
                        return color;
                }
                return AgePalette[^2].Color; // Older

            case GraphColorMode.Age:
                return AgePalette[^1].Color; // Unknown

            default:
                return FileColor;
        }
    }
}
