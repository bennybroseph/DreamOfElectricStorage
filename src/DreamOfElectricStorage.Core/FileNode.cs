namespace DreamOfElectricStorage.Core;

/// <summary>A single file or directory entry in the drive index.</summary>
/// <remarks>
/// <see cref="Id"/> and <see cref="ParentId"/> are NTFS file reference numbers (64-bit),
/// so the hierarchy is reconstructable from a flat MFT/USN enumeration without paths.
/// </remarks>
public sealed record FileNode(
    ulong Id,
    ulong ParentId,
    string Name,
    long SizeBytes,
    bool IsDirectory);
