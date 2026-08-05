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
    private const int SemanticNeighbourCount = 1;
    private const int SemanticCandidatePoolSize = 8;
    private const double SemanticLinkMinimumSimilarity = 0.55;
    private const double LexicalOverlapWeight = 0.1;
    private static readonly char[] SemanticTokenSeparators = [' ', '\t', '\n', '\r', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '#', '-', '`'];

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

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        var embeddings = await _embeddingProvider.EmbedQueriesAsync(new[] { query }, cancellationToken).ConfigureAwait(false);
        return await _vectorStore.SearchAsync(embeddings[0], _embeddingProvider.Name, topK, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> GraphSearchAsync(string query, int topK = 5, int depth = 1, CancellationToken cancellationToken = default)
    {
        var seeds = await SearchAsync(query, topK, cancellationToken).ConfigureAwait(false);
        if (seeds.Count == 0)
        {
            return seeds;
        }

        var seedChunkIds = seeds.Select(s => s.ChunkId).ToList();
        var seedNodes = await _graphStore.GetNodesForChunksAsync(seedChunkIds, cancellationToken).ConfigureAwait(false);
        var expandedChunkIds = (await _graphStore
                .ExpandAsync(seedNodes.Select(n => n.Id).ToList(), depth, cancellationToken)
                .ConfigureAwait(false))
            .Except(seedChunkIds)
            .ToList();

        var neighbours = await _vectorStore.GetChunksAsync(expandedChunkIds, cancellationToken).ConfigureAwait(false);
        var lowestSeedScore = seeds.Min(s => s.Score);

        var combined = seeds
            .Concat(neighbours.Select(n => n with { Score = lowestSeedScore * GraphExpansionWeight }))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.ChunkId)
            .ToList();

        return combined;
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

        await LinkSemanticNeighboursAsync(documentId, cancellationToken).ConfigureAwait(false);
        return chunks.Count;
    }

    private async Task LinkSemanticNeighboursAsync(long documentId, CancellationToken cancellationToken)
    {
        var documentChunks = (await _vectorStore
                .GetChunksForDocumentAsync(documentId, cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(chunk => chunk.Id);

        foreach (var chunk in documentChunks.Values)
        {
            var neighbours = await _vectorStore
                .SearchAsync(chunk.Embedding, _embeddingProvider.Name, documentChunks.Count + SemanticCandidatePoolSize, cancellationToken)
                .ConfigureAwait(false);
            var relatedChunks = neighbours
                .Where(result => result.DocumentId != documentId && result.Score >= SemanticLinkMinimumSimilarity)
                .OrderByDescending(result => result.Score + (LexicalOverlapWeight * TokenOverlap(chunk.Text, result.Text)))
                .Take(SemanticNeighbourCount)
                .ToList();
            if (relatedChunks.Count == 0)
            {
                continue;
            }

            var nodes = await _graphStore
                .GetNodesForChunksAsync(new[] { chunk.Id }.Concat(relatedChunks.Select(result => result.ChunkId)).ToList(), cancellationToken)
                .ConfigureAwait(false);
            var nodesByChunkId = nodes
                .Where(node => node.ChunkId is not null)
                .ToDictionary(node => node.ChunkId!.Value);

            if (!nodesByChunkId.TryGetValue(chunk.Id, out var sourceNode))
            {
                throw new InvalidOperationException($"No graph node exists for chunk {chunk.Id}.");
            }

            foreach (var related in relatedChunks)
            {
                if (!nodesByChunkId.TryGetValue(related.ChunkId, out var targetNode))
                {
                    throw new InvalidOperationException($"No graph node exists for chunk {related.ChunkId}.");
                }

                await _graphStore
                    .AddEdgeAsync(sourceNode.Id, targetNode.Id, EdgeKinds.Semantic, related.Score, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static double TokenOverlap(string left, string right)
    {
        var leftTokens = left
            .Split(SemanticTokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 4)
            .Select(token => token.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var rightTokens = right
            .Split(SemanticTokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 4)
            .Select(token => token.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        return (double)leftTokens.Intersect(rightTokens).Count() / Math.Min(leftTokens.Count, rightTokens.Count);
    }

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
