using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Chunking;

/// <summary>Splits markdown on headings first, then packs paragraphs within each section.</summary>
public sealed class MarkdownChunker : IChunker
{
    private readonly IKbTokenizer _tokenizer;

    public MarkdownChunker(IKbTokenizer tokenizer) => _tokenizer = tokenizer;

    public bool CanHandle(string contentType) => contentType == ContentTypes.Markdown;

    public IReadOnlyList<TextChunk> Chunk(string text, ChunkingOptions options)
    {
        var chunks = new List<TextChunk>();
        foreach (var (heading, body) in SplitSections(text))
        {
            var paragraphs = TextChunker.SplitParagraphs(body);
            if (paragraphs.Count == 0)
            {
                continue;
            }

            chunks.AddRange(ChunkHelper.Pack(paragraphs, heading, options, _tokenizer, chunks.Count));
        }

        return chunks;
    }

    private static IReadOnlyList<(string? Heading, string Body)> SplitSections(string text)
    {
        var sections = new List<(string?, string)>();
        string? currentHeading = null;
        var builder = new System.Text.StringBuilder();

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith('#'))
            {
                if (builder.Length > 0)
                {
                    sections.Add((currentHeading, builder.ToString()));
                    builder.Clear();
                }

                currentHeading = line.TrimStart('#').Trim();
                continue;
            }

            builder.Append(line).Append('\n');
        }

        if (builder.Length > 0)
        {
            sections.Add((currentHeading, builder.ToString()));
        }

        return sections;
    }
}
