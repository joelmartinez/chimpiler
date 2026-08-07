using System.Security.Cryptography;
using System.Text;
using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Chunking;
using Chimpiler.Kb.Models;

namespace Chimpiler.Kb;

/// <summary>
/// Default <see cref="IKnowledgeBase"/>: chunk → embed → store → graph, and
/// query → embed → vector search → graph expansion → rank.
/// </summary>
public sealed class KnowledgeBase : IKnowledgeBase
{
    private static readonly string[] AgentTraversalEdgeKinds =
    [
        EdgeKinds.Mentions,
        EdgeKinds.Evidence,
        EdgeKinds.Subject,
        EdgeKinds.Object,
        EdgeKinds.AgentAsserted
    ];

    private readonly IVectorStore _vectorStore;
    private readonly IGraphStore _graphStore;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ChunkerRegistry _chunkers;
    private readonly bool _allowEmbeddingMismatch;

    public KnowledgeBase(
        IVectorStore vectorStore,
        IGraphStore graphStore,
        IEmbeddingProvider embeddingProvider,
        ChunkerRegistry chunkers,
        bool allowEmbeddingMismatch = false)
    {
        _vectorStore = vectorStore;
        _graphStore = graphStore;
        _embeddingProvider = embeddingProvider;
        _chunkers = chunkers;
        _allowEmbeddingMismatch = allowEmbeddingMismatch;
    }

    /// <summary>Weight applied to graph-expanded neighbours so they rank below direct vector hits.</summary>
    public double GraphExpansionWeight { get; init; } = 0.5;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _vectorStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var storedProvider = await _vectorStore.GetSettingAsync("embedding.provider", cancellationToken).ConfigureAwait(false);
        var storedDimension = await _vectorStore.GetSettingAsync("embedding.dimension", cancellationToken).ConfigureAwait(false);
        var embeddingsMismatch =
            (storedProvider is not null && !string.Equals(storedProvider, _embeddingProvider.Name, StringComparison.Ordinal)) ||
            (storedDimension is not null && !string.Equals(storedDimension, _embeddingProvider.Dimension.ToString(), StringComparison.Ordinal));
        if (embeddingsMismatch && !_allowEmbeddingMismatch)
        {
            throw new InvalidOperationException(
                $"Knowledge base embeddings use '{storedProvider}' ({storedDimension} dimensions), but the active provider is '{_embeddingProvider.Name}' ({_embeddingProvider.Dimension} dimensions). Run 'chimpiler kb rebuild --model <model>' to switch models.");
        }

        if (embeddingsMismatch)
        {
            // Keep the old settings until the replacement embeddings have all been written.
            return;
        }

