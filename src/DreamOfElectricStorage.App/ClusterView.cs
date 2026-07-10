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
/// The Clusters view — a relationship-well map driven by <see cref="ClusterLayout"/>.
/// Files are the nodes (fill = type, rim = home drive, size ∝ bytes); folders and
/// relationships are wells the force layout pulls them into. Live but calm physics runs
/// while unsettled, then the clock idles. Dragging flings a node and it re-settles — it
/// never moves the file. C2 runs on the demo working set; the scalable global relationship
/// index is C5.
/// </summary>
public sealed class ClusterView : ISceneView
{
    private const int MaxWorkingSet = 4000;      // C2 demo bound; C5 does real-scale scoping
    private const double DateBucketDays = 1.0;

    // Home-drive palette (mirrors the design mockup): white C:, cyan D:, violet E:.
    private static readonly (char Letter, Color Color)[] DrivePalette =
    [
        ('C', Color.FromArgb(255, 236, 240, 246)),
        ('D', Color.FromArgb(255, 79, 209, 255)),
        ('E', Color.FromArgb(255, 176, 107, 255)),
    ];

    private static readonly Dictionary<FileTypeCategory, Color> TypeColor =
        GraphView.TypePalette.ToDictionary(t => t.Category, t => t.Color);

    private readonly record struct NodeMeta(
        string Name, float Radius, Color Fill, Color Rim, VolumeIndex Volume, ulong Frn,
        string? DupKey, string? NameKey);

    private readonly AnimationClock _clock = new();
    private ClusterLayout? _layout;
    private NodeMeta[] _meta = [];
    private IReadOnlyList<Vector2> _positions = [];

    private Vector2 _lastViewport = new(1600, 900);
    private bool _pendingInitialFit;
    private double _lastMove;
    private int? _hoverIndex;

    private ulong? _dragId;
    private Vector2 _dragWorld;

    public GraphCamera Camera { get; } = new();
    public bool LightTheme { get; set; }

    private bool _reduceMotion;
    public bool ReduceMotion
    {
        get => _reduceMotion;
        set { _reduceMotion = value; Camera.Instant = value; }
    }

    private ForceWeights _weights = new();
    public ForceWeights Weights
    {
        get => _weights;
        set { _weights = value; if (_layout is not null) { _layout.Weights = value; _clock.RequestFrames(); } }
    }

    public event Action? RedrawNeeded;

    public ClusterView()
    {
        _clock.Tick += Advance;
        _clock.IsIdle = () => Camera.Settled && _dragId is null && _lastMove < 0.05;
    }

    private void Advance(float dt)
    {
        if (_layout is not null)
        {
            if (_dragId is { } id)
                _layout.SetPosition(id, _dragWorld);
            _lastMove = _layout.Step(dt);
            _positions = _layout.Positions();
        }
        Camera.Advance(dt);
        RedrawNeeded?.Invoke();
    }

    public void SetIndex(MachineIndex machine)
    {
        _hoverIndex = null;
        _dragId = null;

        var meta = new List<NodeMeta>();
        var items = new List<ClusterLayout.Item>();
        // Group buckets keyed by string → global (sequential) ids.
        var folder = new Dictionary<string, List<ulong>>();
        var dup = new Dictionary<string, List<ulong>>();
        var name = new Dictionary<string, List<ulong>>();
        var type = new Dictionary<string, List<ulong>>();
        var date = new Dictionary<string, List<ulong>>();

        foreach (VolumeIndex volume in machine.Volumes)
        {
            Color rim = DriveColor(volume.Volume);
            foreach (FileNode file in EnumerateFiles(volume))
            {
                if (meta.Count >= MaxWorkingSet)
                    break;
                ulong gid = (ulong)meta.Count;
                FileTypeCategory cat = FileTypeClassifier.Classify(file.Name);
                string dupKey = $"{file.Name.ToLowerInvariant()}|{file.SizeBytes}";
                string nameKey = NameStem.Normalize(file.Name);

                items.Add(new ClusterLayout.Item(gid, file.SizeBytes));
                meta.Add(new NodeMeta(file.Name, ClusterLayout.NodeRadius(file.SizeBytes),
                    TypeColor.GetValueOrDefault(cat, TypeColor[FileTypeCategory.Other]), rim,
                    volume, file.Id, dupKey, nameKey));

                Add(folder, $"{volume.Volume}|{file.ParentId}", gid);
                Add(dup, dupKey, gid);
                if (nameKey.Length > 0)
                    Add(name, nameKey, gid);
                Add(type, $"{cat}", gid);
                if (file.LastWriteFileTime > 0)
                    Add(date, $"{file.LastWriteFileTime / (long)(DateBucketDays * 864_000_000_000L)}", gid);
            }
        }

        _meta = meta.ToArray();
        var groups = new List<ClusterLayout.Group>();
        AddGroups(groups, WellKind.Folder, folder);
        AddGroups(groups, WellKind.Duplicate, dup);
        AddGroups(groups, WellKind.SimilarName, name);
        AddGroups(groups, WellKind.Type, type);
        AddGroups(groups, WellKind.Date, date);

        _layout = new ClusterLayout(items, groups, _weights);
        _layout.Solve(150); // settle before first paint so the view opens calm, not chaotic
        _positions = _layout.Positions();
        _pendingInitialFit = true;
        _clock.RequestFrames();
    }

