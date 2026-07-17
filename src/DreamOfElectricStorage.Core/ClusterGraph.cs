using System;
using System.Collections.Generic;
using System.Linq;

namespace DreamOfElectricStorage.Core;

/// <summary>Render + identity info for one node in a <see cref="ClusterGraph"/> (parallel to Items by global id).</summary>
public readonly record struct ClusterNodeInfo(
    ulong Frn, VolumeIndex Volume, string Name, long SizeBytes, FileTypeCategory Category, string Drive);

/// <summary>
/// A bounded working set of files plus each file's per-facet grouping keys. Global ids are
/// sequential (0..N-1) — NTFS FRNs are per-volume and would collide across drives, so
/// <see cref="ClusterLayout"/> keys on these instead. The Clusters view composes the nested
/// partition from <see cref="FacetKeys"/> + a <see cref="FacetOrder"/> at layout time — the keys
/// are derived once here (the expensive part); re-grouping on a reorder is cheap.
/// </summary>
public sealed class ClusterGraph
{
    public IReadOnlyList<ClusterLayout.Item> Items { get; }

    /// <summary>Per-node facet key row, indexed by <c>(int)WellKind</c> (Folder/Duplicate/SimilarName/
    /// Type/Date). A null entry means the node has no value for that facet (e.g. no timestamp).</summary>
    public IReadOnlyList<string?[]> FacetKeys { get; }

    public IReadOnlyList<ClusterNodeInfo> Nodes { get; }

    /// <summary>Files eligible before the working-set cap — for "showing N of M".</summary>
    public int TotalEligible { get; }

    internal ClusterGraph(
        IReadOnlyList<ClusterLayout.Item> items,
        IReadOnlyList<string?[]> facetKeys,
        IReadOnlyList<ClusterNodeInfo> nodes,
        int totalEligible)
    {
        Items = items;
        FacetKeys = facetKeys;
        Nodes = nodes;
        TotalEligible = totalEligible;
    }
}

/// <summary>
/// Builds a <see cref="ClusterGraph"/> from a machine index. The working set is the
/// <c>maxNodes</c> largest files across all volumes (size-first matches the size-gravity
/// layout — big files and their big duplicates are the ones worth showing); the per-facet
/// grouping keys are derived WITHIN that set. Scoped-to-working-set on purpose: no 7.7M-node
/// global relationship index (memory), per the C5 plan. Duplicates of a shown file that fall
/// outside the top-N aren't grouped — acceptable since big files' dups are also big.
/// </summary>
public static class ClusterGraphBuilder
{
    private const long DateBucketTicks = 864_000_000_000L; // 1 day in FILETIME 100ns ticks
    private const int FacetCount = 5;                      // Folder, Duplicate, SimilarName, Type, Date

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
        var facetKeys = new List<string?[]>(candidates.Count);

        for (int i = 0; i < candidates.Count; i++)
        {
            var (volume, node) = candidates[i];
            ulong gid = (ulong)i;
            FileTypeCategory cat = FileTypeClassifier.Classify(node.Name);
            string nameKey = NameStem.Normalize(node.Name);

            items.Add(new ClusterLayout.Item(gid, node.SizeBytes));
            nodes.Add(new ClusterNodeInfo(node.Id, volume, node.Name, node.SizeBytes, cat, volume.Volume));

            var keys = new string?[FacetCount];
            keys[(int)WellKind.Folder] = $"{volume.Volume}|{node.ParentId}";
            keys[(int)WellKind.Duplicate] = $"{node.Name.ToLowerInvariant()}|{node.SizeBytes}";
            keys[(int)WellKind.SimilarName] = nameKey.Length > 0 ? nameKey : null;
            keys[(int)WellKind.Type] = cat.ToString();
            keys[(int)WellKind.Date] = node.LastWriteFileTime > 0
                ? (node.LastWriteFileTime / DateBucketTicks).ToString()
                : null;
            facetKeys.Add(keys);
        }

        return new ClusterGraph(items, facetKeys, nodes, totalEligible);
    }
}
