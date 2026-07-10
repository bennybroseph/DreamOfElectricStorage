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
    private const int MaxWorkingSet = 4000;      // working-set cap fed to the layout/physics
    private const int SolveIterations = 120;      // off-thread settle iterations at load

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
        string Name, long Bytes, float Radius, Color Fill, Color Rim, VolumeIndex Volume, ulong Frn,
        string? DupKey, string? NameKey);

    /// <summary>A visual cluster of settled nodes — the LOD summary unit.</summary>
    private readonly record struct SummaryCluster(
        int[] Members, Vector2 Center, float Radius, long Bytes, Color Fill);

    private readonly AnimationClock _clock = new();
    private ClusterLayout? _layout;
    private NodeMeta[] _meta = [];
    private IReadOnlyList<Vector2> _positions = [];

    private Vector2 _lastViewport = new(1600, 900);
    private bool _pendingInitialFit;
    private double _lastMove;
    private int? _hoverIndex;

    // LOD: spatial clusters (recomputed from settled positions) summarize when far,
    // expand to files when near. ExpandStart/Full are the cluster screen-radius crossfade.
    private const float ExpandStart = 70f;   // below this screen radius → fully summarized
    private const float ExpandFull = 150f;   // above → fully expanded to files
    private const double LinkFactor = 1.35;  // neighbors within (r_i+r_j)·this join a cluster
    private List<SummaryCluster>? _clusterCache;
    private bool _clustersDirty = true;

    private ulong? _dragId;
    private Vector2 _dragWorld;
    private bool _settling; // initial Solve running off-thread; layout is bg-owned

    // Perf probe (harness `perf` verb): Solve at load + per-frame step/draw/cluster timings.
    private readonly System.Diagnostics.Stopwatch _timer = new();
    private double _solveMs, _lastStepMs, _lastDrawMs, _lastClusterMs;
    private double _maxStepMs;

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
        set { _weights = value; _clustersDirty = true; if (_layout is not null && !_settling) { _layout.Weights = value; _clock.RequestFrames(); } }
    }

    public event Action? RedrawNeeded;

    public ClusterView()
    {
        _clock.Tick += Advance;
        _clock.IsIdle = () => Camera.Settled && _dragId is null && _lastMove < 0.05;
    }

    private void Advance(float dt)
    {
        // While the initial Solve runs off-thread, the layout is owned by that thread —
        // don't touch it here (just keep the camera easing).
        if (_layout is not null && !_settling)
        {
            if (_dragId is { } id)
                _layout.SetPosition(id, _dragWorld);
            _timer.Restart();
            _lastMove = _layout.Step(dt);
            _positions = _layout.Positions();
            _lastStepMs = _timer.Elapsed.TotalMilliseconds;
            _maxStepMs = Math.Max(_maxStepMs, _lastStepMs);
            _clustersDirty = true; // positions moved → LOD clusters stale
        }
        Camera.Advance(dt);
        RedrawNeeded?.Invoke();
    }

    public int TotalEligible { get; private set; }

    /// <summary>True while the initial layout is settling off-thread (nothing to draw yet).</summary>
    public bool IsSettling => _settling;

    public void SetIndex(MachineIndex machine)
    {
        _hoverIndex = null;
        _dragId = null;

        // Core builds the bounded working set (top-N by size) + relationship groups.
        ClusterGraph graph = ClusterGraphBuilder.Build(machine.Volumes, MaxWorkingSet);
        TotalEligible = graph.TotalEligible;

        _meta = graph.Nodes.Select(info => new NodeMeta(
            info.Name, info.SizeBytes, ClusterLayout.NodeRadius(info.SizeBytes),
            TypeColor.GetValueOrDefault(info.Category, TypeColor[FileTypeCategory.Other]),
            DriveColor(info.Drive), info.Volume, info.Frn,
            $"{info.Name.ToLowerInvariant()}|{info.SizeBytes}", NameStem.Normalize(info.Name)))
            .ToArray();

        var layout = new ClusterLayout(graph.Items, graph.Groups, _weights);
        _layout = layout;
        _positions = [];
        _settling = true;
        RedrawNeeded?.Invoke(); // paint the "arranging…" placeholder immediately

        // Settle off the UI thread — a big working set's Solve can take seconds and must not
        // freeze the window. The layout is bg-owned until this completes.
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var solveTimer = System.Diagnostics.Stopwatch.StartNew();
        Task.Run(() =>
        {
            layout.Solve(SolveIterations);
            solveTimer.Stop();
            var settled = layout.Positions();
            dispatcher.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_layout, layout))
                    return; // a newer SetIndex superseded this solve
                _positions = settled;
                _solveMs = solveTimer.Elapsed.TotalMilliseconds;
                _maxStepMs = 0;
                _settling = false;
                _clustersDirty = true;
                _pendingInitialFit = true;
                _clock.RequestFrames();
                RedrawNeeded?.Invoke();
            });
        });
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
        _timer.Restart();
        _lastViewport = new Vector2((float)canvas.ActualWidth, (float)canvas.ActualHeight);

        if (_settling)
        {
            session.Transform = Matrix3x2.Identity;
            Color msg = LightTheme ? Color.FromArgb(200, 60, 68, 82) : Color.FromArgb(200, 170, 178, 190);
            using var f = new CanvasTextFormat { FontSize = 15f, HorizontalAlignment = CanvasHorizontalAlignment.Center };
            session.DrawText($"arranging {_meta.Length:N0} files…", _lastViewport / 2f, msg, f);
            return;
        }

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

        // LOD: each spatial cluster crossfades between a summary bubble (far) and its files
        // (near) by its on-screen radius. Singletons always draw as a file.
        foreach (SummaryCluster c in GetClusters())
        {
            float screenR = c.Radius * zoom;
            float summaryAlpha = c.Members.Length <= 1 ? 0f
                : Math.Clamp((ExpandFull - screenR) / (ExpandFull - ExpandStart), 0f, 1f);

            if (summaryAlpha < 0.999f)
                foreach (int i in c.Members)
                    DrawFile(session, i, zoom, 1f - summaryAlpha, labelColor);

            if (summaryAlpha > 0.001f)
                DrawSummary(session, c, zoom, summaryAlpha, labelColor);
        }
        _lastDrawMs = _timer.Elapsed.TotalMilliseconds;
    }

    /// <summary>Harness perf snapshot for the Clusters view.</summary>
    public string PerfReport()
    {
        string report = $"nodes={_meta.Length} clusters={_clusterCache?.Count ?? 0}\n"
            + $"solve={_solveMs:F1}ms step(last/max)={_lastStepMs:F1}/{_maxStepMs:F1}ms cluster={_lastClusterMs:F1}ms draw={_lastDrawMs:F1}ms";
        _maxStepMs = _lastStepMs;
        return report;
    }

    private void DrawFile(CanvasDrawingSession session, int i, float zoom, float alpha, Color labelColor)
    {
        NodeMeta m = _meta[i];
        Vector2 p = _positions[i];
        float r = m.Radius;
        bool hovered = _hoverIndex == i;
        if (hovered)
            r *= 1.18f;

        session.FillCircle(p, r, WithAlpha(m.Fill, alpha));
        // Rim = home drive. Thin in world units so it stays a hairline at any zoom.
        session.DrawCircle(p, r, WithAlpha(m.Rim, alpha), MathF.Max(1.5f, r * 0.14f));

        if (alpha > 0.6f && r * zoom > 12f)
        {
            using var format = new CanvasTextFormat { FontSize = MathF.Min(13f, r) / zoom, WordWrapping = CanvasWordWrapping.NoWrap };
            session.DrawText(m.Name, p + new Vector2(0, r + 3f / zoom),
                WithAlpha(labelColor, (hovered ? 1f : 0.75f) * alpha), format);
        }
    }

    private void DrawSummary(CanvasDrawingSession session, SummaryCluster c, float zoom, float alpha, Color labelColor)
    {
        session.FillCircle(c.Center, c.Radius, WithAlpha(c.Fill, alpha * 0.9f));
        session.DrawCircle(c.Center, c.Radius, WithAlpha(labelColor, alpha * 0.4f), 2f / zoom);

        string label = $"{c.Members.Length} items\n{GraphView.FormatSize(c.Bytes)}";
        float fontSize = MathF.Max(9f, MathF.Min(c.Radius * 0.28f, 22f)) / zoom;
        using var format = new CanvasTextFormat
        {
            FontSize = fontSize,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        var box = new Windows.Foundation.Rect(c.Center.X - c.Radius, c.Center.Y - c.Radius, c.Radius * 2, c.Radius * 2);
        session.DrawText(label, box, WithAlpha(labelColor, alpha), format);
    }

    private List<SummaryCluster> GetClusters()
    {
        if (!_clustersDirty && _clusterCache is not null)
            return _clusterCache;
        var clusterTimer = System.Diagnostics.Stopwatch.StartNew();
        _clusterCache = ComputeClusters();
        _lastClusterMs = clusterTimer.Elapsed.TotalMilliseconds;
        _clustersDirty = false;
        return _clusterCache;
    }

    /// <summary>
    /// Single-linkage spatial clustering of settled positions (union-find): nodes whose
    /// centers are within (r_i+r_j)·LinkFactor join — within a well they touch, between
    /// wells they don't. A uniform grid (cell = max link distance) keeps it near-linear so
    /// it stays cheap on the full working set.
    /// </summary>
    private List<SummaryCluster> ComputeClusters()
    {
        int n = _meta.Length;
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;
        int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }

        float maxR = 0f;
        for (int i = 0; i < n; i++)
            maxR = MathF.Max(maxR, _meta[i].Radius);
        double cell = MathF.Max(2f * maxR * (float)LinkFactor, 1f); // covers the largest possible link
        var grid = new Dictionary<long, List<int>>();
        for (int i = 0; i < n; i++)
        {
            long key = CellKey(_positions[i], cell);
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = [];
            list.Add(i);
        }

        for (int i = 0; i < n; i++)
        {
            int gx = (int)Math.Floor(_positions[i].X / cell);
            int gy = (int)Math.Floor(_positions[i].Y / cell);
            for (int nx = gx - 1; nx <= gx + 1; nx++)
                for (int ny = gy - 1; ny <= gy + 1; ny++)
                {
                    if (!grid.TryGetValue(Pack(nx, ny), out var neighbors))
                        continue;
                    foreach (int j in neighbors)
                    {
                        if (j <= i)
                            continue;
                        double link = (_meta[i].Radius + _meta[j].Radius) * LinkFactor;
                        if ((_positions[i] - _positions[j]).LengthSquared() <= link * link)
                            parent[Find(i)] = Find(j);
                    }
                }
        }

        var byRoot = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out var list))
                byRoot[root] = list = [];
            list.Add(i);
        }

        var clusters = new List<SummaryCluster>(byRoot.Count);
        foreach (var members in byRoot.Values)
        {
            Vector2 center = Vector2.Zero;
            long bytes = 0;
            var typeCount = new Dictionary<uint, int>();
            foreach (int i in members)
            {
                center += _positions[i];
                bytes += _meta[i].Bytes;
                uint key = ColorKey(_meta[i].Fill);
                typeCount[key] = typeCount.GetValueOrDefault(key) + 1;
            }
            center /= members.Count;
            float reach = 0f;
            foreach (int i in members)
                reach = MathF.Max(reach, (_positions[i] - center).Length() + _meta[i].Radius);
            // Dominant type color (by count) tints the summary bubble.
            Color fill = _meta[members[0]].Fill;
            int best = -1;
            foreach (int i in members)
            {
                int cnt = typeCount[ColorKey(_meta[i].Fill)];
                if (cnt > best) { best = cnt; fill = _meta[i].Fill; }
            }
            clusters.Add(new SummaryCluster(members.ToArray(), center, reach, bytes, fill));
        }
        return clusters;
    }

    private static uint ColorKey(Color c) => (uint)(c.R << 16 | c.G << 8 | c.B);

    private static long CellKey(Vector2 p, double cell) =>
        Pack((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell));

    private static long Pack(int gx, int gy) => ((long)gx << 32) ^ (uint)gy;

    /// <summary>Harness view of the LOD state: one line per cluster (count, screen radius, state).</summary>
    public IReadOnlyList<(int Count, float ScreenRadius, string State)> ClusterSummaries()
    {
        float zoom = Camera.Zoom;
        return GetClusters().Select(c =>
        {
            float sr = c.Radius * zoom;
            string state = c.Members.Length <= 1 ? "single"
                : sr <= ExpandStart ? "summary"
                : sr >= ExpandFull ? "files" : "fade";
            return (c.Members.Length, sr, state);
        }).ToList();
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
