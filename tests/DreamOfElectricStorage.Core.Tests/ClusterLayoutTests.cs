using System.Numerics;
using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class ClusterLayoutTests
{
    private static ClusterLayout.Item Item(ulong id, long size = 1024) => new(id, size);

    private static Vector2 Centroid(IReadOnlyList<Vector2> pts)
    {
        Vector2 sum = Vector2.Zero;
        foreach (Vector2 p in pts)
            sum += p;
        return sum / pts.Count;
    }

    private static double MeanPairDistance(IEnumerable<Vector2> pts)
    {
        var list = pts.ToList();
        double sum = 0;
        int count = 0;
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                sum += (list[i] - list[j]).Length();
                count++;
            }
        return count == 0 ? 0 : sum / count;
    }

    [Fact]
    public void Solve_IsDeterministic()
    {
        var items = Enumerable.Range(1, 30).Select(i => Item((ulong)i, i * 4096)).ToList();
        var groups = new List<ClusterLayout.Group>
        {
            new(WellKind.Duplicate, new ulong[] { 1, 2, 3, 4, 5 }),
            new(WellKind.SimilarName, new ulong[] { 10, 11, 12 }),
        };

        var a = new ClusterLayout(items, groups);
        var b = new ClusterLayout(items, groups);
        a.Solve();
        b.Solve();

        var pa = a.Positions();
        var pb = b.Positions();
        for (int i = 0; i < pa.Count; i++)
            Assert.Equal(pa[i], pb[i]);
    }

    [Fact]
    public void Solve_ProducesFinitePositions()
    {
        var items = Enumerable.Range(1, 60).Select(i => Item((ulong)i, i * 1_000_000L)).ToList();
        var groups = new List<ClusterLayout.Group>
        {
            new(WellKind.Duplicate, Enumerable.Range(1, 10).Select(i => (ulong)i).ToArray()),
            new(WellKind.Folder, Enumerable.Range(20, 15).Select(i => (ulong)i).ToArray()),
        };
        var layout = new ClusterLayout(items, groups);
        layout.Solve();

        foreach (Vector2 p in layout.Positions())
        {
            Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y));
            Assert.True(p.Length() < 1e7f);
        }
    }

    [Fact]
    public void SameWellMembers_ClusterCloserThanCrossWell()
    {
        // Two disjoint duplicate wells, no size gravity — should form two separated blobs.
        var items = Enumerable.Range(1, 20).Select(i => Item((ulong)i)).ToList();
        ulong[] wellA = Enumerable.Range(1, 10).Select(i => (ulong)i).ToArray();
        ulong[] wellB = Enumerable.Range(11, 10).Select(i => (ulong)i).ToArray();
        var groups = new List<ClusterLayout.Group>
        {
            new(WellKind.Duplicate, wellA),
            new(WellKind.Duplicate, wellB),
        };
        var layout = new ClusterLayout(items, groups, new ForceWeights(SizeGravity: 0));
        layout.Solve();

        Vector2 P(ulong id) => layout.PositionOf(id);
        double intraA = MeanPairDistance(wellA.Select(P));
        double intraB = MeanPairDistance(wellB.Select(P));
        Vector2 cA = Centroid(wellA.Select(P).ToList());
        Vector2 cB = Centroid(wellB.Select(P).ToList());
        double inter = (cA - cB).Length();

        Assert.True(inter > intraA && inter > intraB,
            $"wells not separated: inter {inter:F1} vs intraA {intraA:F1}, intraB {intraB:F1}");
    }

    [Fact]
    public void HeavierMass_SettlesNearerCenter()
    {
        // No wells — only size gravity + repulsion. Heavy nodes should end up central.
        var items = Enumerable.Range(1, 40).Select(i => Item((ulong)i, (long)i * i * 100_000L)).ToList();
        var layout = new ClusterLayout(items, new List<ClusterLayout.Group>(), new ForceWeights(SizeGravity: 1.0));
        layout.Solve();

        var positions = layout.Positions();
        Vector2 center = Centroid(positions);
        // Ids 1..40 ascend in size; compare lightest quartile vs heaviest quartile.
        double lightMean = Enumerable.Range(1, 10)
            .Average(i => (layout.PositionOf((ulong)i) - center).Length());
        double heavyMean = Enumerable.Range(31, 10)
            .Average(i => (layout.PositionOf((ulong)i) - center).Length());

        Assert.True(heavyMean < lightMean,
            $"heavy nodes not central: heavyMean {heavyMean:F1} >= lightMean {lightMean:F1}");
    }

    [Fact]
    public void StrongerWeight_TightensThatWell()
    {
        var items = Enumerable.Range(1, 24).Select(i => Item((ulong)i)).ToList();
        ulong[] members = Enumerable.Range(1, 8).Select(i => (ulong)i).ToArray();
        var groups = new List<ClusterLayout.Group> { new(WellKind.Folder, members) };

        var weak = new ClusterLayout(items, groups, new ForceWeights(SizeGravity: 0, Folder: 0.15));
        var strong = new ClusterLayout(items, groups, new ForceWeights(SizeGravity: 0, Folder: 1.0));
        weak.Solve();
        strong.Solve();

        double weakRadius = weak.Wells()[0].Radius;
        double strongRadius = strong.Wells()[0].Radius;
        Assert.True(strongRadius < weakRadius,
            $"stronger folder pull didn't tighten: strong {strongRadius:F1} >= weak {weakRadius:F1}");
    }

    [Fact]
    public void Wells_ReportMembershipCounts()
    {
        var items = Enumerable.Range(1, 10).Select(i => Item((ulong)i)).ToList();
        var groups = new List<ClusterLayout.Group>
        {
            new(WellKind.Duplicate, new ulong[] { 1, 2, 3 }),
            new(WellKind.Type, new ulong[] { 4, 5, 6, 7 }),
        };
        var layout = new ClusterLayout(items, groups);
        layout.Solve(50);

        var wells = layout.Wells();
        Assert.Equal(2, wells.Count);
        Assert.Contains(wells, w => w.Kind == WellKind.Duplicate && w.Count == 3);
        Assert.Contains(wells, w => w.Kind == WellKind.Type && w.Count == 4);
    }

    [Fact]
    public void GridPath_LargeSet_IsDeterministic_AndBounded()
    {
        // > AllPairsMax(512) forces the spatial-grid repulsion path.
        var items = Enumerable.Range(1, 800).Select(i => Item((ulong)i, i * 2048)).ToList();
        var groups = new List<ClusterLayout.Group>
        {
            new(WellKind.Duplicate, Enumerable.Range(1, 40).Select(i => (ulong)i).ToArray()),
            new(WellKind.Folder, Enumerable.Range(100, 60).Select(i => (ulong)i).ToArray()),
        };

        var a = new ClusterLayout(items, groups);
        var b = new ClusterLayout(items, groups);
        a.Solve(120);
        b.Solve(120);

        var pa = a.Positions();
        var pb = b.Positions();
        for (int i = 0; i < pa.Count; i++)
        {
            Assert.Equal(pa[i], pb[i]); // grid path stays deterministic
            Assert.True(float.IsFinite(pa[i].X) && float.IsFinite(pa[i].Y) && pa[i].Length() < 1e7f);
        }
    }

    [Fact]
    public void GridPath_SeparatesWells()
    {
        // Two disjoint duplicate wells embedded in a large (grid-path) set.
        var items = Enumerable.Range(1, 700).Select(i => Item((ulong)i)).ToList();
        ulong[] wellA = Enumerable.Range(1, 30).Select(i => (ulong)i).ToArray();
        ulong[] wellB = Enumerable.Range(31, 30).Select(i => (ulong)i).ToArray();
        var groups = new List<ClusterLayout.Group>
        {
            new(WellKind.Duplicate, wellA),
            new(WellKind.Duplicate, wellB),
        };
        var layout = new ClusterLayout(items, groups, new ForceWeights(SizeGravity: 0));
        layout.Solve(150);

        Vector2 P(ulong id) => layout.PositionOf(id);
        double intraA = MeanPairDistance(wellA.Select(P));
        Vector2 cA = Centroid(wellA.Select(P).ToList());
        Vector2 cB = Centroid(wellB.Select(P).ToList());
        Assert.True((cA - cB).Length() > intraA);
    }

    [Fact]
    public void UnknownGroupMembers_AreIgnored()
    {
        var items = new List<ClusterLayout.Item> { Item(1), Item(2) };
        // Group references id 99 which isn't in the item set — must not throw.
        var groups = new List<ClusterLayout.Group> { new(WellKind.Duplicate, new ulong[] { 1, 2, 99 }) };
        var layout = new ClusterLayout(items, groups);
        layout.Solve(20);
        Assert.Equal(2, layout.Wells()[0].Count);
    }
}
