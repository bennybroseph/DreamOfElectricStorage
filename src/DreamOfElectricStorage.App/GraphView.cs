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
    private const float SpiralSpacing = 34f;               // phyllotaxis: r = spacing·√i
    private const float GoldenAngle = 2.39996323f;
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

    private readonly record struct GraphNode(FileNode File, VolumeIndex Volume, Vector2 Position, float Radius, string Label);

    /// <summary>A node the pointer landed on. IsVolumeNode = machine-level drive circle (no real FRN).</summary>
    public readonly record struct NodeHit(VolumeIndex Volume, FileNode File, bool IsVolumeNode);

    private MachineIndex? _machine;
    private readonly List<GraphNode> _nodes = [];
    private ulong? _highlightedFrn;

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
            && _swell.Count == 0 && RevealSettled && _pulseElapsed >= PulseSeconds && _dragSource is null;
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

        RedrawNeeded?.Invoke();
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
        RebuildLevel();
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
            // Rebuild the trail root→parent by walking ancestors (same guards as GetPath).
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

            _currentVolume = volume;
            _parentTrail.Clear();
            foreach (ulong ancestor in ancestors)
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
        _nodes.Clear();
        _hoveredFrn = null;
        _selectedFrn = null;
        _relatedFrns.Clear();
        _swell.Clear();
        if (_machine is null)
            return;

        // Two-phase: collect entries first, then place on a spiral spaced by the largest
        // radius so nodes can't overlap. (Interim rule — V2's circle-packing replaces it.)
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

            // Biggest first so heavy items sit near the spiral center; dirs before files at equal size.
            foreach (FileNode child in children
                .OrderByDescending(c => c.SizeBytes)
                .ThenByDescending(c => c.IsDirectory)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add((child, _currentVolume, SizeScaledRadius(child),
                    child.SizeBytes > 0 ? $"{child.Name}  ({FormatSize(child.SizeBytes)})" : child.Name));
            }
        }

        float spacing = SpiralSpacing;
        foreach (var entry in entries)
            spacing = MathF.Max(spacing, entry.Radius * 2.2f);
        for (int i = 0; i < entries.Count; i++)
        {
            var (file, volume, radius, label) = entries[i];
            _nodes.Add(new GraphNode(file, volume, Spiral(i, spacing), radius, label));
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

        RequestFrames();
        LevelChanged?.Invoke();
    }

    private float ContentExtent()
    {
        if (_nodes.Count == 0)
            return 100f;
        float extent = 0f;
        foreach (GraphNode node in _nodes)
            extent = MathF.Max(extent, node.Position.Length() + node.Radius);
        return extent + 30f; // label breathing room
    }

    private static Vector2 Spiral(int i, float spacing) =>
        spacing * MathF.Sqrt(i) * new Vector2(MathF.Cos(i * GoldenAngle), MathF.Sin(i * GoldenAngle));

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

        // Level fade: fading out toward a pending swap, otherwise fully opaque (entrance scales instead).
        float levelAlpha = _pendingLevelSwap is not null ? 1f - _fadeOut.Value : 1f;

        // Hierarchy spokes.
        foreach (GraphNode node in _nodes)
            session.DrawLine(Vector2.Zero, node.Position, WithAlpha(EdgeColor, levelAlpha), 1.5f / zoom);

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

        for (int i = 0; i < _nodes.Count; i++)
        {
            GraphNode node = _nodes[i];

            float radius = node.Radius * EntranceScale(i) * (1f + HoverSwell * _swell.GetValueOrDefault(node.File.Id));
            if (radius <= 0.01f)
                continue;

            session.FillCircle(node.Position, radius, WithAlpha(ResolveColor(node.File, nowFileTime), levelAlpha));

            if (_highlightedFrn == node.File.Id && _currentVolume is not null)
                session.DrawCircle(node.Position, (radius + 6) * pulse, WithAlpha(HighlightColor, levelAlpha), 3f / zoom);

            if (node.File.Id == anchorFrn)
                session.DrawCircle(node.Position, (radius + 4) * (node.File.Id == _selectedFrn ? pulse : 1f),
                    WithAlpha(RelationColor, MathF.Max(_revealAlpha, 0.4f) * levelAlpha),
                    (_selectedFrn is not null ? 3f : 1.5f) / zoom);
            else if (_relatedFrns.Contains(node.File.Id))
                session.DrawCircle(node.Position, radius + 4, WithAlpha(RelationColor, _revealAlpha * levelAlpha), 2f / zoom);

            if (_dropTargetFrn == node.File.Id)
                session.DrawCircle(node.Position, radius + 8, DropTargetColor, 4f / zoom);

            if (radius * zoom >= LabelMinScreenRadius)
            {
                session.DrawText(
                    node.Label,
                    node.Position + new Vector2(0, radius + 4),
                    WithAlpha(LabelColor, levelAlpha),
                    LabelFormat);
            }
        }

#if DEBUG
        // Diagnostic overlay (debug builds only): live camera/clock numbers for the harness.
        session.Transform = Matrix3x2.Identity;
        session.DrawText(
            $"vp={_lastViewport.X:F0}x{_lastViewport.Y:F0} zoom={Camera.Zoom:F3} pan={Camera.Pan.X:F0},{Camera.Pan.Y:F0} min={Camera.MinZoom:F3} nodes={_nodes.Count} n0r={(_nodes.Count > 0 ? _nodes[0].Radius : 0):F0} frame={_frameCounter++}",
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

    private Color ResolveColor(FileNode file, long nowFileTime)
    {
        if (file.IsDirectory)
            return DirectoryColor;

        switch (ColorMode)
        {
            case GraphColorMode.Type:
                FileTypeCategory category = FileTypeClassifier.Classify(file.Name);
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
