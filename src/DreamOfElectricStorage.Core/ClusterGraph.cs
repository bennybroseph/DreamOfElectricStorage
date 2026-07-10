using System;
using System.Collections.Generic;
using System.Linq;

namespace DreamOfElectricStorage.Core;

/// <summary>Render + identity info for one node in a <see cref="ClusterGraph"/> (parallel to Items by global id).</summary>
public readonly record struct ClusterNodeInfo(
    ulong Frn, VolumeIndex Volume, string Name, long SizeBytes, FileTypeCategory Category, string Drive);

/// <summary>
/// A bounded working set of files plus the relationship groups the Clusters view lays out.
/// Global ids are sequential (0..N-1) — NTFS FRNs are per-volume and would collide across
/// drives, so <see cref="ClusterLayout"/> keys on these instead.
/// </summary>
public sealed class ClusterGraph
{
    public IReadOnlyList<ClusterLayout.Item> Items { get; }
    public IReadOnlyList<ClusterLayout.Group> Groups { get; }
    public IReadOnlyList<ClusterNodeInfo> Nodes { get; }

    /// <summary>Files eligible before the working-set cap — for "showing N of M".</summary>
    public int TotalEligible { get; }

    internal ClusterGraph(
        IReadOnlyList<ClusterLayout.Item> items,
        IReadOnlyList<ClusterLayout.Group> groups,
        IReadOnlyList<ClusterNodeInfo> nodes,
        int totalEligible)
    {
        Items = items;
        Groups = groups;
        Nodes = nodes;
        TotalEligible = totalEligible;
    }
}

/// <summary>
/// Builds a <see cref="ClusterGraph"/> from a machine index. The working set is the
/// <c>maxNodes</c> largest files across all volumes (size-first matches the size-gravity
/// layout — big files and their big duplicates are the ones worth showing); relationships
/// are grouped WITHIN that set. Scoped-to-working-set on purpose: no 7.7M-node global
/// relationship index (memory), per the C5 plan. Duplicates of a shown file that fall
/// outside the top-N aren't grouped — acceptable since big files' dups are also big.
/// </summary>
public static class ClusterGraphBuilder
{
    private const long DateBucketTicks = 864_000_000_000L; // 1 day in FILETIME 100ns ticks

    public static ClusterGraph Build(IReadOnlyList<VolumeIndex> volumes, int maxNodes)
    {
        // Working set: each volume's largest files, merged, globally trimmed to maxNodes.
        var candidates = volumes
            .SelectMany(v => v.TopBySize(maxNodes, directories: false).Select(n => (Volume: v, Node: n)))
            .OrderByDescending(e => e.Node.SizeBytes)
            .Take(maxNodes)
            .ToList();

        int totalEligible = volumes.Sum(v => v.AllNodes.Count(n => !n.IsDirectory && n.SizeBytes > 0));

        var items = new List<ClusterLayout.Item>(candidates.Count);
        var nodes = new List<ClusterNodeInfo>(candidates.Count);
        var folder = new Dictionary<string, List<ulong>>();
        var dup = new Dictionary<string, List<ulong>>();
        var name = new Dictionary<string, List<ulong>>();
        var type = new Dictionary<string, List<ulong>>();
        var date = new Dictionary<string, List<ulong>>();

        for (int i = 0; i < candidates.Count; i++)
        {
            var (volume, node) = candidates[i];
            ulong gid = (ulong)i;
            FileTypeCategory cat = FileTypeClassifier.Classify(node.Name);
            string nameKey = NameStem.Normalize(node.Name);

            items.Add(new ClusterLayout.Item(gid, node.SizeBytes));
            nodes.Add(new ClusterNodeInfo(node.Id, volume, node.Name, node.SizeBytes, cat, volume.Volume));

            Add(folder, $"{volume.Volume}|{node.ParentId}", gid);
            Add(dup, $"{node.Name.ToLowerInvariant()}|{node.SizeBytes}", gid);
            if (nameKey.Length > 0)
                Add(name, nameKey, gid);
            Add(type, cat.ToString(), gid);
            if (node.LastWriteFileTime > 0)
                Add(date, (node.LastWriteFileTime / DateBucketTicks).ToString(), gid);
        }

        var groups = new List<ClusterLayout.Group>();
        AddGroups(groups, WellKind.Folder, folder);
        AddGroups(groups, WellKind.Duplicate, dup);
        AddGroups(groups, WellKind.SimilarName, name);
        AddGroups(groups, WellKind.Type, type);
        AddGroups(groups, WellKind.Date, date);

        return new ClusterGraph(items, groups, nodes, totalEligible);
    }

    private static void Add(Dictionary<string, List<ulong>> map, string key, ulong gid)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(gid);
    }

    private static void AddGroups(List<ClusterLayout.Group> groups, WellKind kind, Dictionary<string, List<ulong>> map)
    {
        foreach (var list in map.Values)
            if (list.Count > 1) // a well needs at least two members
                groups.Add(new ClusterLayout.Group(kind, list));
    }
}
