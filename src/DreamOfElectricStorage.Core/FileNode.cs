namespace DreamOfElectricStorage.Core;

/// <summary>A single file or directory entry in the drive index.</summary>
/// <remarks>
/// <see cref="Id"/> and <see cref="ParentId"/> are NTFS file reference numbers (64-bit),
/// so the hierarchy is reconstructable from a flat MFT/USN enumeration without paths.
/// <see cref="SizeBytes"/> is mutable: instances are shared between the id map and
/// adjacency lists, so in-place size fills update every view at once. For directories
/// it holds the rolled-up subtree total after <see cref="VolumeIndex.ComputeDirectorySizes"/>.
/// </remarks>
public sealed record FileNode(
    ulong Id,
    ulong ParentId,
    string Name,
    long SizeBytes,
    bool IsDirectory)
{
    public long SizeBytes { get; set; } = SizeBytes;

    /// <summary>Last write time as FILETIME (100ns ticks since 1601 UTC); 0 = unknown. Mutable like SizeBytes.</summary>
    public long LastWriteFileTime { get; set; }
}
