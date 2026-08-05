namespace Chimpiler.Kb.Abstractions;

/// <summary>Produces dense vector embeddings for text.</summary>
public interface IEmbeddingProvider
{
    /// <summary>Stable provider identifier, persisted alongside embeddings.</summary>
    string Name { get; }

    /// <summary>Dimension of the vectors produced by this provider.</summary>
    int Dimension { get; }

    /// <summary>Embeds a batch of texts, returning one vector per input in the same order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
