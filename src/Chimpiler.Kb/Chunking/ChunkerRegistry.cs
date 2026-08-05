using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Chunking;

/// <summary>Selects the chunker that handles a given content type, falling back to plain text.</summary>
public sealed class ChunkerRegistry
{
    private readonly IReadOnlyList<IChunker> _chunkers;
    private readonly IChunker _fallback;

    public ChunkerRegistry(IEnumerable<IChunker> chunkers, IChunker fallback)
    {
        _chunkers = chunkers.ToList();
        _fallback = fallback;
    }

    public IReadOnlyList<TextChunk> Chunk(string contentType, string text, ChunkingOptions options)
    {
        var chunker = _chunkers.FirstOrDefault(c => c.CanHandle(contentType)) ?? _fallback;
        return chunker.Chunk(text, options);
    }
}
