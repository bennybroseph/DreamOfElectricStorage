using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DreamOfElectricStorage.Core;

/// <summary>The kind of pull that binds a well together — each has its own tunable weight.</summary>
public enum WellKind { Folder, Duplicate, SimilarName, Type, Date }

/// <summary>Per-force strengths (0..1-ish). Surfaced as settings sliders in the App.</summary>
public sealed record ForceWeights(
    double SizeGravity = 0.80,
    double Duplicate = 0.90,
    double SimilarName = 0.75,
    double Folder = 0.50,
    double Date = 0.30,
    double Type = 0.20,
    double Repulsion = 1.00)
{
    public double Of(WellKind kind) => kind switch
    {
        WellKind.Duplicate => Duplicate,
        WellKind.SimilarName => SimilarName,
        WellKind.Folder => Folder,
        WellKind.Date => Date,
        WellKind.Type => Type,
        _ => 0,
    };
}

/// <summary>
/// Deterministic force-directed layout for the Clusters view. Items are pulled toward the
/// centroid of every well they belong to (each pull scaled by that well kind's weight),
/// repel one another so wells don't overlap, and feel a size-scaled gravity that draws the
/// heaviest mass to the center — so heavy wells sit central and lighter ones fan out.
///
/// Headless and reproducible (seeded initial layout, fixed math — no wall-clock/random).
/// <see cref="Solve"/> runs to a settled layout for tests/first paint; <see cref="Step"/>
/// advances one relaxation tick for the live (calm) physics in the App.
///
/// Repulsion is all-pairs O(n²); intended for the visible working set (hundreds of nodes),
/// not a whole volume. A spatial grid is the escalation if a working set ever gets large.
/// </summary>
public sealed class ClusterLayout
{
    public readonly record struct Item(ulong Id, long SizeBytes);
    public readonly record struct Group(WellKind Kind, IReadOnlyList<ulong> MemberIds);

    /// <summary>A computed well: the centroid + enclosing radius of a group's members.</summary>
    public readonly record struct Well(WellKind Kind, Vector2 Center, float Radius, int Count);

    // Force coefficients — tuned so a clear synthetic case separates cleanly. The per-kind
    // ForceWeights scale on top of these.
    private const double SpringK = 0.08;       // attraction toward a well centroid
    private const double RepulsionK = 1.4;     // pairwise push
    private const double GravityK = 0.010;     // size-scaled pull to origin
    private const double MinNodeRadius = 6.0;
    private const int AllPairsMax = 512;        // ≤ this: exact O(n²); above: spatial grid
    private const double CutoffFactor = 12.0;   // repulsion ignored past this·maxRadius (grid path)

    private readonly ulong[] _ids;
    private readonly Dictionary<ulong, int> _index;
    private readonly double[] _radius;
    private readonly double[] _mass;           // normalized 0..1 (log-scaled bytes)
    private readonly double[] _x, _y;
    private readonly double[] _dx, _dy;        // scratch displacement
    private readonly (WellKind Kind, int[] Members)[] _groups;
    private readonly double _initialSpread;
    private readonly double _maxRadius;
    private readonly Dictionary<long, List<int>> _grid = [];  // cell key → node indices (grid path)

    public ForceWeights Weights { get; set; } = new();
    public int Count => _ids.Length;

    public ClusterLayout(IReadOnlyList<Item> items, IReadOnlyList<Group> groups, ForceWeights? weights = null, ulong seed = 0x5A1E0u)
    {
        int n = items.Count;
        _ids = new ulong[n];
        _index = new Dictionary<ulong, int>(n);
        _radius = new double[n];
        _mass = new double[n];
        _x = new double[n];
        _y = new double[n];
        _dx = new double[n];
        _dy = new double[n];
        if (weights is not null)
            Weights = weights;

        double maxRaw = 1e-9;
        for (int i = 0; i < n; i++)
        {
            Item it = items[i];
            _ids[i] = it.Id;
            _index[it.Id] = i;
            double raw = Raw(it.SizeBytes);
            _mass[i] = raw;
            maxRaw = Math.Max(maxRaw, raw);
            _radius[i] = NodeRadius(it.SizeBytes);
        }
        for (int i = 0; i < n; i++)
            _mass[i] /= maxRaw; // normalize to (0,1]
        _maxRadius = n > 0 ? _radius.Max() : MinNodeRadius;

        // Seeded initial scatter on a disk sized to the total node area — deterministic.
        double totalArea = _radius.Sum(r => r * r) + 1;
        _initialSpread = 3.0 * Math.Sqrt(totalArea);
        ulong s = seed;
        double Next() { s = s * 6364136223846793005UL + 1442695040888963407UL; return (s >> 11) * (1.0 / 9007199254740992.0); }
        for (int i = 0; i < n; i++)
        {
            double angle = Next() * Math.Tau;
            double r = _initialSpread * Math.Sqrt(Next());
            _x[i] = r * Math.Cos(angle);
            _y[i] = r * Math.Sin(angle);
        }

        _groups = groups
            .Select(g => (g.Kind, g.MemberIds.Where(_index.ContainsKey).Select(id => _index[id]).ToArray()))
            .Where(g => g.Item2.Length > 0)
            .ToArray();
    }

    private static double Raw(long size) => size <= 0 ? 1.0 : Math.Log2(1 + size / 1024.0) + 1.0;

