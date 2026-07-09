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
                // Journal identity is captured BEFORE enumerating: changes that land during
                // the build get replayed by the watcher (Apply is idempotent enough — upserts
                // and removes converge), so nothing falls in the gap.
                JournalState? journal = TryQueryJournal(volume);

                return await VolumeIndex
                    .BuildAsync(volume, indexer.EnumerateAsync(volume, cancellationToken), cancellationToken, progress, journal)
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

    /// <summary>
    /// Streams journal change batches for one indexed volume, starting from the state
    /// captured at build time. Consumer applies each batch via <see cref="VolumeIndex.Apply"/>
    /// on the thread that owns the index. A RequiresRebuild batch ends the stream —
    /// rebuild the machine index and watch again.
    /// </summary>
    public IAsyncEnumerable<UsnChangeBatch> Watch(VolumeIndex volume, CancellationToken cancellationToken = default)
    {
        if (volume.Journal is not { } journal)
            throw new InvalidOperationException($"{volume.Volume} was built without journal state; cannot watch.");

        return new UsnJournalWatcher().WatchAsync(volume.Volume, journal, cancellationToken);
    }

    private static JournalState? TryQueryJournal(string volume)
    {
        try
        {
            return UsnJournalWatcher.QueryJournal(volume);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Index still builds; the volume just won't be watchable. Elevation problems
            // surface fatally from enumeration itself (real indexer), not from here —
            // this also keeps fake-indexer test volumes from touching real drives fatally.
            return null;
        }
    }

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
