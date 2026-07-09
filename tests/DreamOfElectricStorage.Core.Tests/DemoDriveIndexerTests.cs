using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class DemoDriveIndexerTests
{
    [Fact]
    public async Task Demo_BuildsPopulatedMachineIndex_WithSizesAndRollups()
    {
        var machine = await MachineIndex.BuildAsync(new DemoDriveIndexer(), DemoDriveIndexer.Volumes);

        Assert.Equal(2, machine.Volumes.Count);
        Assert.True(machine.TotalCount > 150, $"expected a substantial demo tree, got {machine.TotalCount}");

        // Sizes ride the stream; rollups must be computed even without the NTFS layout pass.
        var c = machine.Volumes.Single(v => v.Volume == "C:");
        var users = c.RootEntries.Single(n => n.Name == "Users");
        Assert.True(users.SizeBytes > 0, "directory rollups should be populated in demo mode");

        // The demo tree deliberately contains a cross-volume duplicate for Related-panel testing.
        var report = c.Search("report.docx").First();
        Assert.NotEmpty(machine.FindDuplicates(c, report));
    }

    [Fact]
    public async Task Demo_IsDeterministic_AcrossEnumerations()
    {
        var indexer = new DemoDriveIndexer();
        var first = new List<FileNode>();
        var second = new List<FileNode>();
        await foreach (var n in indexer.EnumerateAsync("C:")) first.Add(n);
        await foreach (var n in indexer.EnumerateAsync("C:")) second.Add(n);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(n => (n.Id, n.Name, n.SizeBytes)), second.Select(n => (n.Id, n.Name, n.SizeBytes)));
    }
}
