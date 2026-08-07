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
    Task<IReadOnlyList<KbNode>> GetNodesAsync(IReadOnlyCollection<long> nodeIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KbEdge>> GetEdgesForNodesAsync(IReadOnlyCollection<long> nodeIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KbNode>> GetNodesByKindAsync(string kind, CancellationToken cancellationToken = default);

    Task<KbNode?> GetNodeAsync(string kind, string key, CancellationToken cancellationToken = default);

    /// <summary>Expands outward through allowed edges, retaining a path for every reached chunk.</summary>
    Task<IReadOnlyList<GraphTraversal>> TraverseAsync(IReadOnlyCollection<long> nodeIds, int depth, IReadOnlyCollection<string> edgeKinds, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
