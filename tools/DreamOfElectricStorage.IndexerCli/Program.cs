using System.Diagnostics;
using DreamOfElectricStorage.Core;

// Headless proof harness for the NTFS MFT indexer.
// Usage (elevated):
//   dotnet run --project tools/DreamOfElectricStorage.IndexerCli -- C:              raw enumeration of one volume
//   dotnet run --project tools/DreamOfElectricStorage.IndexerCli -- index [term]    build MachineIndex of all drives, then search
//   dotnet run --project tools/DreamOfElectricStorage.IndexerCli -- watch           index all drives, then stream live changes

return args switch
{
    ["watch"] => await RunWatchAsync(),
    ["index", .. var rest] when rest.Length <= 1 => await RunIndexAsync(rest.Length == 1 ? rest[0] : "benny"),
    [var volume] => await RunEnumerateAsync(volume),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: IndexerCli <volume> | IndexerCli index [search-term] | IndexerCli watch");
    return 2;
}

static async Task<int> RunEnumerateAsync(string volume)
{
    var indexer = new NtfsDriveIndexer();
    var stopwatch = Stopwatch.StartNew();
    long total = 0, directories = 0;

    try
    {
        await foreach (var node in indexer.EnumerateAsync(volume))
        {
            total++;
            if (node.IsDirectory)
                directories++;

            if (total <= 5)
                Console.WriteLine($"  sample: [{(node.IsDirectory ? "dir " : "file")}] frn={node.Id} parent={node.ParentId} \"{node.Name}\"");
            else if (total % 250_000 == 0)
                Console.WriteLine($"  ...{total:N0} entries ({stopwatch.Elapsed.TotalSeconds:F1}s)");
        }
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or IOException or ArgumentException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }

    stopwatch.Stop();
    Console.WriteLine();
    Console.WriteLine($"{volume} -> {total:N0} entries ({directories:N0} directories, {total - directories:N0} files)");
    Console.WriteLine($"elapsed: {stopwatch.Elapsed.TotalSeconds:F2}s ({total / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001):N0} entries/sec)");
    return 0;
}

static async Task<int> RunWatchAsync()
{
    MachineIndex machine;
    try
    {
        Console.WriteLine("building machine index...");
        machine = await MachineIndex.BuildAsync(new NtfsDriveIndexer());
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"indexed {machine.TotalCount:N0} nodes across {machine.Volumes.Count} volume(s)");
    var watchable = machine.Volumes.Where(v => v.Journal is not null).ToList();
    Console.WriteLine($"watching {string.Join(", ", watchable.Select(v => v.Volume))} — create/rename/delete files to see events; Ctrl+C to stop");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    // All Apply calls marshal to this single consumer loop via one channel (index owner = this thread).
    var channel = System.Threading.Channels.Channel.CreateUnbounded<(VolumeIndex Volume, UsnChangeBatch Batch)>();

    var pumps = watchable.Select(async volume =>
    {
        try
        {
            await foreach (var batch in machine.Watch(volume, cts.Token))
                await channel.Writer.WriteAsync((volume, batch), cts.Token);
        }
        catch (OperationCanceledException) { }
    }).ToArray();

    _ = Task.WhenAll(pumps).ContinueWith(_ => channel.Writer.TryComplete());

    try
    {
        await foreach (var (volume, batch) in channel.Reader.ReadAllAsync(cts.Token))
        {
            if (batch.RequiresRebuild)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {volume.Volume} journal discontinuity — full rebuild required (stopping)");
                continue;
            }

            volume.Apply(batch);
            foreach (var entry in batch.Entries)
            {
                string? path = volume.GetPath(entry.Node.Id) ?? $@"{volume.Volume}\...\{entry.Node.Name}";
                string tag =
                    (entry.Reason & UsnReasons.FileDelete) != 0 ? "-" :
                    (entry.Reason & UsnReasons.RenameOldName) != 0 ? "~old" :
                    (entry.Reason & UsnReasons.RenameNewName) != 0 ? "~new" : "+";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {tag,-4} {path}");
            }
        }
    }
    catch (OperationCanceledException) { }

    Console.WriteLine("stopped.");
    return 0;
}

static async Task<int> RunIndexAsync(string searchTerm)
{
    var buildWatch = Stopwatch.StartNew();
    MachineIndex machine;
    try
    {
        machine = await MachineIndex.BuildAsync(
            new NtfsDriveIndexer(),
            progress: new Progress<VolumeIndexProgress>(p =>
                Console.WriteLine(p.Completed
                    ? $"  {p.Volume} indexed: {p.NodesIndexed:N0} nodes"
                    : $"  {p.Volume} ...{p.NodesIndexed:N0}")));
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
    buildWatch.Stop();

    Console.WriteLine();
    Console.WriteLine($"machine index: {machine.TotalCount:N0} nodes across {machine.Volumes.Count} volume(s) in {buildWatch.Elapsed.TotalSeconds:F2}s");
    foreach (var skipped in machine.Skipped)
        Console.WriteLine($"  skipped {skipped.Volume}: {skipped.Reason}");

    long managedBytes = GC.GetTotalMemory(forceFullCollection: true);
    Console.WriteLine($"memory: {managedBytes / (1024.0 * 1024.0):N0} MB managed heap, {Environment.WorkingSet / (1024.0 * 1024.0):N0} MB working set");

    // Path reconstruction spot-check: a few deep-ish nodes per volume.
    Console.WriteLine();
    Console.WriteLine("path samples:");
    foreach (var volume in machine.Volumes)
    {
        foreach (var node in volume.Search(".exe").Take(2))
            Console.WriteLine($"  {volume.GetPath(node.Id)}");
    }

    Console.WriteLine();
    var searchWatch = Stopwatch.StartNew();
    var hits = machine.Search(searchTerm).ToList();
    searchWatch.Stop();
    Console.WriteLine($"search \"{searchTerm}\": {hits.Count:N0} hits in {searchWatch.Elapsed.TotalMilliseconds:F0}ms");
    foreach (var (volume, node) in hits.Take(5))
        Console.WriteLine($"  {volume.GetPath(node.Id)}");

    return 0;
}
