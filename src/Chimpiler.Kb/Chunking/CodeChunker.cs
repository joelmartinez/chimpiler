using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Chunking;

/// <summary>Splits source code on blank-line separated blocks, keeping the enclosing declaration as the heading.</summary>
public sealed class CodeChunker : IChunker
{
    private static readonly string[] DeclarationKeywords =
    {
        "class ", "interface ", "struct ", "record ", "enum ", "namespace ",
        "def ", "func ", "function ", "fn ", "public ", "private ", "internal ", "protected "
    };

    private readonly IKbTokenizer _tokenizer;

    public CodeChunker(IKbTokenizer tokenizer) => _tokenizer = tokenizer;

    public bool CanHandle(string contentType) => contentType == ContentTypes.Code;

    public IReadOnlyList<TextChunk> Chunk(string text, ChunkingOptions options)
    {
        var blocks = TextChunker.SplitParagraphs(text);
        var chunks = new List<TextChunk>();

        foreach (var block in blocks)
        {
            var heading = FindDeclaration(block);
            chunks.AddRange(ChunkHelper.Pack(new[] { block }, heading, options, _tokenizer, chunks.Count));
        }

        return chunks;
    }

    private static string? FindDeclaration(string block)
    {
        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.Trim();
            if (DeclarationKeywords.Any(keyword => trimmed.StartsWith(keyword, StringComparison.Ordinal)))
            {
                return trimmed.TrimEnd('{', ' ', ':');
            }
        }

        return null;
    }
}
