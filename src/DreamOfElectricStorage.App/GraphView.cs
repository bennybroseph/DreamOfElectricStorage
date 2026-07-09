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
/// Canvas-side state and logic for the node graph: current level, layout, camera,
/// drawing, and hit-testing. MainPage owns the CanvasControl and forwards events here.
/// </summary>
public sealed class GraphView
{
    private const float DirectoryRadius = 26f;
    private const float FileRadius = 12f;
    private const float SpiralSpacing = 34f;               // phyllotaxis: r = spacing·√i
    private const float GoldenAngle = 2.39996323f;
    private const float LabelMinScreenRadius = 9f;         // hide labels when nodes get tiny
    private const float MinZoom = 0.02f, MaxZoom = 12f;

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
    private ulong? _dropTargetFrn;

    public bool IsDragging => _dragSource is not null;

    public GraphColorMode ColorMode { get; set; } = GraphColorMode.Type;

    public ulong? SelectedFrn => _selectedFrn;

    // Navigation state: null volume = machine level (volumes as nodes).
    private VolumeIndex? _currentVolume;
    private readonly Stack<ulong> _parentTrail = new(); // FRNs from volume root down to current level

    // Camera: world → screen.
    private float _zoom = 1f;
    private Vector2 _pan; // screen-space offset

    public event Action? LevelChanged;

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

    public void SetIndex(MachineIndex machine)
    {
        _machine = machine;
        _currentVolume = null;
        _parentTrail.Clear();
        RebuildLevel();
    }

    // --- navigation ---

    /// <summary>FRN whose children are currently displayed, or null at machine/root level.</summary>
    public ulong? CurrentParentId => _currentVolume is null ? null
        : _parentTrail.Count == 0 ? VolumeIndex.SyntheticRootId : _parentTrail.Peek();

    public VolumeIndex? CurrentVolume => _currentVolume;

    /// <summary>Re-pulls the current level from the (mutated) index, keeping the camera still.</summary>
    public void RefreshLevel() => RebuildLevel(resetCamera: false);

    public void GoUp()
    {
        if (_currentVolume is null)
            return;

        _highlightedFrn = null;

        if (_parentTrail.Count == 0)
            _currentVolume = null; // back to machine level
        else
            _parentTrail.Pop();
        RebuildLevel();
    }

    /// <summary>Hit-tests a screen point against the current level's nodes.</summary>
    public NodeHit? TryGetNodeAt(Vector2 screenPoint)
    {
        Vector2 world = (screenPoint - _pan) / _zoom;
        // Nodes are laid out sparsely; linear nearest-hit is fine at level sizes.
        foreach (GraphNode node in _nodes)
        {
            if (Vector2.DistanceSquared(world, node.Position) <= node.Radius * node.Radius)
                return new NodeHit(node.Volume, node.File, IsVolumeNode: _currentVolume is null);
        }
        return null;
    }

    /// <summary>
    /// Tap: drill into volumes/directories; select files (relationship reveal).
    /// Returns the hit when a file was selected, null otherwise.
    /// </summary>
    public NodeHit? OnTapped(Vector2 screenPoint)
    {
        if (TryGetNodeAt(screenPoint) is not { } hit)
        {
            ClearSelection();
            return null;
        }

        _highlightedFrn = null;
        if (hit.IsVolumeNode)
        {
            _currentVolume = hit.Volume;
            _parentTrail.Clear();
        }
        else if (hit.File.IsDirectory)
        {
            _parentTrail.Push(hit.File.Id);
        }
        else
        {
            _selectedFrn = hit.File.Id;
            RecomputeRelated();
            return hit;
        }

        RebuildLevel();
        return null;
    }

    public void ClearSelection()
    {
        _selectedFrn = null;
        RecomputeRelated();
    }

    /// <summary>Starts a node drag if the point hits a draggable node (not a volume). Returns success.</summary>
    public bool BeginDrag(Vector2 screenPoint)
    {
        if (TryGetNodeAt(screenPoint) is not { IsVolumeNode: false } hit)
            return false;

        _dragSource = hit;
        _dragGhostScreen = screenPoint;
        _dropTargetFrn = null;
        return true;
    }

    /// <summary>Updates the ghost position and the directory drop target under the cursor.</summary>
    public void UpdateDrag(Vector2 screenPoint)
    {
        if (_dragSource is not { } source)
            return;

        _dragGhostScreen = screenPoint;
        _dropTargetFrn = TryGetNodeAt(screenPoint) is { IsVolumeNode: false } hit
            && hit.File.IsDirectory
            && hit.File.Id != source.File.Id
                ? hit.File.Id
                : null;
    }

    /// <summary>Ends the drag; returns (source, target) when released over a valid directory.</summary>
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

