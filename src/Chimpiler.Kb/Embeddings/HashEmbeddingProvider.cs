using Chimpiler.Kb.Abstractions;

namespace Chimpiler.Kb.Embeddings;

/// <summary>
/// Deterministic offline fallback provider that hashes token n-grams into a fixed-size vector.
/// Used when no ONNX model is installed, and by tests; quality is far below a real model but it
/// keeps the whole pipeline usable with zero downloads.
/// </summary>
public sealed class HashEmbeddingProvider : IEmbeddingProvider
{
    private static readonly char[] Separators = { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '#', '-' };

    public HashEmbeddingProvider(int dimension = 256)
    {
        if (dimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        Dimension = dimension;
    }

    public string Name => "hash";

    public int Dimension { get; }

    public IKbTokenizer Tokenizer { get; } = new WhitespaceTokenizer();

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        => EmbedDocumentsAsync(texts, cancellationToken);

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        EmbedAsyncCore(texts, cancellationToken);

    public Task<IReadOnlyList<float[]>> EmbedQueriesAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        EmbedAsyncCore(texts, cancellationToken);

    private Task<IReadOnlyList<float[]>> EmbedAsyncCore(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var vectors = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vectors.Add(Embed(text));
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    private float[] Embed(string text)
    {
        var vector = new float[Dimension];
        var words = text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            vector[Bucket(word)] += 1f;
        }

        for (var i = 1; i < words.Length; i++)
        {
            vector[Bucket(words[i - 1] + "_" + words[i])] += 0.5f;
        }

        return vector;
    }

    private int Bucket(string token)
    {
        // FNV-1a keeps bucketing stable across processes, unlike string.GetHashCode.
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in token)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return (int)(hash % (uint)Dimension);
        }
    }
}