    private static void Add(Dictionary<string, List<ulong>> map, string key, ulong gid)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(gid);
    }

    private static void AddGroups(List<ClusterLayout.Group> groups, WellKind kind, Dictionary<string, List<ulong>> map)
    {
        foreach (var list in map.Values)
            if (list.Count > 1) // a well needs at least two members
                groups.Add(new ClusterLayout.Group(kind, list));
    }

    /// <summary>DFS every file (leaf) under a volume, biggest-first within each folder.</summary>
    private static IEnumerable<FileNode> EnumerateFiles(VolumeIndex volume)
    {
        var stack = new Stack<FileNode>(volume.RootEntries);
        while (stack.Count > 0)
        {
            FileNode node = stack.Pop();
            if (node.IsDirectory)
            {
                foreach (FileNode child in volume.GetChildren(node.Id))
                    stack.Push(child);
            }
            else
            {
                yield return node;
            }
        }
    }

    private static Color DriveColor(string volume)
    {
        char letter = char.ToUpperInvariant(volume.Length > 0 ? volume[0] : '?');
        foreach (var (l, color) in DrivePalette)
            if (l == letter)
                return color;
        return Color.FromArgb(255, 150, 158, 170);
    }

    private float ContentExtent()
    {
        float reach = 40f;
        for (int i = 0; i < _positions.Count; i++)
            reach = MathF.Max(reach, _positions[i].Length() + _meta[i].Radius);
        return reach + 30f;
    }

    public void Draw(CanvasControl canvas, CanvasDrawingSession session)
    {
        _lastViewport = new Vector2((float)canvas.ActualWidth, (float)canvas.ActualHeight);
        if (_pendingInitialFit && _positions.Count > 0)
        {
            Camera.ZoomToFit(_lastViewport, ContentExtent(), fly: false);
            _pendingInitialFit = false;
        }

        session.Transform = Camera.Transform;
        float zoom = Camera.Zoom;
        Color labelColor = LightTheme ? Color.FromArgb(255, 27, 34, 46) : Color.FromArgb(220, 231, 236, 245);

        // Relationship lines: hovered node → co-members of its duplicate / similar-name well.
        if (_hoverIndex is { } h && h < _meta.Length)
        {
            NodeMeta hm = _meta[h];
            Vector2 hp = _positions[h];
            for (int i = 0; i < _meta.Length; i++)
            {
                if (i == h)
                    continue;
                bool related = (hm.DupKey is { } dk && _meta[i].DupKey == dk)
                    || (hm.NameKey is { Length: > 0 } nk && _meta[i].NameKey == nk);
                if (related)
                    session.DrawLine(hp, _positions[i], Color.FromArgb(150, 255, 120, 190), 2f / zoom);
            }
        }

        for (int i = 0; i < _meta.Length; i++)
        {
            NodeMeta m = _meta[i];
            Vector2 p = _positions[i];
            float r = m.Radius;
            bool hovered = _hoverIndex == i;
            if (hovered)
                r *= 1.18f;

            session.FillCircle(p, r, m.Fill);
            // Rim = home drive. Thin in world units so it stays a hairline at any zoom.
            session.DrawCircle(p, r, m.Rim, MathF.Max(1.5f, r * 0.14f));

            if (r * zoom > 12f)
            {
                using var format = new CanvasTextFormat { FontSize = MathF.Min(13f, r) / zoom, WordWrapping = CanvasWordWrapping.NoWrap };
                session.DrawText(m.Name, p + new Vector2(0, r + 3f / zoom),
                    WithAlpha(labelColor, hovered ? 1f : 0.75f), format);
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
        int? hit = HitTest(screenPoint);
        if (hit == _hoverIndex)
            return false;
        _hoverIndex = hit;
        return true;
    }

    private int? HitTest(Vector2 screenPoint)
    {
        Vector2 world = Camera.ScreenToWorld(screenPoint);
        int? hit = null;
        for (int i = 0; i < _meta.Length; i++)
            if ((world - _positions[i]).LengthSquared() <= _meta[i].Radius * _meta[i].Radius)
                hit = i; // last (topmost) wins
        return hit;
    }

    // --- drag-fling (cluster-specific; not part of ISceneView) ---

    public bool TryGrab(Vector2 screenPoint)
    {
        if (HitTest(screenPoint) is not { } i || _layout is null)
            return false;
        _dragId = (ulong)i;
        _dragWorld = _positions[i];
        _clock.RequestFrames();
        return true;
    }

    public void DragTo(Vector2 screenPoint)
    {
        _dragWorld = Camera.ScreenToWorld(screenPoint);
        _clock.RequestFrames();
    }

    public void Release()
    {
        _dragId = null;
        _clock.RequestFrames(); // let it re-settle (springs back = the fling)
    }

    public bool IsGrabbing => _dragId is not null;

    public IReadOnlyList<GraphView.VisibleNode> GetVisibleNodes()
    {
        var result = new List<GraphView.VisibleNode>(_meta.Length);
        for (int i = 0; i < _meta.Length; i++)
            result.Add(new GraphView.VisibleNode(
                _meta[i].Name, _meta[i].Frn, Camera.WorldToScreen(_positions[i]),
                _meta[i].Radius * Camera.Zoom, false));
        return result;
    }

    private static Color WithAlpha(Color color, float alpha) =>
        Color.FromArgb((byte)(color.A * Math.Clamp(alpha, 0f, 1f)), color.R, color.G, color.B);
}
