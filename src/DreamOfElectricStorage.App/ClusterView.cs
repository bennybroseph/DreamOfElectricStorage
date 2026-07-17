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
    private const float LabelMinScreenR = 22f; // a file needs this screen radius to be labeled
    private const int MaxLabels = 60;          // biggest-first label budget per frame
    private readonly List<(int Index, float ScreenR)> _labelPass = [];
    private readonly List<(Vector2 Center, float ScreenR, string Text, float Alpha)> _summaryLabels = [];
    private List<SummaryCluster>? _clusterCache;
    private bool _clustersDirty = true;

    private ulong? _dragId;
    private Vector2 _dragWorld;

    // Live physics converges by friction (PBD in ClusterLayout) — motion decays to rest on
    // its own. Idle when the largest per-tick move is sub-pixel (a principled rest criterion,
    // not a cooling schedule): 0.5 world units is imperceptible at any real zoom.
    private const float SettleThreshold = 0.5f;

    // Populate entrance: nodes fade/scale in biggest-first over EntranceStagger+Rise seconds.
    // Positions are final from the start (deterministic pack) — this is pure visual.
    private const float EntranceStagger = 0.7f;
    private const float EntranceRise = 0.45f;
    private float _entranceElapsed = 999f; // done by default
    private float[] _appearDelay = [];
    private bool Entrancing => _entranceElapsed < EntranceStagger + EntranceRise;

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

    private LayoutTuning _tuning = new();
    /// <summary>Always-on layout coefficients (size gravity / cohesion / repulsion). Setting
    /// re-steps the physics live — no re-pack (only the nesting order restructures the map).</summary>
    public LayoutTuning Tuning
    {
        get => _tuning;
        set { _tuning = value; _clustersDirty = true; if (_layout is not null) { _layout.Tuning = value; _lastMove = 1f; _clock.RequestFrames(); } }
    }

    private FacetOrder _order = FacetOrder.Default;
    /// <summary>The nesting order (root grouping first). Setting re-packs deterministically (the
    /// map re-forms instantly, overlap-free) then re-settles the physics from there.</summary>
    public FacetOrder Order
    {
        get => _order;
        set
        {
            _order = value;
            _clustersDirty = true;
            if (_layout is not null)
            {
                _layout.Order = value;
                _layout.PackDeterministic();
                _positions = _layout.Positions();
                _lastMove = 1f;
                _clock.RequestFrames();
            }
        }
    }

    public event Action? RedrawNeeded;

    public ClusterView()
    {
        _clock.Tick += Advance;
        _clock.IsIdle = () => Camera.Settled && _dragId is null && _lastMove < SettleThreshold && !Entrancing;
    }

    private void Advance(float dt)
    {
        if (Entrancing)
            _entranceElapsed += dt; // pure visual populate; positions already final

        if (_layout is not null)
        {
            if (_dragId is { } id)
                _layout.SetPosition(id, _dragWorld);
            if (_lastMove >= SettleThreshold || _dragId is not null)
            {
                _timer.Restart();
                _lastMove = _layout.Step();     // friction converges → _lastMove decays to 0
                _positions = _layout.Positions();
                _lastStepMs = _timer.Elapsed.TotalMilliseconds;
                _maxStepMs = Math.Max(_maxStepMs, _lastStepMs);
                _clustersDirty = true;          // positions moved → LOD clusters stale
            }
        }
        Camera.Advance(dt);
        RedrawNeeded?.Invoke();
    }

    public int TotalEligible { get; private set; }

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

        // Deterministic instant placement (no cold-start simulation) — organized + overlap-free
        // from the start. Physics only runs afterward on slider/drag.
        var layout = new ClusterLayout(graph.Items, graph.FacetKeys, _order, _tuning);
        var solveTimer = System.Diagnostics.Stopwatch.StartNew();
        layout.PackDeterministic();
        _solveMs = solveTimer.Elapsed.TotalMilliseconds;
        _layout = layout;
        _positions = layout.Positions();

        // Radii are relative to THIS working set's size range (stretched) — pull them from the
        // layout so render matches physics/packing exactly.
        for (int i = 0; i < _meta.Length; i++)
            _meta[i] = _meta[i] with { Radius = layout.RadiusOf((ulong)i) };

        // Populate animation: nodes fade/scale in biggest-first (positions are already final).
        _appearDelay = new float[_meta.Length];
        int[] order = [.. Enumerable.Range(0, _meta.Length).OrderByDescending(i => _meta[i].Radius)];
        for (int rank = 0; rank < order.Length; rank++)
            _appearDelay[order[rank]] = order.Length > 1 ? (float)rank / (order.Length - 1) * EntranceStagger : 0f;
        _entranceElapsed = 0f;

        _lastMove = 0f;          // placed at rest — physics idle until slider/drag
        _clustersDirty = true;
        _pendingInitialFit = true;
        _clock.RequestFrames();
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

        // Visible world bounds (+margin) — everything outside is culled so cost scales with
        // what's on screen, not the whole working set.
        Vector2 tl = Camera.ScreenToWorld(Vector2.Zero);
        Vector2 br = Camera.ScreenToWorld(_lastViewport);
        const float margin = 160f; // covers the max node radius (120) + hover swell

        // LOD: each spatial cluster crossfades between a summary bubble (far) and its files
        // (near) by its on-screen radius. Circles/bubbles draw in world space; labels are
        // gathered and drawn afterward in SCREEN space (fixed px) — drawing text under the
        // world transform makes the font size `px/zoom`, which explodes at a tiny fit-zoom
        // (real data spreads far) and blows up Win2D's text layout.
        _labelPass.Clear();
        _summaryLabels.Clear();
        bool entrancing = Entrancing;
        foreach (SummaryCluster c in GetClusters())
        {
            float screenR = c.Radius * zoom;
            // During the populate animation draw files (not summaries) so you watch them appear.
            float summaryAlpha = entrancing || c.Members.Length <= 1 ? 0f
                : Math.Clamp((ExpandFull - screenR) / (ExpandFull - ExpandStart), 0f, 1f);

            if (summaryAlpha < 0.999f)
                foreach (int i in c.Members)
                {
                    Vector2 p = _positions[i];
                    if (p.X < tl.X - margin || p.X > br.X + margin || p.Y < tl.Y - margin || p.Y > br.Y + margin)
                        continue; // off-screen
                    float et = entrancing ? EntranceT(i) : 1f;
                    if (et <= 0.001f)
                        continue; // not appeared yet
                    DrawFile(session, i, zoom, (1f - summaryAlpha) * et, 0.4f + 0.6f * et);
                    float sr = _meta[i].Radius * zoom;
                    if (!entrancing && (1f - summaryAlpha) > 0.6f && sr > LabelMinScreenR)
                        _labelPass.Add((i, sr));
                }

            if (summaryAlpha > 0.001f
                && c.Center.X > tl.X - c.Radius && c.Center.X < br.X + c.Radius
                && c.Center.Y > tl.Y - c.Radius && c.Center.Y < br.Y + c.Radius)
            {
                DrawSummary(session, c, zoom, summaryAlpha, labelColor);
                _summaryLabels.Add((c.Center, screenR,
                    $"{c.Members.Length} items\n{GraphView.FormatSize(c.Bytes)}", summaryAlpha));
            }
        }

        DrawLabels(session, labelColor);
        _lastDrawMs = _timer.Elapsed.TotalMilliseconds;
    }

    /// <summary>All text drawn in SCREEN space (fixed px) so zoom can't explode the font size.
    /// File labels are biggest-first and budgeted; summary labels are centered in their bubble.</summary>
    private void DrawLabels(CanvasDrawingSession session, Color labelColor)
    {
        session.Transform = Matrix3x2.Identity;

        // Summary bubbles: centered "N items / size", sized to the bubble's on-screen radius.
        using (var fmt = new CanvasTextFormat
        {
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        })
        {
            foreach (var (center, screenR, text, alpha) in _summaryLabels)
            {
                fmt.FontSize = Math.Clamp(screenR * 0.28f, 9f, 22f);
                Vector2 sp = Camera.WorldToScreen(center);
                var box = new Windows.Foundation.Rect(sp.X - screenR, sp.Y - screenR, screenR * 2, screenR * 2);
                session.DrawText(text, box, WithAlpha(labelColor, alpha), fmt);
            }
        }

        // File labels: budgeted, biggest-first, below the node.
        if (_labelPass.Count == 0)
            return;
        _labelPass.Sort((a, b) => b.ScreenR.CompareTo(a.ScreenR));
        int count = Math.Min(_labelPass.Count, MaxLabels);
        using var fileFmt = new CanvasTextFormat { FontSize = 12f, WordWrapping = CanvasWordWrapping.NoWrap };
        for (int k = 0; k < count; k++)
        {
            int i = _labelPass[k].Index;
            float alpha = _hoverIndex == i ? 1f : 0.8f;
            Vector2 sp = Camera.WorldToScreen(_positions[i]);
            session.DrawText(_meta[i].Name, sp + new Vector2(0, _labelPass[k].ScreenR + 3f),
                WithAlpha(labelColor, alpha), fileFmt);
        }
    }

    /// <summary>Harness perf snapshot for the Clusters view.</summary>
    public string PerfReport()
    {
        string report = $"nodes={_meta.Length} awake={_layout?.AwakeCount ?? 0} clusters={_clusterCache?.Count ?? 0} entrance={Entrancing} lastMove={_lastMove:F3} overlaps={CountOverlaps()}\n"
            + $"solve={_solveMs:F1}ms step(last/max)={_lastStepMs:F1}/{_maxStepMs:F1}ms cluster={_lastClusterMs:F1}ms draw={_lastDrawMs:F1}ms";
        _maxStepMs = _lastStepMs;
        return report;
    }

    /// <summary>Entrance progress for node i (0 = not appeared, 1 = full); biggest appear first.</summary>
    private float EntranceT(int i)
    {
        float t = (_entranceElapsed - _appearDelay[i]) / EntranceRise;
        return Easings.CubicOut(Math.Clamp(t, 0f, 1f));
    }

    private void DrawFile(CanvasDrawingSession session, int i, float zoom, float alpha, float scale)
    {
        NodeMeta m = _meta[i];
        Vector2 p = _positions[i];
        float r = (_hoverIndex == i ? m.Radius * 1.18f : m.Radius) * scale;

        session.FillCircle(p, r, WithAlpha(m.Fill, alpha));
        // Rim = home drive. Thin in world units so it stays a hairline at any zoom.
        session.DrawCircle(p, r, WithAlpha(m.Rim, alpha), MathF.Max(1.5f, r * 0.14f));
    }

    private void DrawSummary(CanvasDrawingSession session, SummaryCluster c, float zoom, float alpha, Color labelColor)
    {
        // Bubble only — the "N items / size" label is drawn in the screen-space pass.
        session.FillCircle(c.Center, c.Radius, WithAlpha(c.Fill, alpha * 0.9f));
        session.DrawCircle(c.Center, c.Radius, WithAlpha(labelColor, alpha * 0.4f), 2f / zoom);
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

    /// <summary>Harness proof of non-overlap: count node pairs that visibly overlap (grid-accelerated).</summary>
    private int CountOverlaps()
    {
        int n = _meta.Length;
        if (n == 0 || _positions.Count != n) // settling: positions not yet populated
            return 0;
        float maxR = 0f;
        for (int i = 0; i < n; i++)
            maxR = MathF.Max(maxR, _meta[i].Radius);
        double cell = 2.0 * maxR + 1;
        var grid = new Dictionary<long, List<int>>();
        for (int i = 0; i < n; i++)
        {
            long key = CellKey(_positions[i], cell);
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = [];
            list.Add(i);
        }
        int overlaps = 0;
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
                        float sr = _meta[i].Radius + _meta[j].Radius;
                        if ((_positions[i] - _positions[j]).LengthSquared() < sr * sr * 0.9f) // 5%+ penetration
                            overlaps++;
                    }
                }
        }
        return overlaps;
    }

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
        _lastMove = 1f;         // keep stepping so friction re-settles from the flung position
        _clock.RequestFrames();
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
