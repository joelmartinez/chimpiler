using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Abstractions;

/// <summary>High level façade over the knowledge base; hides storage, embedding and graph details.</summary>
public interface IKnowledgeBase
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces a document from disk. Returns the number of chunks indexed.</summary>
    Task<int> AddDocumentAsync(string path, ChunkingOptions? options = null, CancellationToken cancellationToken = default);

    Task RemoveDocumentAsync(string path, CancellationToken cancellationToken = default);

    Task<int> UpdateDocumentAsync(string path, ChunkingOptions? options = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KbDocument>> ListDocumentsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KbEntity>> ListEntitiesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default);

    /// <summary>Vector search followed by graph expansion of related nodes.</summary>
    Task<IReadOnlyList<SearchResult>> GraphSearchAsync(string query, int topK = 5, int depth = 1, CancellationToken cancellationToken = default);

    /// <summary>Adds an agent-confirmed relationship between previously indexed entity keys.</summary>
    Task AddEntityRelationshipAsync(KbEntityRelationship relationship, CancellationToken cancellationToken = default);

    /// <summary>Re-chunks and re-embeds every known document.</summary>
    Task<int> RebuildAsync(ChunkingOptions? options = null, CancellationToken cancellationToken = default);

    Task OptimizeAsync(CancellationToken cancellationToken = default);
}
