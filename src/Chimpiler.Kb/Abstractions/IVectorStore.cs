using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Abstractions;

/// <summary>Persists documents, chunks and their embeddings, and performs nearest-neighbour search.</summary>
public interface IVectorStore
{
    /// <summary>Creates or upgrades the underlying schema.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<long> UpsertDocumentAsync(string sourcePath, string title, string contentHash, string contentType, CancellationToken cancellationToken = default);

    Task<KbDocument?> GetDocumentAsync(string sourcePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KbDocument>> ListDocumentsAsync(CancellationToken cancellationToken = default);

    Task RemoveDocumentAsync(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>Removes all chunks, embeddings and graph nodes belonging to a document.</summary>
    Task ClearChunksAsync(long documentId, CancellationToken cancellationToken = default);

    Task<long> AddChunkAsync(long documentId, int ordinal, string text, string? heading, int tokenCount, float[] embedding, string providerName, CancellationToken cancellationToken = default);

    /// <summary>Returns the <paramref name="topK"/> chunks most similar to <paramref name="queryEmbedding"/>.</summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(float[] queryEmbedding, int topK, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> GetChunksAsync(IReadOnlyCollection<long> chunkIds, CancellationToken cancellationToken = default);

    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);

    Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Reclaims space and refreshes statistics.</summary>
    Task OptimizeAsync(CancellationToken cancellationToken = default);
}