    /// <summary>Transient hover reveal. Returns true when the visual state changed.</summary>
    public bool SetHover(Vector2 screenPoint)
    {
        ulong? frn = TryGetNodeAt(screenPoint) is { IsVolumeNode: false } hit ? hit.File.Id : null;
        if (frn == _hoveredFrn)
            return false;

        _hoveredFrn = frn;
        RecomputeRelated();
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

    /// <summary>
    /// Jumps to the level containing <paramref name="frn"/> (its parent's children),
    /// highlights it, and centers the camera on it.
    /// </summary>
    public void NavigateTo(VolumeIndex volume, ulong frn)
    {
        if (!volume.TryGetNode(frn, out FileNode node))
            return;

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

        // Center the camera on the found node (RebuildLevel already picked a sane zoom).
        GraphNode target = _nodes.FirstOrDefault(n => n.File.Id == frn);
        if (target.File is not null)
            _pan = _lastViewport / 2f - target.Position * _zoom;
    }

    // --- camera ---

    public void Zoom(float wheelDelta, Vector2 screenCenter)
    {
        float factor = MathF.Pow(1.001f, wheelDelta);
        float newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        // Keep the world point under the cursor fixed while zooming.
        _pan = screenCenter - (screenCenter - _pan) * (newZoom / _zoom);
        _zoom = newZoom;
    }

    public void Pan(Vector2 screenDelta) => _pan += screenDelta;

    private void ResetCamera(Vector2 viewport)
    {
        if (_nodes.Count == 0)
        {
            _zoom = 1f;
            _pan = viewport / 2f;
            return;
        }

        float extent = SpiralSpacing * MathF.Sqrt(_nodes.Count) + DirectoryRadius * 2;
        _zoom = Math.Clamp(MathF.Min(viewport.X, viewport.Y) / (extent * 2.2f), MinZoom, MaxZoom);
        _pan = viewport / 2f;
    }

    private Vector2 _lastViewport = new(1200, 800);

    // --- level building ---

    private void RebuildLevel(bool resetCamera = true)
    {
        _nodes.Clear();
        _hoveredFrn = null;
        _selectedFrn = null;
        _relatedFrns.Clear();
        if (_machine is null)
            return;

        if (_currentVolume is null)
        {
            // Machine level: one node per volume.
            int i = 0;
            foreach (VolumeIndex volume in _machine.Volumes)
            {
                long totalBytes = volume.RootEntries.Sum(n => n.SizeBytes);
                _nodes.Add(new GraphNode(
                    File: new FileNode(0, 0, volume.Volume, totalBytes, IsDirectory: true),
                    Volume: volume,
                    Position: Spiral(i++),
                    Radius: DirectoryRadius * 2,
                    Label: totalBytes > 0
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
            int i = 0;
            foreach (FileNode child in children
                .OrderByDescending(c => c.SizeBytes)
                .ThenByDescending(c => c.IsDirectory)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                _nodes.Add(new GraphNode(
                    File: child,
                    Volume: _currentVolume,
                    Position: Spiral(i++),
                    Radius: SizeScaledRadius(child),
                    Label: child.SizeBytes > 0 ? $"{child.Name}  ({FormatSize(child.SizeBytes)})" : child.Name));
            }
        }

        if (resetCamera)
            ResetCamera(_lastViewport);
        LevelChanged?.Invoke();
    }

    private static Vector2 Spiral(int i) =>
        SpiralSpacing * MathF.Sqrt(i) * new Vector2(MathF.Cos(i * GoldenAngle), MathF.Sin(i * GoldenAngle));

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
        session.Transform = Matrix3x2.CreateScale(_zoom) * Matrix3x2.CreateTranslation(_pan);
        long nowFileTime = DateTime.UtcNow.ToFileTimeUtc();

        // Edges: every node back to the level's center (hierarchy spokes).
        foreach (GraphNode node in _nodes)
            session.DrawLine(Vector2.Zero, node.Position, EdgeColor, 1.5f / _zoom);

        // Relation edges: anchor → each visible relative, above spokes, below nodes.
        ulong? anchorFrn = _selectedFrn ?? _hoveredFrn;
        Vector2? anchorPos = null;
        if (anchorFrn is { } af && _nodes.FirstOrDefault(n => n.File.Id == af) is { File: not null } anchorNode)
            anchorPos = anchorNode.Position;
        if (anchorPos is { } anchor && _relatedFrns.Count > 0)
        {
            foreach (GraphNode node in _nodes)
            {
                if (_relatedFrns.Contains(node.File.Id))
                    session.DrawLine(anchor, node.Position, RelationColor, 2.5f / _zoom);
            }
        }

        foreach (GraphNode node in _nodes)
        {
            session.FillCircle(node.Position, node.Radius, ResolveColor(node.File, nowFileTime));

            if (_highlightedFrn == node.File.Id && _currentVolume is not null)
                session.DrawCircle(node.Position, node.Radius + 6, HighlightColor, 3f / _zoom);

            if (node.File.Id == anchorFrn)
                session.DrawCircle(node.Position, node.Radius + 4,
                    RelationColor, (_selectedFrn is not null ? 3f : 1.5f) / _zoom);
            else if (_relatedFrns.Contains(node.File.Id))
                session.DrawCircle(node.Position, node.Radius + 4, RelationColor, 2f / _zoom);

            if (_dropTargetFrn == node.File.Id)
                session.DrawCircle(node.Position, node.Radius + 8, DropTargetColor, 4f / _zoom);

            if (node.Radius * _zoom >= LabelMinScreenRadius)
            {
                session.DrawText(
                    node.Label,
                    node.Position + new Vector2(0, node.Radius + 4),
                    LabelColor,
                    LabelFormat);
            }
        }

        // Drag ghost rides in screen space above everything.
        if (_dragSource is { } drag)
        {
            session.Transform = Matrix3x2.Identity;
            Color ghost = Color.FromArgb(140, DirectoryColor.R, DirectoryColor.G, DirectoryColor.B);
            session.FillCircle(_dragGhostScreen, 14, ghost);
            session.DrawText(drag.File.Name, _dragGhostScreen + new Vector2(0, 18), LabelColor, LabelFormat);
        }
    }

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
