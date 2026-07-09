namespace DreamOfElectricStorage.Core;

/// <summary>
/// Backend-agnostic drive indexer. The Windows implementation reads the NTFS MFT / USN journal;
/// the interface exists so other-platform backends could slot in later.
/// </summary>
public interface IDriveIndexer
{
    /// <summary>Enumerate every entry on the given volume (e.g. "C:").</summary>
    IAsyncEnumerable<FileNode> EnumerateAsync(string volume, CancellationToken cancellationToken = default);
}
