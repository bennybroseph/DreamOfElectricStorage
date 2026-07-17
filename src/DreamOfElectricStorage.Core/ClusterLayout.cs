using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DreamOfElectricStorage.Core;

/// <summary>The relationship facets a file can be grouped by. Also used as a well's kind.</summary>
public enum WellKind { Folder, Duplicate, SimilarName, Type, Date }

/// <summary>
/// Ordered, root-first list of the ENABLED grouping facets. List position = nesting depth
/// (index 0 = root grouping). A file is grouped by <c>Enabled[0]</c>, sub-grouped within that by
/// <c>Enabled[1]</c>, and so on — a strict nested GROUP BY. Disabled facets simply aren't listed.
/// </summary>
public sealed record FacetOrder(IReadOnlyList<WellKind> Enabled)
{
    public static FacetOrder Default { get; } =
        new(new[] { WellKind.Type, WellKind.Folder, WellKind.SimilarName, WellKind.Duplicate });
}

/// <summary>
/// The always-on layout coefficients that survive the facet-list redesign. Anchor holds each node
/// near its deterministically-packed slot (so the pack's arrangement — including heavier-central —
/// IS the physics equilibrium); cohesion and repulsion are global (per-facet weights are gone —
/// nesting order decides grouping now, not weights).
/// </summary>
public sealed record LayoutTuning(
    double Anchor = 0.80,
    double Repulsion = 1.00,
    double Cohesion = 1.00);

/// <summary>
/// Deterministic force-directed layout for the Clusters view. Files are partitioned into a nested
/// facet tree (see <see cref="NestedPartition"/>); the tree is circle-packed into concentric wells
/// (<see cref="PackDeterministic"/>) so the map opens organized and overlap-free. Physics
/// (<see cref="Step"/>) then runs only as the organic settle after a reorder/toggle or a drag: each
/// node is pulled toward its ONE bucket's centroid (leaf-only cohesion — a node has a single
/// cohesion target, so the layout can't churn), repels its neighbors, feels size gravity, and PBD
/// collision keeps every node individually readable.
///
/// Headless and reproducible (seeded scatter, fixed math — no wall-clock/random).
/// Repulsion is all-pairs O(n²) below <see cref="AllPairsMax"/>, a uniform grid above.
/// </summary>
public sealed class ClusterLayout
{
    public readonly record struct Item(ulong Id, long SizeBytes);

    /// <summary>A computed well: the centroid + enclosing radius of a bucket's loose members.</summary>
    public readonly record struct Well(WellKind Kind, Vector2 Center, float Radius, int Count);

    // Force coefficients — tuned so a clear synthetic case separates cleanly. LayoutTuning scales
    // repulsion / cohesion / size-gravity on top of these.
    private const double SpringK = 0.08;       // attraction toward a well centroid (near field)
    private const double CohesionCap = 10.0;   // max cohesion force per node (·weight)
    private const double CohesionRadiusFactor = 1.25; // well's packed radius ≈ this·√(Σ member r²);
                                               // cohesion pulls only members beyond it (no compression)
    private const double RepulsionK = 1.4;     // pairwise push
    private const double AnchorK = 0.08;       // pull each node back toward its packed slot — the
                                               // pack IS the equilibrium, so this holds the arrangement
                                               // (no per-node pull-to-origin gravity, which compressed
                                               // big wells into each other at scale)
    private const double AnchorCap = 20.0;     // max anchor force per node (·weight) — large enough to
                                               // animate a reorder move, capped so it can't overshoot
    private const double InitialSpreadFactor = 10.0; // initial disk radius = this·√N·meanRadius
    private const double VelocityRetain = 0.6;  // friction: velocity kept per tick (damps to rest)
    private const double MaxForcePerNode = 60.0; // clamp so near-coincident nodes don't explode
    private const double CollisionPad = 1.5;    // gap kept between non-overlapping nodes
    private const double WellGap = 24.0;        // gap between packed wells (deterministic placement)
    private const int CollisionIters = 4;       // collision passes per step (hold packing vs attraction)
    private const double MinNodeRadius = 6.0;
    private const double MaxNodeRadius = 120.0;  // node radius spans Min..Max across the working set's
                                                 // size range (relative, stretched in log space)
    private const int AllPairsMax = 512;        // ≤ this: exact O(n²); above: spatial grid
    private const double CutoffFactor = 12.0;   // repulsion ignored past this·maxRadius (grid path)

