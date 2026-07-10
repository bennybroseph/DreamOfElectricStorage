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
/// The Clusters view — a relationship-well map, distinct from GraphView's circle-pack
/// hierarchy. C0 scaffold: a static, deterministic per-drive cluster layout that proves
/// the view toggle end-to-end. The real force-directed layout (relationship + folder
/// wells, size gravity, live physics) lands in later slices.
/// </summary>
public sealed class ClusterView : ISceneView
{
    private const int MaxNodesPerCluster = 60;   // biggest N root entries per drive (stub)
    private const float ClusterPadding = 10f;

    // Home-drive palette (mirrors the design mockup): white C:, cyan D:, violet E:.
    private static readonly (char Letter, Color Color)[] DrivePalette =
    [
        ('C', Color.FromArgb(255, 236, 240, 246)),
        ('D', Color.FromArgb(255, 79, 209, 255)),
        ('E', Color.FromArgb(255, 176, 107, 255)),
    ];

    private readonly record struct ClusterNode(
        Vector2 Position, float Radius, Color Fill, string Name, bool IsDirectory, ulong Frn);

    private readonly record struct Cluster(Vector2 Center, float Radius, string Label, Color Accent);

    private readonly AnimationClock _clock = new();
    private readonly List<ClusterNode> _nodes = [];
    private readonly List<Cluster> _clusters = [];

    private Vector2 _lastViewport = new(1600, 900);
    private bool _pendingInitialFit;
    private ulong? _hoveredFrn;

    public GraphCamera Camera { get; } = new();
    public bool LightTheme { get; set; }

    private bool _reduceMotion;
    public bool ReduceMotion
    {
        get => _reduceMotion;
        set { _reduceMotion = value; Camera.Instant = value; }
    }

    public event Action? RedrawNeeded;

    public ClusterView()
    {
        _clock.Tick += Advance;
        _clock.IsIdle = () => Camera.Settled;
    }

    private void Advance(float dt)
    {
        Camera.Advance(dt);
        RedrawNeeded?.Invoke();
    }

    public void SetIndex(MachineIndex machine)
    {
        _nodes.Clear();
        _clusters.Clear();
        _hoveredFrn = null;

        // One blob per drive, biggest total pinned at center, lighter drives fanning out —
        // a nod to the real size-driven layout, enough to read as clusters not one blob.
        var built = new List<(FileNode[] Files, VolumeIndex Volume, long Total)>();
        foreach (VolumeIndex volume in machine.Volumes)
        {
            FileNode[] files = volume.RootEntries
                .OrderByDescending(f => f.SizeBytes)
                .Take(MaxNodesPerCluster)
                .ToArray();
            if (files.Length == 0)
                continue;
            built.Add((files, volume, files.Sum(f => f.SizeBytes)));
        }

        built.Sort((a, b) => b.Total.CompareTo(a.Total));

        // Pack each drive's entries into its own blob, then place the blobs: heaviest at
        // origin, the rest on a ring sized to clear the central blob.
        var packs = built.Select(b =>
        {
            var circles = b.Files.Select(f => new CirclePacker.Circle(NodeRadius(f))).ToArray();
            float radius = (float)CirclePacker.Pack(circles, ClusterPadding);
            return (b.Files, b.Volume, radius, circles);
        }).ToList();

        float ringRadius = packs.Count > 0 ? packs[0].radius * 2.6f : 0f;
        for (int i = 0; i < packs.Count; i++)
        {
            var (files, volume, radius, circles) = packs[i];
            Vector2 center = i == 0
                ? Vector2.Zero
                : ringRadius * Direction((i - 1) / (float)Math.Max(1, packs.Count - 1));
            // Outer blobs sit fully outside the central one.
            if (i > 0)
                center *= 1f + radius / MathF.Max(ringRadius, 1f);

            Color accent = DriveColor(volume.Volume);
            _clusters.Add(new Cluster(center, radius, $"{volume.Volume}  ({volume.Count:N0} items)", accent));
            for (int j = 0; j < files.Length; j++)
            {
                var c = circles[j];
                _nodes.Add(new ClusterNode(
                    center + new Vector2((float)c.X, (float)c.Y), (float)c.R, accent,
                    files[j].Name, files[j].IsDirectory, files[j].Id));
            }
        }

        _pendingInitialFit = true;
        _clock.RequestFrames();
    }

    private static Vector2 Direction(float t)
    {
        float angle = t * MathF.Tau - MathF.PI / 2f; // start at top, go clockwise
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }

    private static float NodeRadius(FileNode node)
    {
        float baseRadius = node.IsDirectory ? 14f : 6f;
        if (node.SizeBytes <= 0)
            return baseRadius;
        float grown = baseRadius + 1.6f * MathF.Log2(1 + node.SizeBytes / 1024f);
        return MathF.Min(grown, node.IsDirectory ? 52f : 40f);
    }

    private static Color DriveColor(string volume)
    {
        char letter = char.ToUpperInvariant(volume.Length > 0 ? volume[0] : '?');
        foreach (var (l, color) in DrivePalette)
            if (l == letter)
                return color;
        return Color.FromArgb(255, 150, 158, 170); // unknown drive
    }

    private float ContentExtent()
    {
        float reach = 0f;
        foreach (Cluster c in _clusters)
            reach = MathF.Max(reach, c.Center.Length() + c.Radius);
        return reach + 40f;
    }

    public void Draw(CanvasControl canvas, CanvasDrawingSession session)
    {
        _lastViewport = new Vector2((float)canvas.ActualWidth, (float)canvas.ActualHeight);
        if (_pendingInitialFit)
        {
            Camera.ZoomToFit(_lastViewport, ContentExtent(), fly: false);
            _pendingInitialFit = false;
        }

        session.Transform = Camera.Transform;
        float zoom = Camera.Zoom;
        Color labelColor = LightTheme ? Color.FromArgb(255, 27, 34, 46) : Color.FromArgb(235, 231, 236, 245);
        Color ringColor = LightTheme ? Color.FromArgb(70, 90, 120, 150) : Color.FromArgb(70, 122, 168, 210);

        // Cluster boundaries + labels first (behind the nodes).
        foreach (Cluster c in _clusters)
        {
            session.DrawCircle(c.Center, c.Radius, WithAlpha(ringColor, 1f), 2f / zoom);
            float screenR = c.Radius * zoom;
            if (screenR > 40f)
            {
                using var format = new CanvasTextFormat { FontSize = 14f / zoom, WordWrapping = CanvasWordWrapping.NoWrap };
                session.DrawText(c.Label, c.Center - new Vector2(0, c.Radius + 22f / zoom),
                    WithAlpha(c.Accent, 1f), format);
            }
        }

        foreach (ClusterNode n in _nodes)
        {
            bool hovered = _hoveredFrn == n.Frn;
            Color fill = hovered ? Lighten(n.Fill) : n.Fill;
            session.FillCircle(n.Position, n.Radius, fill);
            if (!n.IsDirectory)
                session.DrawCircle(n.Position, n.Radius, WithAlpha(ringColor, 1f), 1f / zoom);

            if (n.Radius * zoom > 11f)
            {
                using var format = new CanvasTextFormat { FontSize = MathF.Min(13f, n.Radius) / zoom, WordWrapping = CanvasWordWrapping.NoWrap };
                session.DrawText(n.Name, n.Position + new Vector2(0, n.Radius + 3f / zoom),
                    WithAlpha(labelColor, 0.85f), format);
            }
        }
    }

    public void Pan(Vector2 screenDelta) => Camera.PanBy(screenDelta);

    public void Zoom(float wheelDelta, Vector2 screenCenter)
    {
        Camera.ZoomAboutPoint(wheelDelta, screenCenter);
        _clock.RequestFrames();
    }

    public void ZoomHome()
    {
        Camera.ZoomToFit(_lastViewport, ContentExtent(), fly: true);
        _clock.RequestFrames();
    }

    public bool SetHover(Vector2 screenPoint)
    {
        Vector2 world = Camera.ScreenToWorld(screenPoint);
        ulong? hit = null;
        foreach (ClusterNode n in _nodes)
            if ((world - n.Position).LengthSquared() <= n.Radius * n.Radius)
                hit = n.Frn; // last (topmost) wins
        if (hit == _hoveredFrn)
            return false;
        _hoveredFrn = hit;
        return true;
    }

    public IReadOnlyList<GraphView.VisibleNode> GetVisibleNodes() =>
        _nodes.Select(n => new GraphView.VisibleNode(
            n.Name, n.Frn, Camera.WorldToScreen(n.Position), n.Radius * Camera.Zoom, n.IsDirectory))
            .ToList();

    private static Color WithAlpha(Color color, float alpha) =>
        Color.FromArgb((byte)(color.A * Math.Clamp(alpha, 0f, 1f)), color.R, color.G, color.B);

    private static Color Lighten(Color c) =>
        Color.FromArgb(c.A, (byte)Math.Min(255, c.R + 30), (byte)Math.Min(255, c.G + 30), (byte)Math.Min(255, c.B + 30));
}
