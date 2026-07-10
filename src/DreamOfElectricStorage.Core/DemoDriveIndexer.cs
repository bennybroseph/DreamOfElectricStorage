namespace DreamOfElectricStorage.Core;

/// <summary>
/// Deterministic synthetic volumes for UI verification without elevation ("--demo").
/// Same tree every run → screenshots and input scripts are reproducible.
/// Sizes ride the stream and timestamps are set on the nodes directly, so the graph
/// gets size-scaled nodes, age colors, duplicates, and similar-name groups to show off.
/// </summary>
public sealed class DemoDriveIndexer(int stressFiles = 0) : IDriveIndexer
{
    public static readonly IReadOnlyList<string> Volumes = ["C:", "D:"];

    /// <summary>Fixed "now" anchor so age buckets are stable within a session.</summary>
    private static readonly DateTime Now = DateTime.UtcNow;

    public async IAsyncEnumerable<FileNode> EnumerateAsync(
        string volume, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (FileNode node in BuildVolume(volume))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return node;
        }
        await Task.CompletedTask;
    }

    private List<FileNode> BuildVolume(string volume) => volume.StartsWith('C') ? BuildC() : BuildD(stressFiles);

    // FRN scheme: directories get low ids per volume, files count up from 1000. Parent 5 = root.
    private static List<FileNode> BuildC()
    {
        var nodes = new List<FileNode>();
        ulong nextFile = 1000;

        Dir(nodes, 10, 5, "Windows");
        Dir(nodes, 11, 10, "System32");
        Files(nodes, ref nextFile, 11, 40, i => ($"system-{i:D3}.dll", 200_000 + i * 12_345, AgeDays(400 + i)));
        Files(nodes, ref nextFile, 10, 6, i => ($"setup-{i}.log", 40_000 + i * 900, AgeDays(30 + i)));

        // These names mirror files that exist on every Windows machine, so the demo's
        // synthetic paths resolve against the REAL shell — exercises icon/thumbnail
        // loading without elevation (fake paths fall back to plain shapes).
        File(nodes, ref nextFile, 10, "explorer.exe", 5_400_000, AgeDays(500));
        File(nodes, ref nextFile, 10, "notepad.exe", 360_000, AgeDays(500));
        File(nodes, ref nextFile, 10, "win.ini", 400, AgeDays(500)); // real text file → panel text preview
        File(nodes, ref nextFile, 11, "kernel32.dll", 780_000, AgeDays(500));
        File(nodes, ref nextFile, 11, "user32.dll", 1_700_000, AgeDays(500));
        Dir(nodes, 12, 10, "Web");
        Dir(nodes, 13, 12, "Wallpaper");
        Dir(nodes, 14, 13, "Windows");
        File(nodes, ref nextFile, 14, "img0.jpg", 4_100_000, AgeDays(500));

        Dir(nodes, 20, 5, "Program Files");
        Dir(nodes, 21, 20, "Sample Game");
        Files(nodes, ref nextFile, 21, 4, i => ($"data-{i}.pak", 2_000_000_000L + i * 350_000_000L, AgeDays(200)));
        File(nodes, ref nextFile, 21, "game.exe", 48_000_000, AgeDays(200));

        // NTFS system roots — hidden by default, shown via the "system folders" setting.
        Dir(nodes, 50, 5, "$Recycle.Bin");
        File(nodes, ref nextFile, 50, "$IABCDEF.tmp", 0, AgeDays(5));
        Dir(nodes, 51, 5, "System Volume Information");

        Dir(nodes, 30, 5, "Users");
        Dir(nodes, 31, 30, "benny");

        Dir(nodes, 32, 31, "Documents");
        File(nodes, ref nextFile, 32, "report.docx", 1_400_000, AgeDays(0.2));
        File(nodes, ref nextFile, 32, "report (1).docx", 1_400_000, AgeDays(0.5));   // duplicate + similar
        File(nodes, ref nextFile, 32, "report_v2.docx", 1_650_000, AgeDays(3));      // similar name
        File(nodes, ref nextFile, 32, "notes.md", 18_000, AgeDays(0.1));
        Files(nodes, ref nextFile, 32, 12, i => ($"invoice-{2024 + i / 12}-{1 + i % 12:D2}.pdf", 220_000 + i * 8_000, AgeDays(20 * (i + 1))));

        Dir(nodes, 33, 31, "Pictures");
        Files(nodes, ref nextFile, 33, 24, i => ($"IMG_{4200 + i}.jpg", 3_500_000 + i * 210_000, AgeDays(2 + i * 5)));
        File(nodes, ref nextFile, 33, "wallpaper.png", 12_000_000, AgeDays(90));

        Dir(nodes, 34, 31, "Downloads");
        File(nodes, ref nextFile, 34, "installer.exe", 85_000_000, AgeDays(1));
        File(nodes, ref nextFile, 34, "backup.7z", 5_200_000_000, AgeDays(45));
        File(nodes, ref nextFile, 34, "song.flac", 42_000_000, AgeDays(8));
        Files(nodes, ref nextFile, 34, 8, i => ($"clip-{i}.mp4", 150_000_000L + i * 65_000_000L, AgeDays(1 + i)));

        return nodes;
    }

    private static List<FileNode> BuildD(int stressFiles)
    {
        var nodes = new List<FileNode>();
        ulong nextFile = 1000;

        Dir(nodes, 10, 5, "Projects");
        Dir(nodes, 11, 10, "graph-app");
        Files(nodes, ref nextFile, 11, 30, i => ($"Module{i:D2}.cs", 4_000 + i * 1_700, AgeDays(0.05 * (i + 1))));
        File(nodes, ref nextFile, 11, "README.md", 9_000, AgeDays(0.3));

        Dir(nodes, 20, 5, "Models");
        File(nodes, ref nextFile, 20, "dream-large.safetensors", 22_000_000_000, AgeDays(60));
        File(nodes, ref nextFile, 20, "dream-small.gguf", 6_500_000_000, AgeDays(14));
        File(nodes, ref nextFile, 20, "report.docx", 1_400_000, AgeDays(120)); // cross-volume duplicate of C:'s

        Dir(nodes, 30, 5, "Archive");
        Files(nodes, ref nextFile, 30, 60, i => ($"snapshot-{i:D3}.zip", 90_000_000L + i * 3_000_000L, AgeDays(365 + i * 10)));

        if (stressFiles > 0)
        {
            // Perf-probe dir: one flat directory with N deterministic pseudo-random files
            // (Knuth-hash sizes across 5 decades, rotating extensions for color variety).
            Dir(nodes, 40, 5, "Stress");
            string[] exts = [".jpg", ".mp4", ".dll", ".txt", ".zip", ".cs", ".png", ".exe"];
            Files(nodes, ref nextFile, 40, stressFiles, i =>
            {
                uint hash = (uint)i * 2654435761u;
                long size = 1_000L << (int)(hash % 17);          // 1 KB .. 65 GB, log-distributed
                return ($"stress-{i:D6}{exts[i % exts.Length]}", size + hash % 999, AgeDays(hash % 1500));
            });
        }

        return nodes;
    }

    private static void Dir(List<FileNode> nodes, ulong id, ulong parent, string name) =>
        nodes.Add(new FileNode(id, parent, name, 0, IsDirectory: true));

    private static void File(List<FileNode> nodes, ref ulong nextId, ulong parent, string name, long size, long lastWrite) =>
        nodes.Add(new FileNode(nextId++, parent, name, size, IsDirectory: false) { LastWriteFileTime = lastWrite });

    private static void Files(List<FileNode> nodes, ref ulong nextId, ulong parent, int count, Func<int, (string Name, long Size, long LastWrite)> factory)
    {
        for (int i = 0; i < count; i++)
        {
            (string name, long size, long lastWrite) = factory(i);
            File(nodes, ref nextId, parent, name, size, lastWrite);
        }
    }

    private static long AgeDays(double days) => (Now - TimeSpan.FromDays(days)).ToFileTimeUtc();
}
