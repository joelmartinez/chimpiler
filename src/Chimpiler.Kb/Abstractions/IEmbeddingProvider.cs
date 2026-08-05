namespace Chimpiler.Kb.Abstractions;

/// <summary>Produces dense vector embeddings for text.</summary>
public interface IEmbeddingProvider
{
    /// <summary>Stable provider identifier, persisted alongside embeddings.</summary>
    string Name { get; }

    /// <summary>Dimension of the vectors produced by this provider.</summary>
    int Dimension { get; }

    /// <summary>The exact tokenizer used by this provider's model.</summary>
    IKbTokenizer Tokenizer { get; }

    /// <summary>Embeds corpus text, returning one vector per input in the same order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);

    /// <summary>Embeds corpus text using the provider's document representation.</summary>
    Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);

    /// <summary>Embeds search text using the provider's query representation.</summary>
    Task<IReadOnlyList<float[]>> EmbedQueriesAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
