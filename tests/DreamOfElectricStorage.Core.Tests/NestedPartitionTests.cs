using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class NestedPartitionTests
{
    /// <summary>Build a facet key row; unspecified facets are null.</summary>
    private static string?[] Row(params (WellKind Facet, string Value)[] pairs)
    {
        var k = new string?[5];
        foreach (var (f, v) in pairs)
            k[(int)f] = v;
        return k;
    }

    private static PartitionBucket Build(IReadOnlyList<string?[]> keys, params WellKind[] order) =>
        NestedPartition.Build(keys, new FacetOrder(order), keys.Count);

    [Fact]
    public void SingletonKey_CollapsesToParentLoose_NoBucket()
    {
        // Two files share a duplicate key; one is unique → only the shared pair forms a bucket,
        // the unique file falls to the root's loose members (no size-1 well).
        var keys = new List<string?[]>
        {
            Row((WellKind.Duplicate, "a")),
            Row((WellKind.Duplicate, "a")),
            Row((WellKind.Duplicate, "solo")),
        };
        var tree = Build(keys, WellKind.Duplicate);

        Assert.Single(tree.Children);
        Assert.Equal(new[] { 0, 1 }, tree.Children[0].AllMembers);
        Assert.Contains(2, tree.LooseMembers); // singleton collapsed here
    }

    [Fact]
    public void NullKey_GoesLooseAtThatLevel_AndDoesNotDescend()
    {
        // Node 2 has no Type key → loose at the root; it must NOT be grouped with 0/1 by Folder
        // even though all three share a folder (deeper facets only exist inside a Type bucket).
        var keys = new List<string?[]>
        {
            Row((WellKind.Type, "img"), (WellKind.Folder, "f")),
            Row((WellKind.Type, "img"), (WellKind.Folder, "f")),
            Row((WellKind.Folder, "f")),
        };
        var tree = Build(keys, WellKind.Type, WellKind.Folder);

        Assert.Contains(2, tree.LooseMembers);
        Assert.Single(tree.Children);                            // the "img" type bucket
        Assert.Equal(new[] { 0, 1 }, tree.Children[0].AllMembers); // node 2 never joined them
    }

    [Fact]
    public void OrderDefinesNesting_TypeThenFolder()
    {
        // Two types; within one type, two folders. Order [Type, Folder] → type buckets contain
        // folder sub-buckets.
        var keys = new List<string?[]>
        {
            Row((WellKind.Type, "img"), (WellKind.Folder, "a")),
            Row((WellKind.Type, "img"), (WellKind.Folder, "a")),
            Row((WellKind.Type, "img"), (WellKind.Folder, "b")),
            Row((WellKind.Type, "img"), (WellKind.Folder, "b")),
            Row((WellKind.Type, "doc"), (WellKind.Folder, "a")),
            Row((WellKind.Type, "doc"), (WellKind.Folder, "a")),
        };
        var tree = Build(keys, WellKind.Type, WellKind.Folder);

        Assert.Equal(WellKind.Type, tree.SplitFacet);
        Assert.Equal(2, tree.Children.Count);                       // img, doc
        var img = tree.Children.First(c => c.AllMembers.Count == 4);
        Assert.Equal(WellKind.Type, img.BindingFacet);
        Assert.Equal(WellKind.Folder, img.SplitFacet);
        Assert.Equal(2, img.Children.Count);                        // folder a, folder b within img
        Assert.All(img.Children, c => Assert.Equal(WellKind.Folder, c.BindingFacet));
    }

    [Fact]
    public void Reorder_ProducesDifferentTree()
    {
        var keys = new List<string?[]>
        {
            Row((WellKind.Type, "img"), (WellKind.Folder, "a")),
            Row((WellKind.Type, "doc"), (WellKind.Folder, "a")),
            Row((WellKind.Type, "img"), (WellKind.Folder, "b")),
            Row((WellKind.Type, "doc"), (WellKind.Folder, "b")),
        };
        var byType = Build(keys, WellKind.Type, WellKind.Folder);
        var byFolder = Build(keys, WellKind.Folder, WellKind.Type);

        Assert.Equal(WellKind.Type, byType.SplitFacet);
        Assert.Equal(WellKind.Folder, byFolder.SplitFacet);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var keys = new List<string?[]>();
        for (int i = 0; i < 50; i++)
            keys.Add(Row((WellKind.Type, "t" + (i % 4)), (WellKind.Folder, "f" + (i % 7))));

        string a = Serialize(Build(keys, WellKind.Type, WellKind.Folder));
        string b = Serialize(Build(keys, WellKind.Type, WellKind.Folder));
        Assert.Equal(a, b);
    }

    [Fact]
    public void EmptyOrder_AllLooseAtRoot()
    {
        var keys = new List<string?[]> { Row((WellKind.Type, "a")), Row((WellKind.Type, "b")) };
        var tree = Build(keys); // no facets enabled

        Assert.Empty(tree.Children);
        Assert.Equal(new[] { 0, 1 }, tree.LooseMembers);
    }

    private static string Serialize(PartitionBucket b)
    {
        string loose = string.Join(",", b.LooseMembers);
        string children = string.Join("|", b.Children.Select(Serialize));
        return $"[{b.BindingFacet}:{b.Key}:L({loose}):C({children})]";
    }
}