    // Per-node sleeping: once the whole sim settles (max move drops below SleepAllBelow — the same
    // rest signal the App uses to idle the clock), every node is put to sleep. A sleeping node is
    // skipped in the relax loops (repulsion/cohesion/anchor/integrate) but stays in the grids as an
    // immovable collider. So a later drag only re-settles the awake neighborhood, not all 4000:
    // SetPosition wakes the dragged node, and collision contact wakes whatever it pushes, propagating
    // as a front; a re-pack wakes everything. Gated so it can be turned off. (A per-node quiet-timer
    // was tried first but the clock idles before any timer can fill, and the settled state jitters
    // ~0.48 forever — so sleep-at-settle is both simpler and the only thing that actually completes.)
    private const bool EnableSleeping = true;
    private const double SleepAllBelow = 0.5;   // max-move below this = settled → sleep every node

    private readonly ulong[] _ids;
    private readonly Dictionary<ulong, int> _index;
    private readonly double[] _radius;
    private readonly double[] _mass;           // normalized 0..1 (log-scaled bytes)
    private readonly double[] _x, _y;
    private readonly double[] _vx, _vy;        // velocity (PBD: derived from net movement)
    private readonly double[] _px, _py;        // positions before this tick (PBD velocity update)
    private readonly double[] _dx, _dy;        // scratch force accumulator
    private readonly double[] _ax, _ay;        // packed slot each node anchors to (set by PackDeterministic)
    private readonly bool[] _awake;            // false = sleeping (skipped in the relax loops)
    private readonly string?[][] _facetKeys;   // node → per-facet key row (indexed by (int)WellKind)
    private readonly double _initialSpread;
    private readonly double _maxRadius;
    private readonly Dictionary<long, List<int>> _grid = [];  // cell key → node indices (repulsion grid)
    private readonly Dictionary<long, List<int>> _collGrid = []; // cell key → node indices (collision grid)

    private PartitionBucket _tree = null!;                    // current nested facet partition
    private (WellKind? Kind, int[] Members)[] _cohesionGroups = []; // buckets with ≥2 loose members

    private LayoutTuning _tuning;
    /// <summary>Live layout coefficients (read each tick). Setting does not rebuild — the caller
    /// just re-steps the physics.</summary>
    public LayoutTuning Tuning
    {
        get => _tuning;
        set => _tuning = value;
    }

    private FacetOrder _order;
    /// <summary>The nesting order. Setting rebuilds the partition tree + cohesion groups (a
    /// structural change) — the caller then re-packs (<see cref="PackDeterministic"/>) and re-steps.</summary>
    public FacetOrder Order
    {
        get => _order;
        set { _order = value; BuildPartition(); }
    }

    public int Count => _ids.Length;

    /// <summary>How many nodes are currently awake (being relaxed) — the rest are asleep. Harness probe.</summary>
    public int AwakeCount
    {
        get
        {
            int c = 0;
            foreach (bool a in _awake)
                if (a)
                    c++;
            return c;
        }
    }

    public ClusterLayout(IReadOnlyList<Item> items, IReadOnlyList<string?[]> facetKeys, FacetOrder order,
        LayoutTuning? tuning = null, ulong seed = 0x5A1E0u)
    {
        int n = items.Count;
        _ids = new ulong[n];
        _index = new Dictionary<ulong, int>(n);
        _radius = new double[n];
        _mass = new double[n];
        _x = new double[n];
        _y = new double[n];
        _vx = new double[n];
        _vy = new double[n];
        _px = new double[n];
        _py = new double[n];
        _dx = new double[n];
        _dy = new double[n];
        _ax = new double[n];
        _ay = new double[n];
        _awake = new bool[n];
        Array.Fill(_awake, true); // everything starts awake; sleeps once the sim settles
        _facetKeys = new string?[n][];

        double maxRaw = 1e-9, minRaw = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            Item it = items[i];
            _ids[i] = it.Id;
            _index[it.Id] = i;
            double raw = Raw(it.SizeBytes);
            _mass[i] = raw;
            maxRaw = Math.Max(maxRaw, raw);
            minRaw = Math.Min(minRaw, raw);
            _facetKeys[i] = i < facetKeys.Count ? facetKeys[i] : Array.Empty<string?>();
        }
        // Radius is RELATIVE to the working set's size range (stretched in log space): the biggest
        // file shown always reads big and the smallest small — even when the whole set is large
        // files (top-by-size selection). Absolute log(bytes) gave almost no contrast on real data
        // (every file multi-GB → every node near the cap). Mass stays normalized to the max for
        // the pack's heaviest-first ordering.
        double rawRange = maxRaw - minRaw;
        for (int i = 0; i < n; i++)
        {
            double norm = rawRange > 1e-9 ? (_mass[i] - minRaw) / rawRange : 0.0;
            _radius[i] = MinNodeRadius + (MaxNodeRadius - MinNodeRadius) * norm;
            _mass[i] /= maxRaw; // normalize to (0,1]
        }
        _maxRadius = n > 0 ? _radius.Max() : MinNodeRadius;

