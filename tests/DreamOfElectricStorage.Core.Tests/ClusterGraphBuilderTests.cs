using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class ClusterGraphBuilderTests
{
    private static async IAsyncEnumerable<FileNode> ToAsync(IEnumerable<FileNode> nodes)
    {
        foreach (var node in nodes)
            yield return node;
        await Task.CompletedTask;
    }

    private static async Task<VolumeIndex> Volume(string letter, params FileNode[] nodes) =>
        await VolumeIndex.BuildAsync(letter, ToAsync(nodes));

    private static FileNode File(ulong id, ulong parent, string name, long size) =>
        new(id, parent, name, size, IsDirectory: false);

    private static string? Key(ClusterGraph g, int node, WellKind facet) => g.FacetKeys[node][(int)facet];

    [Fact]
    public async Task Build_SelectsLargestFiles_UpToCap()
    {
        var v = await Volume("c",
            File(10, 5, "a.bin", 100),
            File(11, 5, "b.bin", 300),
            File(12, 5, "c.bin", 200),
            File(13, 5, "d.bin", 50));

        var graph = ClusterGraphBuilder.Build([v], maxNodes: 2);

        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(4, graph.TotalEligible);
        // Biggest first: b (300), c (200).
        Assert.Equal("b.bin", graph.Nodes[0].Name);
        Assert.Equal("c.bin", graph.Nodes[1].Name);
    }

    [Fact]
    public async Task Build_ZeroSizeFiles_AreExcluded()
    {
        var v = await Volume("c",
            File(10, 5, "real.bin", 500),
            File(11, 5, "empty.txt", 0));

        var graph = ClusterGraphBuilder.Build([v], maxNodes: 100);

        Assert.Single(graph.Nodes);
        Assert.Equal("real.bin", graph.Nodes[0].Name);
        Assert.Equal(1, graph.TotalEligible);
    }

    [Fact]
    public async Task Build_DuplicateKey_MatchesForSameNameAndSize()
    {
        var c = await Volume("c", File(10, 5, "report.docx", 4096));
        var d = await Volume("d", File(10, 5, "report.docx", 4096)); // same name+size, other drive

        var graph = ClusterGraphBuilder.Build([c, d], maxNodes: 100);

        Assert.Equal(2, graph.Nodes.Count);
        // Same duplicate key → they nest together under a Duplicate grouping.
        Assert.Equal(Key(graph, 0, WellKind.Duplicate), Key(graph, 1, WellKind.Duplicate));
        // Home-drive differs → folder keys are per-volume and don't collide.
        Assert.NotEqual(Key(graph, 0, WellKind.Folder), Key(graph, 1, WellKind.Folder));
    }

    [Fact]
    public async Task Build_SimilarNameKey_MatchesAcrossVersionedNames()
    {
        var v = await Volume("c",
            File(10, 5, "snapshot-001.zip", 1000),
            File(11, 5, "snapshot-002.zip", 1100),
            File(12, 5, "snapshot-003.zip", 1200));

        var graph = ClusterGraphBuilder.Build([v], maxNodes: 100);

        string? k = Key(graph, 0, WellKind.SimilarName);
        Assert.NotNull(k);
        Assert.Equal(k, Key(graph, 1, WellKind.SimilarName));
        Assert.Equal(k, Key(graph, 2, WellKind.SimilarName));
    }

    [Fact]
    public async Task Build_TypeKey_MatchesForSameCategory()
    {
        var v = await Volume("c",
            File(10, 5, "one.png", 100),
            File(11, 5, "two.png", 200),
            File(12, 5, "three.jpg", 300));

        var graph = ClusterGraphBuilder.Build([v], maxNodes: 100);

        // All three are images → one shared Type key.
        Assert.Equal(Key(graph, 0, WellKind.Type), Key(graph, 1, WellKind.Type));
        Assert.Equal(Key(graph, 0, WellKind.Type), Key(graph, 2, WellKind.Type));
    }

    [Fact]
    public async Task Build_GlobalIds_AreSequential_AndParallelToKeys()
    {
        var v = await Volume("c",
            File(10, 5, "a.bin", 300),
            File(11, 5, "b.bin", 200),
            File(12, 5, "c.bin", 100));

        var graph = ClusterGraphBuilder.Build([v], maxNodes: 100);

        Assert.Equal(graph.Nodes.Count, graph.Items.Count);
        Assert.Equal(graph.Nodes.Count, graph.FacetKeys.Count);
        for (int i = 0; i < graph.Items.Count; i++)
        {
            Assert.Equal((ulong)i, graph.Items[i].Id);
            Assert.Equal(graph.Nodes[i].SizeBytes, graph.Items[i].SizeBytes);
            // Every file has folder/dup/type keys; date may be null (no timestamp in the fixture).
            Assert.NotNull(Key(graph, i, WellKind.Folder));
            Assert.NotNull(Key(graph, i, WellKind.Duplicate));
            Assert.NotNull(Key(graph, i, WellKind.Type));
        }
    }

    [Fact]
    public async Task Build_EmptyVolumes_ProduceEmptyGraph()
    {
        var v = await Volume("c", File(10, 5, "empty.txt", 0));
        var graph = ClusterGraphBuilder.Build([v], maxNodes: 100);
        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.FacetKeys);
    }
}
