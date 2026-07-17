using System;
using System.Collections.Generic;
using System.Linq;

namespace DreamOfElectricStorage.Core;

/// <summary>
/// One bucket in the nested facet partition (a strict GROUP BY tree). Every graph node is a
/// <see cref="LooseMembers"/> of exactly ONE bucket — that is the node's single cohesion target,
/// so physics can never pull a node toward competing centroids (the old multi-membership churn).
/// A bucket's <see cref="Children"/> are its sub-groups under the next facet; a size-1 sub-group
/// never becomes a child — its lone member collapses into this bucket's loose members instead.
/// </summary>
public sealed class PartitionBucket
{
    /// <summary>The facet that FORMED this bucket (its parent split on it); null at the root.</summary>
    public WellKind? BindingFacet { get; init; }
    /// <summary>The facet this bucket splits its children on; null when terminal (no children).</summary>
    public WellKind? SplitFacet { get; init; }
    /// <summary>This bucket's key value under its parent's facet; null at the root.</summary>
    public string? Key { get; init; }
    /// <summary>Depth from the root (0 = root).</summary>
    public int Depth { get; init; }
    /// <summary>Collapsed singletons + null-key fall-through + terminal members held directly here.</summary>
    public IReadOnlyList<int> LooseMembers { get; init; } = Array.Empty<int>();
    /// <summary>Sub-buckets, each with a subtree of >= 2 members.</summary>
    public IReadOnlyList<PartitionBucket> Children { get; init; } = Array.Empty<PartitionBucket>();
    /// <summary>Whole subtree (loose + all descendants), sorted — for pack mass + centering.</summary>
    public IReadOnlyList<int> AllMembers { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Builds the nested facet partition for a working set. Pure, deterministic, O(N·depth) — cheap
/// enough to re-run on every reorder/toggle over the SAME per-node facet keys (the expensive key
/// derivation happens once in <see cref="ClusterGraphBuilder"/>).
///
/// At each level nodes are bucketed by their key for that facet; a null key drops the node to
/// loose at this level (it does NOT descend to deeper facets — deeper facets only exist inside a
/// bucket of this level). A key-group of one member collapses to loose (singleton collapse) — this
/// is what stops equivalence facets (Duplicate/SimilarName), whose keys are unique for most files,
/// from producing thousands of size-1 wells.
/// </summary>
public static class NestedPartition
{
    public static PartitionBucket Build(IReadOnlyList<string?[]> facetKeys, FacetOrder order, int nodeCount)
    {
        var all = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
            all[i] = i;
        return BuildBucket(facetKeys, order.Enabled, all, depth: 0, binding: null, key: null);
    }

    private static PartitionBucket BuildBucket(
        IReadOnlyList<string?[]> facetKeys, IReadOnlyList<WellKind> facets,
        IReadOnlyList<int> indices, int depth, WellKind? binding, string? key)
    {
        // Out of facets → terminal bucket: everything here shares the full facet path and is loose.
        if (depth >= facets.Count)
        {
            var members = indices.OrderBy(i => i).ToArray();
            return new PartitionBucket
            {
                Depth = depth, BindingFacet = binding, SplitFacet = null, Key = key,
                LooseMembers = members, Children = Array.Empty<PartitionBucket>(), AllMembers = members,
            };
        }

        int fi = (int)facets[depth];
        var groups = new Dictionary<string, List<int>>();
        var loose = new List<int>();
        foreach (int idx in indices) // input order preserved → group lists stay index-ascending
        {
            string? k = fi < facetKeys[idx].Length ? facetKeys[idx][fi] : null;
            if (k is null)
            {
                loose.Add(idx); // no value for this facet → loose here, does not descend
                continue;
            }
            if (!groups.TryGetValue(k, out var list))
                groups[k] = list = [];
            list.Add(idx);
        }

        var children = new List<PartitionBucket>();
        // Order key-groups by their smallest member index → determinism (independent of dictionary order).
        foreach (var kv in groups.OrderBy(kv => kv.Value[0]))
        {
            if (kv.Value.Count == 1)
                loose.Add(kv.Value[0]); // singleton collapse → loose member of THIS bucket
            else
                children.Add(BuildBucket(facetKeys, facets, kv.Value, depth + 1, facets[depth], kv.Key));
        }

        loose.Sort();
        var allMembers = new List<int>(loose);
        foreach (var child in children)
            allMembers.AddRange(child.AllMembers);
        allMembers.Sort();

        return new PartitionBucket
        {
            Depth = depth, BindingFacet = binding, Key = key,
            SplitFacet = children.Count > 0 ? facets[depth] : null,
            LooseMembers = loose, Children = children, AllMembers = allMembers,
        };
    }
}
