using DreamOfElectricStorage.Core;
using Xunit;

namespace DreamOfElectricStorage.Core.Tests;

public class FileNodeTests
{
    [Fact]
    public void Node_ExposesConstructorValues()
    {
        var node = new FileNode(Id: 42, ParentId: 5, Name: "report.pdf", SizeBytes: 1024, IsDirectory: false);

        Assert.Equal(42ul, node.Id);
        Assert.Equal("report.pdf", node.Name);
        Assert.Equal(1024, node.SizeBytes);
        Assert.False(node.IsDirectory);
    }

    [Fact]
    public void Records_WithSameValues_AreEqual()
    {
        var a = new FileNode(1, 0, "root", 0, IsDirectory: true);
        var b = new FileNode(1, 0, "root", 0, IsDirectory: true);

        Assert.Equal(a, b);
    }
}
