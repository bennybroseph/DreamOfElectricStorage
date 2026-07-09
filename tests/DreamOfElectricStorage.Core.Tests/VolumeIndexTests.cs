using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class VolumeIndexTests
{
    // Root (frn 5) is deliberately absent — MFT enumeration doesn't emit it.
    // Property, not static readonly: FileNode is partially mutable (SizeBytes/LastWriteFileTime),
    // so shared instances would leak state between tests.
    private static FileNode[] SampleTree =>
    [
        new(Id: 10, ParentId: 5, Name: "Users", SizeBytes: 0, IsDirectory: true),
        new(Id: 11, ParentId: 10, Name: "benny", SizeBytes: 0, IsDirectory: true),
        new(Id: 12, ParentId: 11, Name: "notes.txt", SizeBytes: 0, IsDirectory: false),
        new(Id: 13, ParentId: 11, Name: "Games", SizeBytes: 0, IsDirectory: true),
        new(Id: 20, ParentId: 5, Name: "Windows", SizeBytes: 0, IsDirectory: true),
    ];

    private static async Task<VolumeIndex> BuildAsync(IEnumerable<FileNode> nodes) =>
        await VolumeIndex.BuildAsync("c", ToAsync(nodes));

    private static async IAsyncEnumerable<FileNode> ToAsync(IEnumerable<FileNode> nodes)
    {
        foreach (var node in nodes)
            yield return node;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Build_NormalizesVolumeName_AndCounts()
    {
        var index = await BuildAsync(SampleTree);

        Assert.Equal("C:", index.Volume);
        Assert.Equal(5, index.Count);
    }

    [Fact]
    public async Task Children_GroupedUnderParent()
    {
        var index = await BuildAsync(SampleTree);

        var children = index.GetChildren(11);
        Assert.Equal(2, children.Count);
        Assert.Contains(children, n => n.Name == "notes.txt");
        Assert.Contains(children, n => n.Name == "Games");
        Assert.Empty(index.GetChildren(12)); // leaf
    }

    [Fact]
    public async Task NodesWithUnknownParent_AttachToSyntheticRoot()
    {
        var index = await BuildAsync(SampleTree);

        Assert.Equal(2, index.RootEntries.Count); // Users + Windows (parent frn 5 not in stream)
        Assert.Contains(index.RootEntries, n => n.Name == "Users");
    }

    [Fact]
    public async Task GetPath_WalksToVolumeRoot()
    {
        var index = await BuildAsync(SampleTree);

        Assert.Equal(@"C:\Users\benny\notes.txt", index.GetPath(12));
        Assert.Equal(@"C:\Users", index.GetPath(10));
        Assert.Null(index.GetPath(999));
    }

    [Fact]
    public async Task GetPath_SelfParentedNode_TerminatesAtRoot()
    {
        // NTFS root's parent is itself; guard must stop the walk.
        var index = await BuildAsync([new FileNode(5, 5, "self", 0, true)]);

        Assert.Equal(@"C:\self", index.GetPath(5));
        Assert.Contains(index.RootEntries, n => n.Id == 5ul);
    }

    [Fact]
    public async Task GetPath_ParentCycle_Terminates()
    {
        var index = await BuildAsync(
        [
            new FileNode(1, 2, "a", 0, true),
            new FileNode(2, 1, "b", 0, true),
        ]);

        string? path = index.GetPath(1);
        Assert.NotNull(path); // guarded walk must complete, not hang
    }

    [Fact]
    public async Task DuplicateFrn_LastWins()
    {
        var index = await BuildAsync(
        [
            new FileNode(1, 5, "old-name.txt", 0, false),
            new FileNode(1, 5, "new-name.txt", 0, false),
        ]);

        Assert.Equal(1, index.Count);
        Assert.True(index.TryGetNode(1, out var node));
        Assert.Equal("new-name.txt", node.Name);
    }

    [Fact]
    public async Task Search_IsCaseInsensitive_Substring()
    {
        var index = await BuildAsync(SampleTree);

        Assert.Equal("notes.txt", Assert.Single(index.Search("NOTES")).Name);
        Assert.Equal(2, index.Search("n").Count(n => n.IsDirectory ? n.Name == "Windows" : n.Name == "notes.txt"));
        Assert.Empty(index.Search("zzz"));
    }

    [Fact]
    public async Task EmptyStream_YieldsEmptyIndex()
    {
        var index = await BuildAsync([]);

        Assert.Equal(0, index.Count);
        Assert.Empty(index.RootEntries);
        Assert.Empty(index.Search("x"));
    }

    [Fact]
    public async Task ApplyLayoutInfo_SetsFilesByFrn_SkipsDirsAndUnknowns()
    {
        var index = await BuildAsync(SampleTree);

        long applied = index.ApplyLayoutInfo(
        [
            new FileLayoutInfo(12, 2048, LastWriteFileTime: 131_000_000_000_000_000),
            new FileLayoutInfo(10, 999, 1) /* dir */,
            new FileLayoutInfo(777, 1, 1) /* unknown */,
        ]);

        Assert.Equal(1, applied);
        Assert.True(index.TryGetNode(12, out var note));
        Assert.Equal(2048, note.SizeBytes);
        Assert.Equal(131_000_000_000_000_000, note.LastWriteFileTime);
        Assert.True(index.TryGetNode(10, out var dir));
        Assert.Equal(0, dir.SizeBytes);
    }

    [Fact]
    public async Task ComputeDirectorySizes_RollsUpSubtrees()
    {
        // C:\Users\benny\notes.txt (2048) + C:\Users\benny\Games\save.dat (100)
        var tree = SampleTree.Append(new FileNode(14, 13, "save.dat", 0, false)).ToList();
        var index = await BuildAsync(tree);
        index.ApplyLayoutInfo([new FileLayoutInfo(12, 2048, 0), new FileLayoutInfo(14, 100, 0)]);

        index.ComputeDirectorySizes();

        Assert.True(index.TryGetNode(13, out var games));
        Assert.Equal(100, games.SizeBytes);
        Assert.True(index.TryGetNode(11, out var benny));
        Assert.Equal(2148, benny.SizeBytes);
        Assert.True(index.TryGetNode(10, out var users));
        Assert.Equal(2148, users.SizeBytes);
        Assert.True(index.TryGetNode(20, out var windows));
        Assert.Equal(0, windows.SizeBytes);
    }

    [Fact]
    public async Task Progress_ReportsCompletion()
    {
        var reports = new List<VolumeIndexProgress>();
        var progress = new SynchronousProgress(reports.Add);

        await VolumeIndex.BuildAsync("d", ToAsync(SampleTree), progress: progress);

        var final = Assert.Single(reports, r => r.Completed);
        Assert.Equal("D:", final.Volume);
        Assert.Equal(5, final.NodesIndexed);
    }

    /// <summary>Invokes the callback inline (Progress&lt;T&gt; posts to a sync context, racing tests).</summary>
    private sealed class SynchronousProgress(Action<VolumeIndexProgress> callback) : IProgress<VolumeIndexProgress>
    {
        public void Report(VolumeIndexProgress value) => callback(value);
    }
}
