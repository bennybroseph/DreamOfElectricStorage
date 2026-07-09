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

    /// <summary>Tap: drill into the volume/directory that was hit (files inert on single tap).</summary>
    public void OnTapped(Vector2 screenPoint)
    {
        if (TryGetNodeAt(screenPoint) is not { } hit)
            return;

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
            return;
        }

        RebuildLevel();
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

        // Edges: every node back to the level's center (hierarchy spokes).
        foreach (GraphNode node in _nodes)
            session.DrawLine(Vector2.Zero, node.Position, EdgeColor, 1.5f / _zoom);

        foreach (GraphNode node in _nodes)
        {
            Color fill = node.File.IsDirectory ? DirectoryColor : FileColor;
            session.FillCircle(node.Position, node.Radius, fill);

            if (_highlightedFrn == node.File.Id && _currentVolume is not null)
                session.DrawCircle(node.Position, node.Radius + 6, HighlightColor, 3f / _zoom);

            if (node.Radius * _zoom >= LabelMinScreenRadius)
            {
                session.DrawText(
                    node.Label,
                    node.Position + new Vector2(0, node.Radius + 4),
                    LabelColor,
                    LabelFormat);
            }
        }
    }
}
