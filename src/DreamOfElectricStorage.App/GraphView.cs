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

    private static readonly CanvasTextFormat LabelFormat = new()
    {
        FontSize = 12,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Top,
    };

    private readonly record struct GraphNode(FileNode File, VolumeIndex Volume, Vector2 Position, float Radius, string Label);

    private MachineIndex? _machine;
    private readonly List<GraphNode> _nodes = [];

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

        if (_parentTrail.Count == 0)
            _currentVolume = null; // back to machine level
        else
            _parentTrail.Pop();
        RebuildLevel();
    }

    /// <summary>Hit-test a screen point; drills into the volume/directory if one was hit.</summary>
    public void OnTapped(Vector2 screenPoint)
    {
        Vector2 world = (screenPoint - _pan) / _zoom;
        // Nodes are laid out sparsely; linear nearest-hit is fine at level sizes.
        foreach (GraphNode node in _nodes)
        {
            if (Vector2.DistanceSquared(world, node.Position) > node.Radius * node.Radius)
                continue;

            if (_currentVolume is null)
            {
                _currentVolume = node.Volume;
                _parentTrail.Clear();
            }
            else if (node.File.IsDirectory)
            {
                _parentTrail.Push(node.File.Id);
            }
            else
            {
                return; // file click: no action this slice
            }

            RebuildLevel();
            return;
        }
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
                _nodes.Add(new GraphNode(
                    File: new FileNode(0, 0, volume.Volume, 0, IsDirectory: true),
                    Volume: volume,
                    Position: Spiral(i++),
                    Radius: DirectoryRadius * 2,
                    Label: $"{volume.Volume}  ({volume.Count:N0})"));
            }
        }
        else
        {
            IReadOnlyList<FileNode> children = _parentTrail.Count == 0
                ? _currentVolume.RootEntries
                : _currentVolume.GetChildren(_parentTrail.Peek());

            // Directories first, then files; largest levels stay readable.
            int i = 0;
            foreach (FileNode child in children.OrderByDescending(c => c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                _nodes.Add(new GraphNode(
                    File: child,
                    Volume: _currentVolume,
                    Position: Spiral(i++),
                    Radius: child.IsDirectory ? DirectoryRadius : FileRadius,
                    Label: child.Name));
            }
        }

        if (resetCamera)
            ResetCamera(_lastViewport);
        LevelChanged?.Invoke();
    }

    private static Vector2 Spiral(int i) =>
        SpiralSpacing * MathF.Sqrt(i) * new Vector2(MathF.Cos(i * GoldenAngle), MathF.Sin(i * GoldenAngle));

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
