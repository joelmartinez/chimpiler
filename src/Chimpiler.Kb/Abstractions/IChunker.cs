using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Abstractions;

/// <summary>A chunk produced by a chunker, before it is persisted.</summary>
public sealed record TextChunk(int Ordinal, string Text, string? Heading, int TokenCount);

/// <summary>Splits document text into embeddable chunks.</summary>
public interface IChunker
{
    /// <summary>True when this chunker knows how to handle the given content type (e.g. "markdown").</summary>
    bool CanHandle(string contentType);

    IReadOnlyList<TextChunk> Chunk(string text, ChunkingOptions options);
}
