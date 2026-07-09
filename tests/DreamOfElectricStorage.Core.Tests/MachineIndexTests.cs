using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class MachineIndexTests
{
    private sealed class FakeDriveIndexer : IDriveIndexer
    {
        private readonly Dictionary<string, FileNode[]> _volumes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unsupported = new(StringComparer.OrdinalIgnoreCase);

        public FakeDriveIndexer Add(string volume, params FileNode[] nodes)
        {
            _volumes[volume] = nodes;
            return this;
        }

        public FakeDriveIndexer AddUnsupported(string volume)
        {
            _unsupported.Add(volume);
            return this;
        }

        public async IAsyncEnumerable<FileNode> EnumerateAsync(
            string volume, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_unsupported.Contains(volume))
                throw new NotSupportedException($"{volume} is not NTFS.");

            foreach (var node in _volumes[volume])
                yield return node;
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Build_IndexesEveryVolume_AndAggregates()
    {
        var indexer = new FakeDriveIndexer()
            .Add("C:", new FileNode(1, 5, "Windows", 0, true), new FileNode(2, 1, "notepad.exe", 0, false))
            .Add("D:", new FileNode(1, 5, "Games", 0, true));

        var machine = await MachineIndex.BuildAsync(indexer, ["C:", "D:"]);

        Assert.Equal(2, machine.Volumes.Count);
        Assert.Equal(3, machine.TotalCount);
        Assert.Empty(machine.Skipped);
    }

    [Fact]
    public async Task Build_SameFrnOnDifferentVolumes_StaysSeparate()
    {
        // FRNs are per-volume (observed identical FRNs on real C: and D:).
        var indexer = new FakeDriveIndexer()
            .Add("C:", new FileNode(42, 5, "on-c.txt", 0, false))
            .Add("D:", new FileNode(42, 5, "on-d.txt", 0, false));

        var machine = await MachineIndex.BuildAsync(indexer, ["C:", "D:"]);

        var c = machine.Volumes.Single(v => v.Volume == "C:");
        var d = machine.Volumes.Single(v => v.Volume == "D:");
        Assert.Equal(@"C:\on-c.txt", c.GetPath(42));
        Assert.Equal(@"D:\on-d.txt", d.GetPath(42));
    }

    [Fact]
    public async Task Build_UnsupportedVolume_IsSkippedNotFatal()
    {
        var indexer = new FakeDriveIndexer()
            .Add("C:", new FileNode(1, 5, "file.txt", 0, false))
            .AddUnsupported("E:");

        var machine = await MachineIndex.BuildAsync(indexer, ["C:", "E:"]);

        Assert.Single(machine.Volumes);
        var skipped = Assert.Single(machine.Skipped);
        Assert.Equal("E:", skipped.Volume);
        Assert.Contains("not NTFS", skipped.Reason);
    }

    [Fact]
    public async Task Search_SpansVolumes()
    {
        var indexer = new FakeDriveIndexer()
            .Add("C:", new FileNode(1, 5, "save.dat", 0, false))
            .Add("D:", new FileNode(9, 5, "SAVE-backup.dat", 0, false));

        var machine = await MachineIndex.BuildAsync(indexer, ["C:", "D:"]);
        var hits = machine.Search("save").ToList();

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Volume.Volume == "C:" && h.Node.Name == "save.dat");
        Assert.Contains(hits, h => h.Volume.Volume == "D:" && h.Node.Name == "SAVE-backup.dat");
    }
}
