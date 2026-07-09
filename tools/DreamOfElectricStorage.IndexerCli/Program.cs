using System.Diagnostics;
using DreamOfElectricStorage.Core;

// Headless proof harness for the NTFS MFT indexer.
// Usage (elevated): dotnet run --project tools/DreamOfElectricStorage.IndexerCli -- C:

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: IndexerCli <volume>   e.g. IndexerCli C:");
    return 2;
}

var indexer = new NtfsDriveIndexer();
var stopwatch = Stopwatch.StartNew();
long total = 0, directories = 0;

try
{
    await foreach (var node in indexer.EnumerateAsync(args[0]))
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
Console.WriteLine($"{args[0]} -> {total:N0} entries ({directories:N0} directories, {total - directories:N0} files)");
Console.WriteLine($"elapsed: {stopwatch.Elapsed.TotalSeconds:F2}s ({total / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001):N0} entries/sec)");
return 0;
