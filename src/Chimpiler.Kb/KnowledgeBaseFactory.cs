using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Chunking;
using Chimpiler.Kb.Embeddings;
using Chimpiler.Kb.EntityExtraction;
using Chimpiler.Kb.Models;
using Chimpiler.Kb.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Chimpiler.Kb;

/// <summary>Options describing how to open a knowledge base.</summary>
public sealed record KnowledgeBaseOptions
{
    /// <summary>Default database location relative to the working directory.</summary>
    public const string DefaultDatabaseFile = ".chimpiler/kb.db";

    public string DatabasePath { get; init; } = DefaultDatabaseFile;

    /// <summary>Model id to use, or "default". When null, no ONNX model is loaded.</summary>
    public string? ModelId { get; init; }

    /// <summary>When true, downloads the model on first use if it is missing.</summary>
    public bool AutoInstallModel { get; init; } = true;

    /// <summary>Allows rebuild to replace a knowledge base's embedding provider.</summary>
    public bool AllowEmbeddingMismatch { get; init; }

    public ChunkingOptions Chunking { get; init; } = ChunkingOptions.Default;
}

/// <summary>Wires up the knowledge base graph of services.</summary>
public static class KnowledgeBaseFactory
{
    /// <summary>Registers knowledge base services against the supplied options.</summary>
    public static IServiceCollection AddKnowledgeBase(this IServiceCollection services, KnowledgeBaseOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(_ => new KbDatabase(options.DatabasePath));
        services.AddSingleton<IVectorStore>(sp => new SqliteVectorStore(sp.GetRequiredService<KbDatabase>()));
        services.AddSingleton<IGraphStore>(sp => new SqliteGraphStore(sp.GetRequiredService<KbDatabase>()));
        services.AddSingleton<IEntityExtractor, RuleBasedEntityExtractor>();
        services.AddSingleton<IEntityRelationshipExtractor, RuleBasedEntityRelationshipExtractor>();
        services.AddSingleton<IEmbeddingModelCatalog>(_ => new HuggingFaceModelCatalog());
        services.AddSingleton<IEmbeddingProvider>(sp => CreateProvider(
            (HuggingFaceModelCatalog)sp.GetRequiredService<IEmbeddingModelCatalog>(),
            options));
        services.AddSingleton<IKbTokenizer>(sp => sp.GetRequiredService<IEmbeddingProvider>().Tokenizer);
        services.AddSingleton(sp => CreateRegistry(sp.GetRequiredService<IKbTokenizer>()));
        services.AddSingleton<IKnowledgeBase>(sp => new KnowledgeBase(
            sp.GetRequiredService<IVectorStore>(),
            sp.GetRequiredService<IGraphStore>(),
            sp.GetRequiredService<IEmbeddingProvider>(),
            sp.GetRequiredService<ChunkerRegistry>(),
            options.AllowEmbeddingMismatch,
            sp.GetRequiredService<IEntityExtractor>(),
            sp.GetRequiredService<IEntityRelationshipExtractor>()));

        return services;
    }

    /// <summary>Builds a ready-to-use provider for callers that do not want a DI container.</summary>
    public static ServiceProvider Build(KnowledgeBaseOptions options) =>
        new ServiceCollection().AddKnowledgeBase(options).BuildServiceProvider();

    /// <summary>Creates the default chunker registry (markdown, code, plain text).</summary>
    public static ChunkerRegistry CreateRegistry(IKbTokenizer tokenizer)
    {
        var text = new TextChunker(tokenizer);
        return new ChunkerRegistry(
            new IChunker[] { new MarkdownChunker(tokenizer), new CodeChunker(tokenizer), text },
            text);
    }

    private static IEmbeddingProvider CreateProvider(HuggingFaceModelCatalog catalog, KnowledgeBaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModelId))
        {
            return new HashEmbeddingProvider();
        }

        var descriptor = catalog.Resolve(options.ModelId);
        if (!catalog.IsInstalled(descriptor.Id))
        {
            if (!options.AutoInstallModel)
            {
                throw new InvalidOperationException(
                    $"Embedding model '{descriptor.Id}' is not installed. Run 'chimpiler kb models install {descriptor.Id}'.");
            }

            // Run on the thread pool so the blocking wait cannot deadlock on a captured synchronization context.
            Task.Run(() => catalog.EnsureInstalledAsync(descriptor.Id)).GetAwaiter().GetResult();
        }

        return new OnnxEmbeddingProvider(
            descriptor,
            catalog.GetModelPath(descriptor.Id),
            catalog.GetVocabPath(descriptor.Id));
    }
}
