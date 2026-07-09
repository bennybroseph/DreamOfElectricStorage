namespace DreamOfElectricStorage.Core;

/// <summary>
/// Queryable in-memory snapshot of one volume's file hierarchy, built from an
/// indexer stream. Immutable after build — safe for concurrent reads.
/// </summary>
public sealed class VolumeIndex
{
    /// <summary>
    /// Synthetic parent for nodes whose ParentId is not in the snapshot.
    /// The NTFS root directory (MFT record 5) is not emitted by MFT enumeration,
    /// so its direct children (e.g. $Extend, Users) land here.
    /// </summary>
    public const ulong SyntheticRootId = ulong.MaxValue;

    private const int MaxPathDepth = 512; // cycle/corruption guard for parent walks

    private readonly Dictionary<ulong, FileNode> _nodesById;
    private readonly Dictionary<ulong, List<FileNode>> _childrenByParent;

    private VolumeIndex(string volume, Dictionary<ulong, FileNode> nodesById, Dictionary<ulong, List<FileNode>> childrenByParent)
    {
        Volume = volume;
        _nodesById = nodesById;
        _childrenByParent = childrenByParent;
    }

    /// <summary>Drive designator this index covers, e.g. "C:".</summary>
    public string Volume { get; }

    public int Count => _nodesById.Count;

    /// <summary>Nodes attached directly under the volume root.</summary>
    public IReadOnlyList<FileNode> RootEntries => GetChildren(SyntheticRootId);

    public static async Task<VolumeIndex> BuildAsync(
        string volume,
        IAsyncEnumerable<FileNode> nodes,
        CancellationToken cancellationToken = default,
        IProgress<VolumeIndexProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);
        ArgumentNullException.ThrowIfNull(nodes);
        string normalizedVolume = $"{char.ToUpperInvariant(volume.TrimStart()[0])}:";

        // Pass 1: collect. Duplicate FRNs (e.g. rename during enumeration) — last wins.
        var nodesById = new Dictionary<ulong, FileNode>();
        long streamed = 0;
        await foreach (FileNode node in nodes.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            nodesById[node.Id] = node;
            if (++streamed % 250_000 == 0)
                progress?.Report(new VolumeIndexProgress(normalizedVolume, streamed, Completed: false));
        }

        // Pass 2: adjacency. Parents missing from the snapshot (incl. the unemitted
        // NTFS root) and self-parented nodes resolve to the synthetic root.
        var childrenByParent = new Dictionary<ulong, List<FileNode>>();
        foreach (FileNode node in nodesById.Values)
        {
            ulong parentId = node.ParentId != node.Id && nodesById.ContainsKey(node.ParentId)
                ? node.ParentId
                : SyntheticRootId;

            if (!childrenByParent.TryGetValue(parentId, out List<FileNode>? siblings))
                childrenByParent[parentId] = siblings = [];
            siblings.Add(node);
        }

        progress?.Report(new VolumeIndexProgress(normalizedVolume, nodesById.Count, Completed: true));
        return new VolumeIndex(normalizedVolume, nodesById, childrenByParent);
    }

    public bool TryGetNode(ulong id, out FileNode node) => _nodesById.TryGetValue(id, out node!);

    public IReadOnlyList<FileNode> GetChildren(ulong parentId) =>
        _childrenByParent.TryGetValue(parentId, out List<FileNode>? children) ? children : [];

    /// <summary>Full path of a node, e.g. "C:\Users\benny\file.txt". Null if the id is unknown.</summary>
    public string? GetPath(ulong id)
    {
        if (!_nodesById.TryGetValue(id, out FileNode? node))
            return null;

        var components = new Stack<string>();
        for (int depth = 0; node is not null && depth < MaxPathDepth; depth++)
        {
            components.Push(node.Name);
            if (node.ParentId == node.Id || !_nodesById.TryGetValue(node.ParentId, out node))
                node = null;
        }

        return $@"{Volume}\{string.Join('\\', components)}";
    }

    /// <summary>Case-insensitive substring search over names. Lazy linear scan.</summary>
    public IEnumerable<FileNode> Search(string substring)
    {
        ArgumentException.ThrowIfNullOrEmpty(substring);
        foreach (FileNode node in _nodesById.Values)
        {
            if (node.Name.Contains(substring, StringComparison.OrdinalIgnoreCase))
                yield return node;
        }
    }
}

/// <summary>Build progress for one volume; emitted every 250k nodes and on completion.</summary>
public sealed record VolumeIndexProgress(string Volume, long NodesIndexed, bool Completed);
