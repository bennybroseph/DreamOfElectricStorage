using System.Security.Principal;
using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class NtfsDriveIndexerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CD")]
    [InlineData("C:D")]
    [InlineData("7")]
    [InlineData(@"\\server\share")]
    public void EnumerateAsync_InvalidVolume_ThrowsEagerly(string volume)
    {
        var indexer = new NtfsDriveIndexer();

        // Validation happens at call time, before any enumeration starts.
        Assert.ThrowsAny<ArgumentException>(() => indexer.EnumerateAsync(volume));
    }

    [Fact]
    public async Task EnumerateAsync_SystemVolume_YieldsWellFormedNodes()
    {
        // MFT enumeration needs elevation; self-skip in normal (non-admin) test runs.
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return;

        var indexer = new NtfsDriveIndexer();
        var nodes = new List<FileNode>();

        await foreach (var node in indexer.EnumerateAsync("C:"))
        {
            nodes.Add(node);
            if (nodes.Count >= 1000)
                break; // enough to prove the pipeline; full-drive proof lives in IndexerCli
        }

        Assert.Equal(1000, nodes.Count);
        Assert.All(nodes, n => Assert.False(string.IsNullOrEmpty(n.Name)));
        Assert.Contains(nodes, n => n.IsDirectory);
        Assert.All(nodes, n => Assert.NotEqual(0ul, n.Id));
    }
}
