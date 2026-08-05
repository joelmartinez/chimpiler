using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Chunking;

/// <summary>Packs text fragments into token-bounded chunks with configurable overlap.</summary>
internal static class ChunkHelper
{
    public static IReadOnlyList<TextChunk> Pack(
        IReadOnlyList<string> fragments,
        string? heading,
        ChunkingOptions options,
        IKbTokenizer tokenizer,
        int startOrdinal)
    {
        var maxTokens = Math.Max(1, options.MaxTokens);
        var overlap = Math.Clamp(options.OverlapTokens, 0, maxTokens - 1);

        var chunks = new List<TextChunk>();
        var current = new List<string>();
        var currentTokens = 0;

        foreach (var fragment in fragments)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                continue;
            }

            foreach (var piece in SplitOversized(fragment, maxTokens, tokenizer))
            {
                var pieceTokens = tokenizer.CountTokens(piece);
                if (current.Count > 0 && currentTokens + pieceTokens > maxTokens)
                {
                    chunks.Add(Create(chunks.Count + startOrdinal, current, heading, tokenizer));
                    (current, currentTokens) = CarryOver(current, overlap, tokenizer);
                }

                current.Add(piece);
                currentTokens += pieceTokens;
            }
        }

        if (current.Count > 0)
        {
            chunks.Add(Create(chunks.Count + startOrdinal, current, heading, tokenizer));
        }

        return chunks;
    }

    private static TextChunk Create(int ordinal, List<string> parts, string? heading, IKbTokenizer tokenizer)
    {
        var text = string.Join("\n\n", parts).Trim();
        return new TextChunk(ordinal, text, heading, tokenizer.CountTokens(text));
    }

    private static (List<string> Parts, int Tokens) CarryOver(List<string> parts, int overlap, IKbTokenizer tokenizer)
    {
        var carried = new List<string>();
        var tokens = 0;

        for (var i = parts.Count - 1; i >= 0 && tokens < overlap; i--)
        {
            var partTokens = tokenizer.CountTokens(parts[i]);
            if (tokens + partTokens > overlap)
            {
                break;
            }

            carried.Insert(0, parts[i]);
            tokens += partTokens;
        }

        return (carried, tokens);
    }

    /// <summary>Breaks a fragment that exceeds the token budget into word-aligned pieces.</summary>
    private static IEnumerable<string> SplitOversized(string fragment, int maxTokens, IKbTokenizer tokenizer)
    {
        if (tokenizer.CountTokens(fragment) <= maxTokens)
        {
            yield return fragment;
            yield break;
        }

        var words = fragment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var buffer = new List<string>();
        var tokens = 0;

        foreach (var word in words)
        {
            var wordTokens = Math.Max(1, tokenizer.CountTokens(word));
            if (buffer.Count > 0 && tokens + wordTokens > maxTokens)
            {
                yield return string.Join(' ', buffer);
                buffer.Clear();
                tokens = 0;
            }

            buffer.Add(word);
            tokens += wordTokens;
        }

        if (buffer.Count > 0)
        {
            yield return string.Join(' ', buffer);
        }
    }
}
