namespace Chimpiler.Kb.Models;

/// <summary>A source document tracked by the knowledge base.</summary>
public sealed record KbDocument(
    long Id,
    string SourcePath,
    string Title,
    string ContentHash,
    string ContentType,
    DateTimeOffset UpdatedUtc);

/// <summary>A chunk of a document that is individually embedded and searchable.</summary>
public sealed record KbChunk(
    long Id,
    long DocumentId,
    int Ordinal,
    string Text,
    string? Heading,
    int TokenCount);

/// <summary>A stored chunk together with its embedding, used to construct semantic graph edges.</summary>
public sealed record EmbeddedKbChunk(long Id, long DocumentId, string Text, float[] Embedding);

/// <summary>Kinds of graph nodes stored in the knowledge base.</summary>
public static class NodeKinds
{
    public const string Document = "document";
    public const string Chunk = "chunk";
    public const string Section = "section";
    public const string Symbol = "symbol";
    public const string Type = "type";
}

/// <summary>Kinds of graph edges stored in the knowledge base.</summary>
public static class EdgeKinds
{
    public const string Contains = "contains";
    public const string Parent = "parent";
    public const string Child = "child";
    public const string References = "references";
    public const string Section = "section";
    public const string Semantic = "semantic";
    public const string Symbol = "symbol";
    public const string Type = "type";
}

/// <summary>A node in the knowledge graph.</summary>
public sealed record KbNode(long Id, string Kind, string Key, long? ChunkId, long? DocumentId);

/// <summary>An edge in the knowledge graph.</summary>
public sealed record KbEdge(long Id, long SourceNodeId, long TargetNodeId, string Kind, double Weight);

/// <summary>A single search hit.</summary>
public sealed record SearchResult
{
    public required long ChunkId { get; init; }
    public required long DocumentId { get; init; }
    public required string SourcePath { get; init; }
    public required string Text { get; init; }
    public string? Heading { get; init; }
    public required double Score { get; init; }
    /// <summary>True when the hit was pulled in by graph expansion rather than vector similarity.</summary>
    public bool FromGraphExpansion { get; init; }
}

/// <summary>Options controlling how documents are split into chunks.</summary>
public sealed record ChunkingOptions
{
    public int MaxTokens { get; init; } = 256;
    public int OverlapTokens { get; init; } = 32;

    public static ChunkingOptions Default { get; } = new();
}
