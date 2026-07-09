namespace DreamOfElectricStorage.Core;

/// <summary>
/// Index of every indexable volume on the machine — one <see cref="VolumeIndex"/> per drive,
/// built concurrently. Volumes that can't be indexed are recorded, not fatal.
/// </summary>
public sealed class MachineIndex
{
    private MachineIndex(IReadOnlyList<VolumeIndex> volumes, IReadOnlyList<SkippedVolume> skipped)
    {
        Volumes = volumes;
        Skipped = skipped;
    }

    public IReadOnlyList<VolumeIndex> Volumes { get; }

    /// <summary>Volumes that were discovered but not indexed (non-NTFS, I/O failure…).</summary>
    public IReadOnlyList<SkippedVolume> Skipped { get; }

    public long TotalCount => Volumes.Sum(v => (long)v.Count);

    /// <param name="indexer">Per-volume indexer (NTFS MFT in production, fakes in tests).</param>
    /// <param name="volumes">
    /// Volumes to index, e.g. ["C:", "D:"]. Null → discover ready fixed/removable NTFS
    /// drives via <see cref="DriveInfo.GetDrives"/>.
    /// </param>
    public static async Task<MachineIndex> BuildAsync(
        IDriveIndexer indexer,
        IReadOnlyList<string>? volumes = null,
        CancellationToken cancellationToken = default,
        IProgress<VolumeIndexProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(indexer);

        var skipped = new List<SkippedVolume>();
        volumes ??= DiscoverVolumes(skipped);

        var builds = volumes.Select(async volume =>
        {
            try
            {
                return await VolumeIndex
                    .BuildAsync(volume, indexer.EnumerateAsync(volume, cancellationToken), cancellationToken, progress)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException)
            {
                // One unreadable volume (exFAT stick, dying disk) must not sink the rest.
                // UnauthorizedAccessException stays fatal: it means the whole process lacks elevation.
                lock (skipped)
                    skipped.Add(new SkippedVolume(volume, ex.Message));
                return null;
            }
        }).ToArray();

        VolumeIndex?[] results = await Task.WhenAll(builds).ConfigureAwait(false);
        return new MachineIndex([.. results.Where(v => v is not null)!], skipped);
    }

    /// <summary>Case-insensitive substring search across every indexed volume.</summary>
    public IEnumerable<(VolumeIndex Volume, FileNode Node)> Search(string substring) =>
        Volumes.SelectMany(volume => volume.Search(substring).Select(node => (volume, node)));

    private static List<string> DiscoverVolumes(List<SkippedVolume> skipped)
    {
        var discovered = new List<string>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;

            string volume = drive.Name.TrimEnd('\\');
            if (string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                discovered.Add(volume);
            else
                skipped.Add(new SkippedVolume(volume, $"non-NTFS volume ({drive.DriveFormat}); fallback indexer not implemented yet"));
        }
        return discovered;
    }
}

/// <summary>A discovered volume that was not indexed, with the reason.</summary>
public sealed record SkippedVolume(string Volume, string Reason);