        _tuning = tuning ?? new LayoutTuning();
        _order = order;
        BuildPartition();

        // Seeded initial scatter on a disk sized so nodes start well SPREAD OUT (beyond the
        // eventual equilibrium radius) and settle INWARD. PackDeterministic overwrites this at
        // startup; it only matters for a bare Solve (tests) with no pack.
        double meanRadius = n > 0 ? _radius.Average() : MinNodeRadius;
        _initialSpread = InitialSpreadFactor * Math.Sqrt(n) * (meanRadius + 4.0);
        ulong s = seed;
        double Next() { s = s * 6364136223846793005UL + 1442695040888963407UL; return (s >> 11) * (1.0 / 9007199254740992.0); }
        for (int i = 0; i < n; i++)
        {
            double angle = Next() * Math.Tau;
            double r = _initialSpread * Math.Sqrt(Next());
            _x[i] = r * Math.Cos(angle);
            _y[i] = r * Math.Sin(angle);
        }
    }

    /// <summary>Rebuild the nested partition + cohesion groups from the current facet keys + order.
    /// Every bucket with ≥2 loose members is a cohesion group; each node is a loose member of exactly
    /// one bucket, so a node has a single cohesion target and the physics cannot churn.</summary>
    private void BuildPartition()
    {
        _tree = NestedPartition.Build(_facetKeys, _order, _ids.Length);
        var groups = new List<(WellKind?, int[])>();
        void Walk(PartitionBucket b)
        {
            if (b.LooseMembers.Count >= 2)
                groups.Add((b.BindingFacet, b.LooseMembers.ToArray()));
            foreach (PartitionBucket c in b.Children)
                Walk(c);
        }
        if (_ids.Length > 0)
            Walk(_tree);
        _cohesionGroups = groups.ToArray();
    }

    private static double Raw(long size) => size <= 0 ? 1.0 : Math.Log2(1 + size / 1024.0) + 1.0;

    /// <summary>Absolute node radius from bytes (steep log curve, ~9px@1KB..120px cap). Kept for
    /// headless tooling; the layout/App use the RELATIVE per-node radius (see <see cref="RadiusOf"/>),
    /// which stretches across the working set's actual size range for real contrast.</summary>
    public static float NodeRadius(long sizeBytes)
    {
        if (sizeBytes <= 0)
            return (float)MinNodeRadius;
        double grown = MinNodeRadius + 3.0 * Math.Log2(1 + sizeBytes / 1024.0);
        return (float)Math.Min(grown, 120.0);
    }

    public Vector2 PositionOf(ulong id) =>
        _index.TryGetValue(id, out int i) ? new Vector2((float)_x[i], (float)_y[i]) : Vector2.Zero;

    /// <summary>This node's relative radius (stretched across the working set's size range) — the
    /// App renders with this so render + physics agree.</summary>
    public float RadiusOf(ulong id) =>
        _index.TryGetValue(id, out int i) ? (float)_radius[i] : (float)MinNodeRadius;

    public IReadOnlyList<Vector2> Positions()
    {
        var result = new Vector2[_ids.Length];
        for (int i = 0; i < _ids.Length; i++)
            result[i] = new Vector2((float)_x[i], (float)_y[i]);
        return result;
    }

    /// <summary>Run to a settled layout. Friction (velocity decay) makes it converge to rest
    /// on its own — no cooling schedule needed. Deterministic.</summary>
    public void Solve(int iterations = 300)
    {
        for (int k = 0; k < iterations; k++)
            Step();
    }

    /// <summary>
    /// Instant deterministic placement (no simulation): the nested facet tree is circle-packed
    /// concentrically — each leaf bucket packed tight, then its parent packs that bucket (as one
    /// circle) alongside its sibling buckets + loose members, up to the root, heaviest-first so the
    /// biggest mass lands central. Used at startup and after a reorder — the map opens organized and
    /// overlap-free; physics only runs afterward on drag / tuning change.
    /// </summary>
    public void PackDeterministic()
    {
        int n = _ids.Length;
        if (n == 0)
            return;
        PackBucket(_tree);
        for (int i = 0; i < n; i++)
        {
            _ax[i] = _x[i]; // this slot becomes the physics anchor (the pack IS the equilibrium)
            _ay[i] = _y[i];
            _vx[i] = 0;
            _vy[i] = 0;
            _awake[i] = true; // a re-pack moves every slot → everything must re-settle
        }
    }

    /// <summary>Pack one bucket, writing every subtree node's position in bucket-local coordinates
    /// centered at the origin; returns the enclosing radius.</summary>
    private double PackBucket(PartitionBucket b)
    {
        if (b.Children.Count == 0)
            return PackNodes(b.LooseMembers); // leaf / terminal — pack members directly

        // Recursively pack each child, then pack (child buckets + loose members) as siblings.
        int childCount = b.Children.Count;
        var childR = new double[childCount];
        for (int c = 0; c < childCount; c++)
            childR[c] = PackBucket(b.Children[c]);

        // entry = a child bucket (IsChild) or a loose member; sized + massed for placement.
        var entries = new List<(bool IsChild, int Ref, double R, double Mass, int MinId)>(childCount + b.LooseMembers.Count);
        for (int c = 0; c < childCount; c++)
        {
            PartitionBucket ch = b.Children[c];
            double mass = 0;
            int minId = int.MaxValue;
            foreach (int m in ch.AllMembers)
            {
                mass += _mass[m];
                if (m < minId)
                    minId = m;
            }
            entries.Add((true, c, childR[c] + WellGap, mass, minId));
        }
        foreach (int m in b.LooseMembers)
            entries.Add((false, m, _radius[m] + CollisionPad * 0.5, _mass[m], m));

        // Heaviest-first so the biggest mass packs central (matches size gravity); min-id tiebreak.
        entries.Sort((p, q) => p.Mass != q.Mass ? q.Mass.CompareTo(p.Mass) : p.MinId.CompareTo(q.MinId));

        var circles = new CirclePacker.Circle[entries.Count];
        for (int e = 0; e < entries.Count; e++)
            circles[e] = new CirclePacker.Circle(entries[e].R);
        double r = CirclePacker.Pack(circles, 0);

        for (int e = 0; e < entries.Count; e++)
        {
            double ox = circles[e].X, oy = circles[e].Y;
            if (entries[e].IsChild)
            {
                foreach (int m in b.Children[entries[e].Ref].AllMembers)
                {
                    _x[m] += ox;
                    _y[m] += oy;
                }
            }
            else
            {
                _x[entries[e].Ref] = ox;
                _y[entries[e].Ref] = oy;
            }
        }
        return r;
    }

    /// <summary>Pack a flat member set biggest-radius-first (densest), centered at the origin.</summary>
    private double PackNodes(IReadOnlyList<int> members)
    {
        if (members.Count == 0)
            return 0;
        int[] nodes = [.. members.OrderByDescending(m => _radius[m]).ThenBy(m => m)];
        var circles = new CirclePacker.Circle[nodes.Length];
        for (int k = 0; k < nodes.Length; k++)
            circles[k] = new CirclePacker.Circle(_radius[nodes[k]] + CollisionPad * 0.5);
        double r = CirclePacker.Pack(circles, 0);
        for (int k = 0; k < nodes.Length; k++)
        {
            _x[nodes[k]] = circles[k].X;
            _y[nodes[k]] = circles[k].Y;
        }
        return r;
    }

    /// <summary>
    /// One relaxation tick (velocity-Verlet with friction): accumulate forces → velocity,
    /// damp velocity, advance positions, then resolve collisions. Friction bleeds off energy
    /// so attraction/collision reach equilibrium and motion decays to zero — the map settles
    /// by itself. Returns the largest node move this tick (→ 0 at rest).
    /// </summary>
    public double Step() => Relax();

    /// <summary>Pin a node to a world position (drag) — overrides forces + velocity for it.</summary>
    public void SetPosition(ulong id, Vector2 world)
    {
        if (_index.TryGetValue(id, out int i))
        {
            _x[i] = world.X;
            _y[i] = world.Y;
            _vx[i] = 0;
            _vy[i] = 0;
            _awake[i] = true; // the dragged node is always live (and wakes what it touches)
        }
    }

    private double Relax()
    {
        int n = _ids.Length;
        Array.Clear(_dx);
        Array.Clear(_dy);

        // Snapshot positions for the PBD velocity derivation (below), for ALL nodes incl. sleepers —
        // so a sleeper woken by collision this tick derives its net move against its real pre-tick
        // position, not a stale pre-sleep one.
        for (int i = 0; i < n; i++)
        {
            _px[i] = _x[i];
            _py[i] = _y[i];
        }

        // Repulsion — every pair pushes apart, stronger for bigger nodes and when closer.
        // Exact O(n²) for small sets; a uniform spatial grid with a distance cutoff above
        // AllPairsMax (repulsion is negligible past a few radii anyway). Sleeping nodes are skipped
        // as force TARGETS but stay in the grid as SOURCES, so awake nodes still repel away from them.
        double rep = RepulsionK * Math.Max(_tuning.Repulsion, 0);
        if (n <= AllPairsMax)
        {
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (EnableSleeping && !_awake[i] && !_awake[j])
                        continue; // both asleep → already spaced, no work
                    Repel(i, j, rep);
                }
        }
        else
        {
            RepelGrid(rep);
        }

        // Cohesion — each node is pulled toward its ONE bucket's centroid (the same nested partition
        // the pack uses, so physics agrees with the packed layout and settles). Only the distance
        // BEYOND the bucket's packed radius pulls, so a gathered well isn't compressed below packing.
        // Every node is a loose member of exactly one bucket → a single cohesion target, never the
        // competing multi-well pull that caused the old default-weights churn.
        double cw = _tuning.Cohesion;
        if (cw > 0)
        {
            foreach (var (_, members) in _cohesionGroups)
            {
                double cx = 0, cy = 0, sumR2 = 0;
                foreach (int m in members)
                {
                    cx += _x[m]; cy += _y[m]; sumR2 += _radius[m] * _radius[m];
                }
                cx /= members.Length; cy /= members.Length;
                double packedRadius = CohesionRadiusFactor * Math.Sqrt(sumR2);
                double cap = CohesionCap * cw;
                foreach (int m in members)
                {
                    if (EnableSleeping && !_awake[m])
                        continue; // sleeper won't integrate the pull anyway
                    double dx = cx - _x[m], dy = cy - _y[m];
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    double excess = dist - packedRadius;
                    if (excess <= 0 || dist < 1e-9)
                        continue;
                    double f = Math.Min(SpringK * cw * excess, cap);
                    _dx[m] += dx / dist * f;
                    _dy[m] += dy / dist * f;
                }
            }
        }

        // Anchor — pull each node back toward its packed slot. The deterministic pack already
        // arranges wells heaviest-central and overlap-free, so THAT is the resting state; anchoring
        // to it makes the pack the physics equilibrium (fast settle, no drift) and, crucially,
        // replaces the old pull-to-origin size gravity, which — applied per node toward one point —
        // dragged every big well into the center and crammed them together at 4000-node scale.
        // On a reorder the slots move (re-pack) so this springs the map from the old to the new
        // arrangement; on a drag the flung node springs back to its slot.
        double aw = Math.Max(_tuning.Anchor, 0);
        if (aw > 0)
        {
            double ak = AnchorK * aw;
            double acap = AnchorCap * aw;
            for (int i = 0; i < n; i++)
            {
                if (EnableSleeping && !_awake[i])
                    continue;
                double dx = _ax[i] - _x[i], dy = _ay[i] - _y[i];
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-9)
                    continue;
                double f = Math.Min(ak * dist, acap);
                _dx[i] += dx / dist * f;
                _dy[i] += dy / dist * f;
            }
        }

        // Position-Based Dynamics integration. Predict positions from damped velocity + forces,
        // then resolve collisions as a hard position constraint, then DERIVE velocity from the
        // actual net movement. The derivation is what makes it converge: when attraction and
        // collision reach a standoff the net move → 0, so velocity → 0 and the map settles —
        // no cooling bandaid. (Plain velocity integration never zeroes because collision fixes
        // position but not the velocity the forces keep pumping.)
        for (int i = 0; i < n; i++)
        {
            if (EnableSleeping && !_awake[i])
                continue; // sleeper: position + velocity held (still a collider via the grids)
            double flen = Math.Sqrt(_dx[i] * _dx[i] + _dy[i] * _dy[i]);
            if (flen > MaxForcePerNode) // clamp: near-coincident nodes would explode
            {
                _dx[i] *= MaxForcePerNode / flen;
                _dy[i] *= MaxForcePerNode / flen;
            }
            _vx[i] = (_vx[i] + _dx[i]) * VelocityRetain;
            _vy[i] = (_vy[i] + _dy[i]) * VelocityRetain;
            _x[i] += _vx[i];
            _y[i] += _vy[i];
        }

        // Hard non-overlap constraint (readable, individually interactable nodes). Wakes any sleeper
        // it has to push, so a drag's displacement propagates as a wake-front through the cluster.
        ResolveCollisions();

        // Velocity ← actual net movement (post-constraint). Collision-cancelled motion zeroes out.
        double maxMove = 0;
        for (int i = 0; i < n; i++)
        {
            if (EnableSleeping && !_awake[i])
                continue; // sleeper contributes no motion
            _vx[i] = _x[i] - _px[i];
            _vy[i] = _y[i] - _py[i];
            double move = Math.Sqrt(_vx[i] * _vx[i] + _vy[i] * _vy[i]);
            if (move > maxMove)
                maxMove = move;
        }

        // Sim settled → put every node to sleep, so the next interaction only re-settles the
        // neighborhood it disturbs. Same threshold the App uses to idle the clock, so this fires
        // exactly as motion stops (the settled state jitters ~0.48 and never reaches 0).
        if (EnableSleeping && maxMove < SleepAllBelow)
        {
            for (int i = 0; i < n; i++)
            {
                if (!_awake[i])
                    continue;
                _awake[i] = false;
                _vx[i] = 0;
                _vy[i] = 0;
            }
        }
        return maxMove;
    }

    private void ResolveCollisions()
    {
        for (int iter = 0; iter < CollisionIters; iter++)
            CollisionPass();
    }

    /// <summary>
    /// One position-based collision pass (grid-accelerated): separates overlapping node pairs
    /// to r_i+r_j+pad. Deterministic (cells fill in index order, pairs applied at min index).
    /// </summary>
    private void CollisionPass()
    {
        int n = _ids.Length;
        double cell = 2.0 * _maxRadius + CollisionPad;
        foreach (var list in _collGrid.Values)
            list.Clear();
        for (int i = 0; i < n; i++)
        {
            long key = CellKey(_x[i], _y[i], cell);
            if (!_collGrid.TryGetValue(key, out var list))
                _collGrid[key] = list = [];
            list.Add(i);
        }
        for (int i = 0; i < n; i++)
        {
            if (EnableSleeping && !_awake[i])
                continue; // an awake neighbor handles any pair involving this sleeper
            int cx = (int)Math.Floor(_x[i] / cell);
            int cy = (int)Math.Floor(_y[i] / cell);
            for (int gx = cx - 1; gx <= cx + 1; gx++)
                for (int gy = cy - 1; gy <= cy + 1; gy++)
                {
                    if (!_collGrid.TryGetValue(Pack(gx, gy), out var cellList))
                        continue;
                    foreach (int j in cellList)
                    {
                        bool jAwake = !EnableSleeping || _awake[j];
                        if (jAwake && j <= i)
                            continue; // awake-awake once at min index (also skips j==i); a sleeper j is handled here
                        double dx = _x[i] - _x[j], dy = _y[i] - _y[j];
                        double dist = Math.Sqrt(dx * dx + dy * dy) + 1e-9;
                        double min = _radius[i] + _radius[j] + CollisionPad;
                        if (dist < min)
                        {
                            double push = (min - dist) * 0.5;
                            double ux = dx / dist, uy = dy / dist;
                            _x[i] += ux * push; _y[i] += uy * push;
                            _x[j] -= ux * push; _y[j] -= uy * push;
                            if (!jAwake)
                                _awake[j] = true; // contact wakes the sleeper we just pushed
                        }
                    }
                }
        }
    }

    /// <summary>Apply the symmetric repulsion force to a single pair.</summary>
    private void Repel(int i, int j, double rep)
    {
        double ddx = _x[i] - _x[j];
        double ddy = _y[i] - _y[j];
        double dist2 = ddx * ddx + ddy * ddy + 1e-6;
        double dist = Math.Sqrt(dist2);
        double sr = _radius[i] + _radius[j];
        // Falls off with distance; a small floor keeps coincident points from exploding.
        double f = rep * sr * sr / dist2;
        double ux = ddx / dist, uy = ddy / dist;
        _dx[i] += ux * f; _dy[i] += uy * f;
        _dx[j] -= ux * f; _dy[j] -= uy * f;
    }

    /// <summary>
    /// Repulsion via a uniform grid: each node only repels those in its own + 8 neighbor
    /// cells (cell size = cutoff), so far pairs — whose 1/dist² force is negligible — are
    /// skipped. Deterministic: cells fill in node-index order, pairs applied at min index.
    /// </summary>
    private void RepelGrid(double rep)
    {
        int n = _ids.Length;
        double cutoff = CutoffFactor * _maxRadius;
        double cutoff2 = cutoff * cutoff;
        foreach (var list in _grid.Values)
            list.Clear();

        for (int i = 0; i < n; i++)
        {
            long key = CellKey(_x[i], _y[i], cutoff);
            if (!_grid.TryGetValue(key, out var list))
                _grid[key] = list = [];
            list.Add(i);
        }

        for (int i = 0; i < n; i++)
        {
            if (EnableSleeping && !_awake[i])
                continue; // sleeper's own repulsion is skipped; awake neighbors still repel away from it
            int cx = (int)Math.Floor(_x[i] / cutoff);
            int cy = (int)Math.Floor(_y[i] / cutoff);
            for (int gx = cx - 1; gx <= cx + 1; gx++)
                for (int gy = cy - 1; gy <= cy + 1; gy++)
                {
                    if (!_grid.TryGetValue(Pack(gx, gy), out var cell))
                        continue;
                    foreach (int j in cell)
                    {
                        bool jAwake = !EnableSleeping || _awake[j];
                        if (jAwake && j <= i)
                            continue; // dedup awake-awake (also j==i); a sleeper j is repelled-from here
                        double ddx = _x[i] - _x[j], ddy = _y[i] - _y[j];
                        if (ddx * ddx + ddy * ddy <= cutoff2)
                            Repel(i, j, rep);
                    }
                }
        }
    }

    private static long CellKey(double x, double y, double cell) =>
        Pack((int)Math.Floor(x / cell), (int)Math.Floor(y / cell));

    private static long Pack(int gx, int gy) => ((long)gx << 32) ^ (uint)gy;

    /// <summary>Well descriptors (centroid + enclosing radius) — one per named bucket (a bucket with
    /// ≥2 loose members and a binding facet). The root "misc" pile has no binding facet and is skipped.</summary>
    public IReadOnlyList<Well> Wells()
    {
        var wells = new List<Well>(_cohesionGroups.Length);
        foreach (var (kind, members) in _cohesionGroups)
        {
            if (kind is not { } k)
                continue;
            double cx = 0, cy = 0;
            foreach (int m in members) { cx += _x[m]; cy += _y[m]; }
            cx /= members.Length; cy /= members.Length;
            double reach = 0;
            foreach (int m in members)
            {
                double d = Math.Sqrt((_x[m] - cx) * (_x[m] - cx) + (_y[m] - cy) * (_y[m] - cy)) + _radius[m];
                reach = Math.Max(reach, d);
            }
            wells.Add(new Well(k, new Vector2((float)cx, (float)cy), (float)reach, members.Length));
        }
        return wells;
    }
}
