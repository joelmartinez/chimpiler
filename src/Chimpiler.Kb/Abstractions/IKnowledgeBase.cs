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

    /// <summary>Registers an agent-verified entity mention tied to evidence in an indexed source.</summary>
    Task RegisterEntityAsync(KbEntityMention entity, string evidence, string sourcePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default);

    /// <summary>Vector search followed by bounded expansion through agent-authored evidence edges.</summary>
    Task<IReadOnlyList<SearchResult>> GraphSearchAsync(string query, int topK = 5, int depth = 1, CancellationToken cancellationToken = default);

    /// <summary>Adds an agent-confirmed relationship between previously registered entity keys.</summary>
    Task AddEntityRelationshipAsync(KbEntityRelationship relationship, CancellationToken cancellationToken = default);

    /// <summary>Re-chunks and re-embeds every known document.</summary>
    Task<int> RebuildAsync(ChunkingOptions? options = null, CancellationToken cancellationToken = default);

    Task OptimizeAsync(CancellationToken cancellationToken = default);
}