    /// <summary>Screen/world node radius from bytes — shared with the App's rendering scale.</summary>
    public static float NodeRadius(long sizeBytes)
    {
        if (sizeBytes <= 0)
            return (float)MinNodeRadius;
        double grown = MinNodeRadius + 1.6 * Math.Log2(1 + sizeBytes / 1024.0);
        return (float)Math.Min(grown, 40.0);
    }

    public Vector2 PositionOf(ulong id) =>
        _index.TryGetValue(id, out int i) ? new Vector2((float)_x[i], (float)_y[i]) : Vector2.Zero;

    public IReadOnlyList<Vector2> Positions()
    {
        var result = new Vector2[_ids.Length];
        for (int i = 0; i < _ids.Length; i++)
            result[i] = new Vector2((float)_x[i], (float)_y[i]);
        return result;
    }

    /// <summary>Run to a settled layout: many cooling relaxation steps. Deterministic.</summary>
    public void Solve(int iterations = 300)
    {
        double t0 = _initialSpread * 0.10;
        for (int k = 0; k < iterations; k++)
        {
            double t = t0 * (1.0 - k / (double)iterations); // linear cooling → 0
            Relax(Math.Max(t, 0.5));
        }
    }

    /// <summary>One live relaxation tick (calm physics); returns the largest node move.</summary>
    public double Step(double dt)
    {
        double cap = _initialSpread * 0.04 * Math.Clamp(dt * 60.0, 0.1, 2.0);
        return Relax(cap);
    }

    /// <summary>Pin a node to a world position (drag) — overrides forces for this node.</summary>
    public void SetPosition(ulong id, Vector2 world)
    {
        if (_index.TryGetValue(id, out int i))
        {
            _x[i] = world.X;
            _y[i] = world.Y;
        }
    }

    private double Relax(double maxStep)
    {
        int n = _ids.Length;
        Array.Clear(_dx);
        Array.Clear(_dy);

        // Repulsion — every pair pushes apart, stronger for bigger nodes and when closer.
        // Exact O(n²) for small sets; a uniform spatial grid with a distance cutoff above
        // AllPairsMax (repulsion is negligible past a few radii anyway).
        double rep = RepulsionK * Math.Max(Weights.Repulsion, 0);
        if (n <= AllPairsMax)
        {
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    Repel(i, j, rep);
        }
        else
        {
            RepelGrid(rep);
        }

        // Attraction — each item springs toward the centroid of every well it belongs to,
        // scaled by that well kind's weight (so a file sits between its pulls).
        foreach (var (kind, members) in _groups)
        {
            double w = Weights.Of(kind);
            if (w <= 0 || members.Length < 2)
                continue;
            double cx = 0, cy = 0;
            foreach (int m in members) { cx += _x[m]; cy += _y[m]; }
            cx /= members.Length; cy /= members.Length;
            double pull = SpringK * w;
            foreach (int m in members)
            {
                _dx[m] += (cx - _x[m]) * pull;
                _dy[m] += (cy - _y[m]) * pull;
            }
        }

        // Size gravity — heavier mass is pulled harder to the origin, so heavy wells settle
        // central and light ones drift outward (balanced by repulsion).
        double grav = GravityK * Math.Max(Weights.SizeGravity, 0);
        for (int i = 0; i < n; i++)
        {
            _dx[i] -= _x[i] * grav * _mass[i];
            _dy[i] -= _y[i] * grav * _mass[i];
        }

        // Integrate with a per-step cap (temperature) — keeps the relaxation stable.
        double maxMove = 0;
        for (int i = 0; i < n; i++)
        {
            double len = Math.Sqrt(_dx[i] * _dx[i] + _dy[i] * _dy[i]);
            if (len > maxStep)
            {
                _dx[i] *= maxStep / len;
                _dy[i] *= maxStep / len;
            }
            _x[i] += _dx[i];
            _y[i] += _dy[i];
            maxMove = Math.Max(maxMove, Math.Sqrt(_dx[i] * _dx[i] + _dy[i] * _dy[i]));
        }
        return maxMove;
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
            int cx = (int)Math.Floor(_x[i] / cutoff);
            int cy = (int)Math.Floor(_y[i] / cutoff);
            for (int gx = cx - 1; gx <= cx + 1; gx++)
                for (int gy = cy - 1; gy <= cy + 1; gy++)
                {
                    if (!_grid.TryGetValue(Pack(gx, gy), out var cell))
                        continue;
                    foreach (int j in cell)
                    {
                        if (j <= i)
                            continue; // each unordered pair once, at the smaller index
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

    /// <summary>Well descriptors (centroid + enclosing radius) for the current positions.</summary>
    public IReadOnlyList<Well> Wells()
    {
        var wells = new List<Well>(_groups.Length);
        foreach (var (kind, members) in _groups)
        {
            double cx = 0, cy = 0;
            foreach (int m in members) { cx += _x[m]; cy += _y[m]; }
            cx /= members.Length; cy /= members.Length;
            double reach = 0;
            foreach (int m in members)
            {
                double d = Math.Sqrt((_x[m] - cx) * (_x[m] - cx) + (_y[m] - cy) * (_y[m] - cy)) + _radius[m];
                reach = Math.Max(reach, d);
            }
            wells.Add(new Well(kind, new Vector2((float)cx, (float)cy), (float)reach, members.Length));
        }
        return wells;
    }
}
