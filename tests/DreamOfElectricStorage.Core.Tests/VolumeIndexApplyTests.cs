using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class VolumeIndexApplyTests
{
    private static async Task<VolumeIndex> BuildAsync(IEnumerable<FileNode> nodes, JournalState? journal = null)
    {
        return await VolumeIndex.BuildAsync("C", ToAsync(nodes), journal: journal);

        static async IAsyncEnumerable<FileNode> ToAsync(IEnumerable<FileNode> source)
        {
            foreach (var node in source)
                yield return node;
            await Task.CompletedTask;
        }
    }

    private static UsnChangeBatch Batch(params UsnJournalEntry[] entries) => new(entries, NextUsn: 100, RequiresRebuild: false);

    // Properties, not static readonly — FileNode is partially mutable (see VolumeIndexTests.SampleTree).
    private static FileNode Docs => new(10, 5, "Docs", 0, IsDirectory: true);
    private static FileNode Note => new(11, 10, "note.txt", 0, IsDirectory: false);

    [Fact]
    public async Task Create_AddsNodeUnderParent()
    {
        var index = await BuildAsync([Docs]);

        index.Apply(Batch(new UsnJournalEntry(new FileNode(20, 10, "new.txt", 0, false), UsnReasons.FileCreate)));

        Assert.True(index.TryGetNode(20, out var node));
        Assert.Equal(@"C:\Docs\new.txt", index.GetPath(20));
        Assert.Contains(index.GetChildren(10), n => n.Name == "new.txt");
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public async Task Delete_RemovesNodeAndAdjacency()
    {
        var index = await BuildAsync([Docs, Note]);

        index.Apply(Batch(new UsnJournalEntry(Note, UsnReasons.FileDelete)));

        Assert.False(index.TryGetNode(11, out _));
        Assert.Empty(index.GetChildren(10));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public async Task Delete_OfDirectoryThenChildren_DrainsCleanly()
    {
        // NTFS deletes children before the dir, but journal batching can surface
        // records in either grouping — both nodes must be gone at the end.
        var index = await BuildAsync([Docs, Note]);

        index.Apply(Batch(
            new UsnJournalEntry(Docs, UsnReasons.FileDelete),
            new UsnJournalEntry(Note, UsnReasons.FileDelete)));

        Assert.False(index.TryGetNode(10, out _));
        Assert.False(index.TryGetNode(11, out _));
        Assert.Equal(0, index.Count);
        Assert.Empty(index.GetChildren(10));
    }

    [Fact]
    public async Task RenamePair_RenamesInPlace()
    {
        var index = await BuildAsync([Docs, Note]);

        index.Apply(Batch(
            new UsnJournalEntry(Note, UsnReasons.RenameOldName),
            new UsnJournalEntry(new FileNode(11, 10, "renamed.txt", 0, false), UsnReasons.RenameNewName)));

        Assert.True(index.TryGetNode(11, out var node));
        Assert.Equal("renamed.txt", node.Name);
        Assert.Equal("renamed.txt", Assert.Single(index.GetChildren(10)).Name);
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public async Task MovePair_Reparents()
    {
        var other = new FileNode(30, 5, "Other", 0, IsDirectory: true);
        var index = await BuildAsync([Docs, other, Note]);

        index.Apply(Batch(
            new UsnJournalEntry(Note, UsnReasons.RenameOldName),
            new UsnJournalEntry(new FileNode(11, 30, "note.txt", 0, false), UsnReasons.RenameNewName)));

        Assert.Empty(index.GetChildren(10));
        Assert.Contains(index.GetChildren(30), n => n.Id == 11ul);
        Assert.Equal(@"C:\Other\note.txt", index.GetPath(11));
    }

    [Fact]
    public async Task CreatedThenDeleted_CumulativeReason_RemoveWins()
    {
        var index = await BuildAsync([Docs]);

        // Temp-file lifecycle: the final record carries CREATE|DELETE together.
        index.Apply(Batch(new UsnJournalEntry(
            new FileNode(40, 10, "temp.tmp", 0, false), UsnReasons.FileCreate | UsnReasons.FileDelete)));

        Assert.False(index.TryGetNode(40, out _));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public async Task DeleteUnknownFrn_IsNoOp()
    {
        var index = await BuildAsync([Docs]);

        index.Apply(Batch(new UsnJournalEntry(new FileNode(999, 10, "ghost", 0, false), UsnReasons.FileDelete)));

        Assert.Equal(1, index.Count);
    }

    [Fact]
    public async Task DirectoryRename_KeepsExistingChildrenAttached()
    {
        var index = await BuildAsync([Docs, Note]);

        index.Apply(Batch(
            new UsnJournalEntry(Docs, UsnReasons.RenameOldName),
            new UsnJournalEntry(new FileNode(10, 5, "Documents", 0, true), UsnReasons.RenameNewName)));

        Assert.Equal(@"C:\Documents\note.txt", index.GetPath(11));
        Assert.Contains(index.GetChildren(10), n => n.Id == 11ul);
    }

    [Fact]
    public async Task Apply_AdvancesNextUsn_WhenJournalStatePresent()
    {
        var index = await BuildAsync([Docs], new JournalState(JournalId: 7, NextUsn: 50));

        index.Apply(Batch(new UsnJournalEntry(Note, UsnReasons.FileCreate)));

        Assert.Equal(100, index.Journal!.Value.NextUsn);
        Assert.Equal(7ul, index.Journal.Value.JournalId);
    }
}
