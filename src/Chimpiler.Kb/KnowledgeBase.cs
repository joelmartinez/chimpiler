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
    private readonly IVectorStore _vectorStore;
    private readonly IGraphStore _graphStore;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ChunkerRegistry _chunkers;

    public KnowledgeBase(
        IVectorStore vectorStore,
        IGraphStore graphStore,
        IEmbeddingProvider embeddingProvider,
        ChunkerRegistry chunkers)
    {
        _vectorStore = vectorStore;
        _graphStore = graphStore;
        _embeddingProvider = embeddingProvider;
        _chunkers = chunkers;
    }

    /// <summary>Weight applied to graph-expanded neighbours so they rank below direct vector hits.</summary>
    public double GraphExpansionWeight { get; init; } = 0.5;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _vectorStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
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
        var embeddings = await _embeddingProvider.EmbedAsync(new[] { query }, cancellationToken).ConfigureAwait(false);
        return await _vectorStore.SearchAsync(embeddings[0], topK, cancellationToken).ConfigureAwait(false);
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
            .EmbedAsync(chunks.Select(c => c.Text).ToList(), cancellationToken)
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

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
