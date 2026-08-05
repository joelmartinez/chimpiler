using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Abstractions;

/// <summary>Stores the lightweight knowledge graph and supports in-memory traversal.</summary>
public interface IGraphStore
{
    Task<long> UpsertNodeAsync(string kind, string key, long? chunkId, long? documentId, CancellationToken cancellationToken = default);

    Task AddEdgeAsync(long sourceNodeId, long targetNodeId, string kind, double weight = 1.0, CancellationToken cancellationToken = default);

    Task SetNodeMetadataAsync(long nodeId, string key, string value, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetNodeMetadataAsync(long nodeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KbNode>> GetNodesForChunksAsync(IReadOnlyCollection<long> chunkIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KbNode>> GetNodesByKindAsync(string kind, CancellationToken cancellationToken = default);

    Task<KbNode?> GetNodeAsync(string kind, string key, CancellationToken cancellationToken = default);

    /// <summary>Expands outward from the given nodes up to <paramref name="depth"/> hops, returning reachable chunk ids.</summary>
    Task<IReadOnlyList<long>> ExpandAsync(IReadOnlyCollection<long> nodeIds, int depth, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