        await _vectorStore.SetSettingAsync("embedding.provider", _embeddingProvider.Name, cancellationToken).ConfigureAwait(false);
        await _vectorStore.SetSettingAsync("embedding.dimension", _embeddingProvider.Dimension.ToString(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> AddDocumentAsync(string path, ChunkingOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Document not found: {path}", path);
        }

        var fullPath = Path.GetFullPath(path);
        var text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return await IndexAsync(fullPath, text, options ?? ChunkingOptions.Default, cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveDocumentAsync(string path, CancellationToken cancellationToken = default) =>
        _vectorStore.RemoveDocumentAsync(Path.GetFullPath(path), cancellationToken);

    public Task<int> UpdateDocumentAsync(string path, ChunkingOptions? options = null, CancellationToken cancellationToken = default) =>
        AddDocumentAsync(path, options, cancellationToken);

    public Task<IReadOnlyList<KbDocument>> ListDocumentsAsync(CancellationToken cancellationToken = default) =>
        _vectorStore.ListDocumentsAsync(cancellationToken);

    public async Task<IReadOnlyList<KbEntity>> ListEntitiesAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await _graphStore.GetNodesByKindAsync(NodeKinds.Entity, cancellationToken).ConfigureAwait(false);
        var entities = new List<KbEntity>(nodes.Count);
        foreach (var node in nodes)
        {
            var metadata = await _graphStore.GetNodeMetadataAsync(node.Id, cancellationToken).ConfigureAwait(false);
            entities.Add(new KbEntity(
                node.Key,
                metadata.GetValueOrDefault("entity.kind", "unknown"),
                metadata.GetValueOrDefault("entity.surface", node.Key)));
        }

        return entities.OrderBy(entity => entity.Key, StringComparer.Ordinal).ToList();
    }

    public async Task RegisterEntityAsync(KbEntityMention entity, string evidence, string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.Surface);

        var sourceChunkNode = await FindEvidenceChunkNodeAsync(sourcePath, evidence, cancellationToken).ConfigureAwait(false);
        var entityNodeId = await _graphStore
            .UpsertNodeAsync(NodeKinds.Entity, entity.Key, chunkId: null, documentId: null, cancellationToken)
            .ConfigureAwait(false);
        await _graphStore.SetNodeMetadataAsync(entityNodeId, "entity.kind", entity.Kind, cancellationToken).ConfigureAwait(false);
        await _graphStore.SetNodeMetadataAsync(entityNodeId, "entity.surface", entity.Surface, cancellationToken).ConfigureAwait(false);
        await _graphStore.AddEdgeAsync(sourceChunkNode.Id, entityNodeId, EdgeKinds.Mentions, 1.0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        var embeddings = await _embeddingProvider.EmbedQueriesAsync(new[] { query }, cancellationToken).ConfigureAwait(false);
        return await _vectorStore.SearchAsync(embeddings[0], _embeddingProvider.Name, topK, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> GraphSearchAsync(string query, int topK = 5, int depth = 1, CancellationToken cancellationToken = default)
    {
        var seeds = await SearchAsync(query, topK, cancellationToken).ConfigureAwait(false);
        var seedChunkIds = seeds.Select(s => s.ChunkId).ToList();
        var seedNodes = await _graphStore.GetNodesForChunksAsync(seedChunkIds, cancellationToken).ConfigureAwait(false);
        var traversalSeedNodeIds = seedNodes.Select(node => node.Id).Distinct().ToList();
        if (traversalSeedNodeIds.Count == 0)
        {
            return seeds;
        }

        var expandedChunkIds = (await _graphStore
                .ExpandAsync(traversalSeedNodeIds, depth, AgentTraversalEdgeKinds, cancellationToken)
                .ConfigureAwait(false))
            .Except(seedChunkIds)
            .ToList();

        var neighbours = (await _vectorStore.GetChunksAsync(expandedChunkIds, cancellationToken).ConfigureAwait(false))
            .GroupBy(result => result.DocumentId)
            .Select(group => group.OrderBy(result => result.ChunkId).First())
            .Take(topK)
            .ToList();
        var lowestSeedScore = seeds.Count == 0 ? 1.0 : seeds.Min(s => s.Score);

        var combined = seeds
            .Concat(neighbours.Select(n => n with { Score = lowestSeedScore * GraphExpansionWeight }))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.ChunkId)
            .ToList();

        return combined;
    }

    public async Task AddEntityRelationshipAsync(KbEntityRelationship relationship, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship.SubjectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship.Predicate);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship.ObjectKey);
        if (relationship.Confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(relationship), "Relationship confidence must be between 0 and 1.");
        }

        var subject = await RequireEntityAsync(relationship.SubjectKey, cancellationToken).ConfigureAwait(false);
        var target = await RequireEntityAsync(relationship.ObjectKey, cancellationToken).ConfigureAwait(false);
        var sourceChunkNode = await FindEvidenceChunkNodeAsync(relationship.SourcePath, relationship.Evidence, cancellationToken).ConfigureAwait(false);
        await AddRelationshipEventAsync(subject.Id, target.Id, relationship, sourceChunkNode.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RebuildAsync(ChunkingOptions? options = null, CancellationToken cancellationToken = default)
    {
        var documents = await _vectorStore.ListDocumentsAsync(cancellationToken).ConfigureAwait(false);
        var total = 0;

        foreach (var document in documents)
        {
            if (!File.Exists(document.SourcePath))
            {
                await _vectorStore.RemoveDocumentAsync(document.SourcePath, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var text = await File.ReadAllTextAsync(document.SourcePath, cancellationToken).ConfigureAwait(false);
            total += await IndexAsync(document.SourcePath, text, options ?? ChunkingOptions.Default, cancellationToken).ConfigureAwait(false);
        }

        await _vectorStore.SetSettingAsync("embedding.provider", _embeddingProvider.Name, cancellationToken).ConfigureAwait(false);
        await _vectorStore.SetSettingAsync("embedding.dimension", _embeddingProvider.Dimension.ToString(), cancellationToken).ConfigureAwait(false);
        return total;
    }

    public Task OptimizeAsync(CancellationToken cancellationToken = default) =>
        _vectorStore.OptimizeAsync(cancellationToken);

    private async Task<int> IndexAsync(string fullPath, string text, ChunkingOptions options, CancellationToken cancellationToken)
    {
        var contentType = ContentTypes.FromPath(fullPath);
        var documentId = await _vectorStore.UpsertDocumentAsync(
            fullPath,
            Path.GetFileName(fullPath),
            ComputeHash(text),
            contentType,
            cancellationToken).ConfigureAwait(false);

        await _vectorStore.ClearChunksAsync(documentId, cancellationToken).ConfigureAwait(false);

        var chunks = _chunkers.Chunk(contentType, text, options);
        if (chunks.Count == 0)
        {
            return 0;
        }

        var embeddings = await _embeddingProvider
            .EmbedDocumentsAsync(chunks.Select(c => c.Text).ToList(), cancellationToken)
            .ConfigureAwait(false);

        var documentNodeId = await _graphStore
            .UpsertNodeAsync(NodeKinds.Document, fullPath, chunkId: null, documentId, cancellationToken)
            .ConfigureAwait(false);

        long? previousNodeId = null;
        var headingNodes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var chunkId = await _vectorStore.AddChunkAsync(
                documentId,
                chunk.Ordinal,
                chunk.Text,
                chunk.Heading,
                chunk.TokenCount,
                embeddings[i],
                _embeddingProvider.Name,
                cancellationToken).ConfigureAwait(false);

            var chunkNodeId = await _graphStore
                .UpsertNodeAsync(NodeKinds.Chunk, $"{fullPath}#{chunk.Ordinal}", chunkId, documentId, cancellationToken)
                .ConfigureAwait(false);

            await _graphStore.AddEdgeAsync(documentNodeId, chunkNodeId, EdgeKinds.Contains, 1.0, cancellationToken).ConfigureAwait(false);
            if (previousNodeId is { } previous)
            {
                await _graphStore.AddEdgeAsync(previous, chunkNodeId, EdgeKinds.Child, 1.0, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(chunk.Heading))
            {
                if (!headingNodes.TryGetValue(chunk.Heading, out var sectionNodeId))
                {
                    sectionNodeId = await _graphStore
                        .UpsertNodeAsync(NodeKinds.Section, $"{fullPath}::{chunk.Heading}", chunkId: null, documentId, cancellationToken)
                        .ConfigureAwait(false);
                    headingNodes[chunk.Heading] = sectionNodeId;
                }

                await _graphStore.AddEdgeAsync(sectionNodeId, chunkNodeId, EdgeKinds.Section, 1.0, cancellationToken).ConfigureAwait(false);
            }

            previousNodeId = chunkNodeId;
        }

        return chunks.Count;
    }

    private async Task AddRelationshipEventAsync(
        long subjectNodeId,
        long objectNodeId,
        KbEntityRelationship relationship,
        long? chunkNodeId,
        CancellationToken cancellationToken)
    {
        var eventKey = $"event:{subjectNodeId}:{relationship.Predicate}:{objectNodeId}:{ComputeHash(relationship.Evidence)[..16]}";
        var eventNodeId = await _graphStore
            .UpsertNodeAsync(NodeKinds.Event, eventKey, chunkId: null, documentId: null, cancellationToken)
            .ConfigureAwait(false);
        await _graphStore.SetNodeMetadataAsync(eventNodeId, "event.predicate", relationship.Predicate, cancellationToken).ConfigureAwait(false);
        await _graphStore.SetNodeMetadataAsync(eventNodeId, "event.evidence", relationship.Evidence, cancellationToken).ConfigureAwait(false);
        await _graphStore.SetNodeMetadataAsync(eventNodeId, "event.provenance", relationship.Provenance, cancellationToken).ConfigureAwait(false);
        await _graphStore.AddEdgeAsync(subjectNodeId, eventNodeId, EdgeKinds.Subject, relationship.Confidence, cancellationToken).ConfigureAwait(false);
        await _graphStore.AddEdgeAsync(eventNodeId, objectNodeId, EdgeKinds.Object, relationship.Confidence, cancellationToken).ConfigureAwait(false);
        if (chunkNodeId is { } sourceChunkNodeId)
        {
            await _graphStore.AddEdgeAsync(sourceChunkNodeId, eventNodeId, EdgeKinds.Evidence, 1.0, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _graphStore.AddEdgeAsync(subjectNodeId, objectNodeId, EdgeKinds.AgentAsserted, relationship.Confidence, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<KbNode> RequireEntityAsync(string key, CancellationToken cancellationToken) =>
        await _graphStore.GetNodeAsync(NodeKinds.Entity, key, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Entity '{key}' is not indexed. Run 'chimpiler kb entities' to list available entity keys.");

    private async Task<KbNode> FindEvidenceChunkNodeAsync(string sourcePath, string evidence, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);

        var fullPath = Path.GetFullPath(sourcePath);
        var document = await _vectorStore.GetDocumentAsync(fullPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Source '{fullPath}' is not indexed. Index it before adding graph evidence.");
        var chunks = await _vectorStore.GetChunksForDocumentAsync(document.Id, cancellationToken).ConfigureAwait(false);
        var evidenceChunk = chunks.FirstOrDefault(chunk =>
            chunk.Text.Contains(evidence, StringComparison.OrdinalIgnoreCase));
        if (evidenceChunk is null)
        {
            throw new InvalidOperationException($"The supplied evidence was not found in indexed source '{fullPath}'.");
        }

        var node = (await _graphStore
                .GetNodesForChunksAsync(new[] { evidenceChunk.Id }, cancellationToken)
                .ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.Kind == NodeKinds.Chunk);
        return node ?? throw new InvalidOperationException($"No graph node exists for evidence chunk {evidenceChunk.Id}.");
    }

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
