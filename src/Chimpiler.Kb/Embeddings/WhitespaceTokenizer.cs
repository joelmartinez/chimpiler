using Chimpiler.Kb.Abstractions;

namespace Chimpiler.Kb.Embeddings;

/// <summary>Dependency-free approximate tokenizer used for chunking when no model vocabulary is installed.</summary>
public sealed class WhitespaceTokenizer : IKbTokenizer
{
    private static readonly char[] Separators = { ' ', '\t', '\n', '\r' };

    public int CountTokens(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Length;

    public IReadOnlyList<int> Encode(string text, int maxTokens) =>
        text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Take(maxTokens)
            .Select(word => word.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF)
            .ToList();
}
