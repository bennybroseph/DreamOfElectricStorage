namespace DreamOfElectricStorage.Core;

/// <summary>USN_REASON_* flags relevant to index maintenance (values from Windows metadata).</summary>
public static class UsnReasons
{
    public const uint FileCreate = Windows.Win32.PInvoke.USN_REASON_FILE_CREATE;
    public const uint FileDelete = Windows.Win32.PInvoke.USN_REASON_FILE_DELETE;
    public const uint RenameOldName = Windows.Win32.PInvoke.USN_REASON_RENAME_OLD_NAME;
    public const uint RenameNewName = Windows.Win32.PInvoke.USN_REASON_RENAME_NEW_NAME;
}

/// <summary>
/// Queryable in-memory snapshot of one volume's file hierarchy, built from an
/// indexer stream and kept fresh via <see cref="Apply"/>.
/// NOT thread-safe: the index owner must serialize all reads and writes
/// (the WinUI app owns it on the dispatcher thread; the CLI on its main loop).
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

    /// <summary>
    /// Journal identity captured before the enumeration this index was built from;
    /// NextUsn advances as change batches are applied. Null when built without watch state.
    /// </summary>
    public JournalState? Journal { get; private set; }

    public int Count => _nodesById.Count;

    /// <summary>Nodes attached directly under the volume root.</summary>
    public IReadOnlyList<FileNode> RootEntries => GetChildren(SyntheticRootId);

    public static async Task<VolumeIndex> BuildAsync(
        string volume,
        IAsyncEnumerable<FileNode> nodes,
        CancellationToken cancellationToken = default,
        IProgress<VolumeIndexProgress>? progress = null,
        JournalState? journal = null)
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
        return new VolumeIndex(normalizedVolume, nodesById, childrenByParent) { Journal = journal };
    }

    /// <summary>
    /// Applies one journal change batch. Remove-type reasons (delete, rename-old) win over
    /// add-type ones within a record because USN reason flags are cumulative per file-open:
    /// e.g. a created-then-deleted temp file's final record carries CREATE|DELETE.
    /// </summary>
    public void Apply(UsnChangeBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        foreach (UsnJournalEntry entry in batch.Entries)
        {
            if ((entry.Reason & (UsnReasons.FileDelete | UsnReasons.RenameOldName)) != 0)
                Remove(entry.Node.Id);
            else if ((entry.Reason & (UsnReasons.FileCreate | UsnReasons.RenameNewName)) != 0)
                Upsert(entry.Node);
        }

        if (Journal is { } journal && batch.NextUsn > 0)
            Journal = journal with { NextUsn = batch.NextUsn };
    }

    private void Remove(ulong id)
    {
        if (!_nodesById.Remove(id, out FileNode? node))
            return;

        DetachFromParent(node);

        // A removed dir's children list stays keyed under its FRN on purpose:
        // a rename pair (OLD removes, NEW re-adds the same FRN) reattaches them for free,
        // and a true delete drains them via the children's own delete records
        // (NTFS never deletes a non-empty directory).
    }

    private void Upsert(FileNode node)
    {
        if (_nodesById.TryGetValue(node.Id, out FileNode? existing))
            DetachFromParent(existing);

        _nodesById[node.Id] = node;
        GetOrAddChildList(ResolveParentKey(node)).Add(node);
    }

    /// <summary>
    /// Removes the node from whichever child list actually holds it. The resolved parent
    /// key can drift after the node was inserted (its parent may since have been removed),
    /// so fall back from the resolved key to the raw ParentId to the synthetic root.
    /// </summary>
    private void DetachFromParent(FileNode node)
    {
        if (TryDetach(ResolveParentKey(node), node) || TryDetach(node.ParentId, node))
            return;
        TryDetach(SyntheticRootId, node);

        bool TryDetach(ulong parentKey, FileNode child) =>
            _childrenByParent.TryGetValue(parentKey, out List<FileNode>? siblings) && siblings.Remove(child);
    }

    private ulong ResolveParentKey(FileNode node) =>
        node.ParentId != node.Id && _nodesById.ContainsKey(node.ParentId) ? node.ParentId : SyntheticRootId;

    private List<FileNode> GetOrAddChildList(ulong parentId)
    {
        if (!_childrenByParent.TryGetValue(parentId, out List<FileNode>? children))
            _childrenByParent[parentId] = children = [];
        return children;
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
