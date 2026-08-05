using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Chunking;

/// <summary>Splits plain text on paragraph boundaries, respecting a token budget and overlap.</summary>
public sealed class TextChunker : IChunker
{
    private readonly IKbTokenizer _tokenizer;

    public TextChunker(IKbTokenizer tokenizer) => _tokenizer = tokenizer;

    public bool CanHandle(string contentType) => contentType == ContentTypes.Text;

    public IReadOnlyList<TextChunk> Chunk(string text, ChunkingOptions options)
        => ChunkHelper.Pack(SplitParagraphs(text), heading: null, options, _tokenizer, startOrdinal: 0);

    internal static IReadOnlyList<string> SplitParagraphs(string text) =>
        text.Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
