using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

/// <summary>Real-filesystem tests in an isolated temp directory (no elevation needed).</summary>
public sealed class FileOperationsTests : IDisposable
{
    private readonly string _root;

    public FileOperationsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"deos-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string CreateFile(string relative, string content = "x")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Rename_File_MovesInPlace()
    {
        string source = CreateFile("a.txt", "hello");

        string renamed = FileOperations.Rename(source, "b.txt");

        Assert.Equal(Path.Combine(_root, "b.txt"), renamed);
        Assert.False(File.Exists(source));
        Assert.Equal("hello", File.ReadAllText(renamed));
    }

    [Fact]
    public void Rename_Directory_Works()
    {
        CreateFile(@"dir\inner.txt");
        string dir = Path.Combine(_root, "dir");

        string renamed = FileOperations.Rename(dir, "renamed-dir");

        Assert.True(File.Exists(Path.Combine(renamed, "inner.txt")));
    }

    [Fact]
    public void Rename_InvalidCharacters_Throws()
    {
        string source = CreateFile("a.txt");
        Assert.Throws<ArgumentException>(() => FileOperations.Rename(source, "bad|name.txt"));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Rename_Collision_Throws()
    {
        string source = CreateFile("a.txt");
        CreateFile("b.txt");
        Assert.Throws<IOException>(() => FileOperations.Rename(source, "b.txt"));
    }

    [Fact]
    public void Move_File_IntoSubdirectory()
    {
        string source = CreateFile("a.txt", "payload");
        string dest = Path.Combine(_root, "sub");
        Directory.CreateDirectory(dest);

        string moved = FileOperations.Move(source, dest);

        Assert.Equal(Path.Combine(dest, "a.txt"), moved);
        Assert.False(File.Exists(source));
        Assert.Equal("payload", File.ReadAllText(moved));
    }

    [Fact]
    public void Move_Directory_SameVolume()
    {
        CreateFile(@"dir\inner.txt");
        string dest = Path.Combine(_root, "target");
        Directory.CreateDirectory(dest);

        string moved = FileOperations.Move(Path.Combine(_root, "dir"), dest);

        Assert.True(File.Exists(Path.Combine(moved, "inner.txt")));
    }

    [Fact]
    public void Move_Collision_Throws()
    {
        string source = CreateFile("a.txt");
        string dest = Path.Combine(_root, "sub");
        Directory.CreateDirectory(dest);
        CreateFile(@"sub\a.txt");

        Assert.Throws<IOException>(() => FileOperations.Move(source, dest));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Move_MissingDestination_Throws()
    {
        string source = CreateFile("a.txt");
        Assert.Throws<DirectoryNotFoundException>(() => FileOperations.Move(source, Path.Combine(_root, "nope")));
    }

    [Fact]
    public void Move_Directory_CrossVolume_Throws()
    {
        // Only meaningful when another fixed volume exists — self-skip otherwise.
        string? otherRoot = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => d.Name)
            .FirstOrDefault(root => !string.Equals(root, Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase));
        if (otherRoot is null)
            return;

        CreateFile(@"dir\inner.txt");

        Assert.Throws<NotSupportedException>(() => FileOperations.Move(Path.Combine(_root, "dir"), otherRoot));
    }

    [Fact]
    public void DeleteToRecycleBin_File_RemovesFromSource()
    {
        string source = CreateFile("deleted.txt");

        FileOperations.DeleteToRecycleBin(source);

        // Bin-content assertions are fragile; "gone from origin, no exception" is the contract.
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void DeleteToRecycleBin_Directory_RemovesFromSource()
    {
        CreateFile(@"dir\inner.txt");
        string dir = Path.Combine(_root, "dir");

        FileOperations.DeleteToRecycleBin(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteToRecycleBin_RelativePath_Throws()
    {
        Assert.Throws<ArgumentException>(() => FileOperations.DeleteToRecycleBin("relative.txt"));
    }

    [Fact]
    public void DeleteToRecycleBin_Missing_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => FileOperations.DeleteToRecycleBin(Path.Combine(_root, "ghost.txt")));
    }
}
