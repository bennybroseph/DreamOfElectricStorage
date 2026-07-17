using System.Numerics;
using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class ClusterLayoutTests
{
    private static ClusterLayout.Item Item(ulong id, long size = 1024) => new(id, size);

    /// <summary>Build a facet key row; unspecified facets are null.</summary>
    private static string?[] Row(params (WellKind Facet, string Value)[] pairs)
    {
        var k = new string?[5];
        foreach (var (f, v) in pairs)
            k[(int)f] = v;
        return k;
    }

    private static FacetOrder Order(params WellKind[] facets) => new(facets);

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
        var keys = new List<string?[]>();
        for (int i = 0; i < 30; i++)
            keys.Add(i < 5 ? Row((WellKind.Duplicate, "dup"))
                : i is >= 9 and < 12 ? Row((WellKind.SimilarName, "name"))
                : Row());
        var order = Order(WellKind.Duplicate, WellKind.SimilarName);

        var a = new ClusterLayout(items, keys, order);
        var b = new ClusterLayout(items, keys, order);
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
        var keys = new List<string?[]>();
        for (int i = 0; i < 60; i++)
            keys.Add(i < 10 ? Row((WellKind.Duplicate, "d"))
                : i is >= 19 and < 34 ? Row((WellKind.Folder, "f"))
                : Row());
        var layout = new ClusterLayout(items, keys, Order(WellKind.Duplicate, WellKind.Folder));
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
        // Two disjoint duplicate wells; the deterministic pack places them apart and physics holds it.
        var items = Enumerable.Range(1, 20).Select(i => Item((ulong)i)).ToList();
        var keys = new List<string?[]>();
        for (int i = 0; i < 20; i++)
            keys.Add(Row((WellKind.Duplicate, i < 10 ? "A" : "B")));
        var layout = new ClusterLayout(items, keys, Order(WellKind.Duplicate));
        layout.PackDeterministic();
        for (int i = 0; i < 40; i++)
            layout.Step();

        Vector2 P(ulong id) => layout.PositionOf(id);
        var wellA = Enumerable.Range(1, 10).Select(i => (ulong)i).ToArray();
        var wellB = Enumerable.Range(11, 10).Select(i => (ulong)i).ToArray();
        double intraA = MeanPairDistance(wellA.Select(P));
        double intraB = MeanPairDistance(wellB.Select(P));
        Vector2 cA = Centroid(wellA.Select(P).ToList());
        Vector2 cB = Centroid(wellB.Select(P).ToList());
        double inter = (cA - cB).Length();

        Assert.True(inter > intraA && inter > intraB,
            $"wells not separated: inter {inter:F1} vs intraA {intraA:F1}, intraB {intraB:F1}");
    }

    [Fact]
    public void Pack_PlacesHeavierNearerCenter()
    {
        // Heavier-central is now the pack's job (biggest-first placement), not a live gravity force.
        var items = Enumerable.Range(1, 40).Select(i => Item((ulong)i, (long)i * i * 100_000L)).ToList();
        var keys = Enumerable.Range(0, 40).Select(_ => Row()).ToList();
        var layout = new ClusterLayout(items, keys, Order());
        layout.PackDeterministic();

        var positions = layout.Positions();
        Vector2 center = Centroid(positions);
        double lightMean = Enumerable.Range(1, 10)
            .Average(i => (layout.PositionOf((ulong)i) - center).Length());
        double heavyMean = Enumerable.Range(31, 10)
            .Average(i => (layout.PositionOf((ulong)i) - center).Length());

        Assert.True(heavyMean < lightMean,
            $"heavy nodes not central: heavyMean {heavyMean:F1} >= lightMean {lightMean:F1}");
    }

    [Fact]
    public void Wells_ReflectLeafBuckets()
    {
        var items = Enumerable.Range(1, 7).Select(i => Item((ulong)i)).ToList();
        var keys = new List<string?[]>();
        for (int i = 0; i < 7; i++)
            keys.Add(Row((WellKind.Duplicate, i < 3 ? "d1" : "d2")));
        var layout = new ClusterLayout(items, keys, Order(WellKind.Duplicate));

        var wells = layout.Wells();
        Assert.Equal(2, wells.Count);
        Assert.All(wells, w => Assert.Equal(WellKind.Duplicate, w.Kind));
        Assert.Contains(wells, w => w.Count == 3);
        Assert.Contains(wells, w => w.Count == 4);
    }

    [Fact]
    public void OrderSetter_RestructuresWells()
    {
        var items = Enumerable.Range(1, 4).Select(i => Item((ulong)i)).ToList();
        var keys = new List<string?[]>
        {
            Row((WellKind.Type, "img"), (WellKind.Folder, "a")),
            Row((WellKind.Type, "img"), (WellKind.Folder, "b")),
            Row((WellKind.Type, "doc"), (WellKind.Folder, "a")),
            Row((WellKind.Type, "doc"), (WellKind.Folder, "b")),
        };
        var layout = new ClusterLayout(items, keys, Order(WellKind.Type));
        Assert.All(layout.Wells(), w => Assert.Equal(WellKind.Type, w.Kind));

        layout.Order = Order(WellKind.Folder);
        Assert.All(layout.Wells(), w => Assert.Equal(WellKind.Folder, w.Kind));
    }

    [Fact]
    public void GridPath_LargeSet_IsDeterministic_AndBounded()
    {
        // > AllPairsMax(512) forces the spatial-grid repulsion path.
        var items = Enumerable.Range(1, 800).Select(i => Item((ulong)i, i * 2048)).ToList();
        var keys = new List<string?[]>();
        for (int i = 0; i < 800; i++)
            keys.Add(i < 40 ? Row((WellKind.Duplicate, "d"))
                : i is >= 100 and < 160 ? Row((WellKind.Folder, "f"))
                : Row());

        var a = new ClusterLayout(items, keys, Order(WellKind.Duplicate, WellKind.Folder));
        var b = new ClusterLayout(items, keys, Order(WellKind.Duplicate, WellKind.Folder));
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
        // Two disjoint duplicate wells embedded in a large (grid-path) set. Matches the app:
        // deterministic pack, then physics ticks (cohesion holds each well without merging).
        var items = Enumerable.Range(1, 700).Select(i => Item((ulong)i)).ToList();
        var keys = new List<string?[]>();
        for (int i = 0; i < 700; i++)
            keys.Add(i < 30 ? Row((WellKind.Duplicate, "A"))
                : i is >= 30 and < 60 ? Row((WellKind.Duplicate, "B"))
                : Row());
        var layout = new ClusterLayout(items, keys, Order(WellKind.Duplicate));
        layout.PackDeterministic();
        for (int i = 0; i < 40; i++)
            layout.Step();

        Vector2 P(ulong id) => layout.PositionOf(id);
        var wellA = Enumerable.Range(1, 30).Select(i => (ulong)i).ToArray();
        double intraA = MeanPairDistance(wellA.Select(P));
        Vector2 cA = Centroid(wellA.Select(P).ToList());
        Vector2 cB = Centroid(Enumerable.Range(31, 30).Select(i => P((ulong)i)).ToList());
        Assert.True((cA - cB).Length() > intraA);
    }

    [Fact]
    public void NullKeyMembers_AreLoose_NoWell()
    {
        // Node 3 has no keys at all → loose, forms no well. Only the shared pair is a well.
        var items = new List<ClusterLayout.Item> { Item(1), Item(2), Item(3) };
        var keys = new List<string?[]>
        {
            Row((WellKind.Duplicate, "d")),
            Row((WellKind.Duplicate, "d")),
            Row(),
        };
        var layout = new ClusterLayout(items, keys, Order(WellKind.Duplicate));
        var wells = layout.Wells();
        Assert.Single(wells);
        Assert.Equal(2, wells[0].Count);
    }
}
