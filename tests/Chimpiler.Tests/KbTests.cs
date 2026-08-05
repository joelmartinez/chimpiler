using Chimpiler.Kb;
using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Chunking;
using Chimpiler.Kb.Embeddings;
using Chimpiler.Kb.Models;
using Chimpiler.Kb.Storage;

namespace Chimpiler.Tests;

public class KbTests : IDisposable
{
    private readonly string _directory;

    public KbTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "chimpiler-kb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private KbDatabase CreateDatabase()
    {
        var database = new KbDatabase(Path.Combine(_directory, "kb.db"));
        database.EnsureSchema();
        return database;
    }

    private static KnowledgeBase CreateKnowledgeBase(KbDatabase database, IEmbeddingProvider? provider = null)
    {
        var tokenizer = new WhitespaceTokenizer();
        return new KnowledgeBase(
            new SqliteVectorStore(database),
            new SqliteGraphStore(database),
            provider ?? new HashEmbeddingProvider(),
            KnowledgeBaseFactory.CreateRegistry(tokenizer));
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Schema_CreatesAllTables_AndRecordsMigrations()
    {
        using var database = CreateDatabase();

        using var command = database.CreateCommand("SELECT name FROM sqlite_master WHERE type = 'table';");
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        foreach (var expected in new[] { "Documents", "Chunks", "Embeddings", "Nodes", "Edges", "NodeMetadata", "Settings", "MigrationHistory" })
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public void Schema_Apply_IsIdempotent()
    {
        using var database = CreateDatabase();
        SqliteSchema.Apply(database.Connection);
        SqliteSchema.Apply(database.Connection);

        using var command = database.CreateCommand("SELECT COUNT(*) FROM MigrationHistory;");
        Assert.Equal(SqliteSchema.Migrations.Count, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void VectorCodec_RoundTripsVectors()
    {
        var vector = new[] { 0.5f, -1.25f, 3f };
        var decoded = VectorCodec.Decode(VectorCodec.Encode(vector), vector.Length);
        Assert.Equal(vector, decoded);
    }

    [Fact]
    public void VectorCodec_CosineSimilarity_IsOneForIdenticalVectors()
    {
        var vector = new[] { 1f, 2f, 3f };
        var norm = VectorCodec.Norm(vector);
        Assert.Equal(1.0, VectorCodec.CosineSimilarity(vector, norm, vector, norm), 6);
    }

    [Fact]
    public void VectorCodec_CosineSimilarity_ReturnsZeroForDegenerateVector()
    {
        var vector = new[] { 1f, 0f };
        var zero = new[] { 0f, 0f };
        Assert.Equal(0.0, VectorCodec.CosineSimilarity(vector, VectorCodec.Norm(vector), zero, VectorCodec.Norm(zero)));
    }

    [Fact]
    public async Task HashEmbeddingProvider_IsDeterministicAndFixedSize()
    {
        var provider = new HashEmbeddingProvider(64);
        var first = await provider.EmbedAsync(new[] { "hello world" });
        var second = await provider.EmbedAsync(new[] { "hello world" });

        Assert.Equal(64, first[0].Length);
        Assert.Equal(first[0], second[0]);
    }

    [Fact]
    public void MarkdownChunker_SplitsOnHeadings()
    {
        var chunker = new MarkdownChunker(new WhitespaceTokenizer());
        var chunks = chunker.Chunk("# One\n\nalpha text\n\n# Two\n\nbeta text\n", ChunkingOptions.Default);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("One", chunks[0].Heading);
        Assert.Equal("Two", chunks[1].Heading);
        Assert.Equal(new[] { 0, 1 }, chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void Chunker_RespectsMaxTokenBudget()
    {
        var tokenizer = new WhitespaceTokenizer();
        var chunker = new TextChunker(tokenizer);
        var text = string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"word{i} word{i}b word{i}c"));

        var chunks = chunker.Chunk(text, new ChunkingOptions { MaxTokens = 6, OverlapTokens = 0 });

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(tokenizer.CountTokens(chunk.Text) <= 6));
    }

    [Fact]
    public void Chunker_SplitsSingleOversizedParagraph()
    {
        var tokenizer = new WhitespaceTokenizer();
        var chunker = new TextChunker(tokenizer);
        var text = string.Join(' ', Enumerable.Range(0, 50).Select(i => $"w{i}"));

        var chunks = chunker.Chunk(text, new ChunkingOptions { MaxTokens = 10, OverlapTokens = 0 });

        Assert.True(chunks.Count >= 5);
        Assert.All(chunks, chunk => Assert.True(tokenizer.CountTokens(chunk.Text) <= 10));
    }

    [Fact]
    public void CodeChunker_UsesDeclarationAsHeading()
    {
        var chunker = new CodeChunker(new WhitespaceTokenizer());
        var chunks = chunker.Chunk("public class Widget\n{\n    int x;\n}\n", ChunkingOptions.Default);

        Assert.Single(chunks);
        Assert.Equal("public class Widget", chunks[0].Heading);
    }

    [Fact]
    public void ContentTypes_AreInferredFromExtension()
    {
        Assert.Equal(ContentTypes.Markdown, ContentTypes.FromPath("a/b.md"));
        Assert.Equal(ContentTypes.Code, ContentTypes.FromPath("a/b.cs"));
        Assert.Equal(ContentTypes.Text, ContentTypes.FromPath("a/b.log"));
    }

    [Fact]
    public async Task AddDocument_IndexesChunksAndIsIdempotent()
    {
        using var database = CreateDatabase();
        var kb = CreateKnowledgeBase(database);
        await kb.InitializeAsync();

        var path = WriteFile("doc.md", "# Alpha\n\nalpha content here\n\n# Beta\n\nbeta content here\n");

        var first = await kb.AddDocumentAsync(path);
        var second = await kb.AddDocumentAsync(path);

        Assert.Equal(2, first);
        Assert.Equal(2, second);

        var documents = await kb.ListDocumentsAsync();
        Assert.Single(documents);

        using var command = database.CreateCommand("SELECT COUNT(*) FROM Chunks;");
        Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public async Task RemoveDocument_DeletesChunksAndEmbeddings()
    {
        using var database = CreateDatabase();
        var kb = CreateKnowledgeBase(database);
        await kb.InitializeAsync();

        var path = WriteFile("doc.md", "# Alpha\n\nalpha content here\n");
        await kb.AddDocumentAsync(path);
        await kb.RemoveDocumentAsync(path);

        Assert.Empty(await kb.ListDocumentsAsync());

        using var chunks = database.CreateCommand("SELECT COUNT(*) FROM Chunks;");
        Assert.Equal(0, Convert.ToInt32(chunks.ExecuteScalar()));

        using var embeddings = database.CreateCommand("SELECT COUNT(*) FROM Embeddings;");
        Assert.Equal(0, Convert.ToInt32(embeddings.ExecuteScalar()));
    }

    [Fact]
    public async Task Search_RanksTheMostRelevantChunkFirst()
    {
        using var database = CreateDatabase();
        var kb = CreateKnowledgeBase(database);
        await kb.InitializeAsync();

        await kb.AddDocumentAsync(WriteFile("cats.txt", "cats purr and knead blankets"));
        await kb.AddDocumentAsync(WriteFile("db.txt", "sqlite stores vectors as blobs"));

        var results = await kb.SearchAsync("sqlite vectors blobs", topK: 2);

        Assert.NotEmpty(results);
        Assert.EndsWith("db.txt", results[0].SourcePath);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task Search_RespectsTopK()
    {
        using var database = CreateDatabase();
        var kb = CreateKnowledgeBase(database);
        await kb.InitializeAsync();

        await kb.AddDocumentAsync(WriteFile("a.md", "# A\n\none\n\n# B\n\ntwo\n\n# C\n\nthree\n"));

        Assert.Single(await kb.SearchAsync("one", topK: 1));
    }

    [Fact]
    public async Task GraphSearch_PullsInRelatedChunks()
    {
        using var database = CreateDatabase();
        var kb = CreateKnowledgeBase(database);
        await kb.InitializeAsync();

        await kb.AddDocumentAsync(WriteFile("doc.md",
            "# Storage\n\nsqlite stores vectors as blobs\n\n# Graph\n\nnodes and edges describe relationships\n"));

        var plain = await kb.SearchAsync("sqlite blobs", topK: 1);
        var graph = await kb.GraphSearchAsync("sqlite blobs", topK: 1, depth: 2);

        Assert.Single(plain);
        Assert.True(graph.Count > plain.Count);
        Assert.Contains(graph, r => r.FromGraphExpansion);
        // Graph neighbours must never outrank the direct vector hit.
        Assert.False(graph[0].FromGraphExpansion);
    }

    [Fact]
    public async Task GraphSearch_OnEmptyKnowledgeBase_ReturnsNoResults()
    {
        using var database = CreateDatabase();
        var kb = CreateKnowledgeBase(database);
        await kb.InitializeAsync();

        Assert.Empty(await kb.GraphSearchAsync("anything"));
    }

    [Fact]
    public async Task Rebuild_DropsDocumentsWhoseFilesAreGone()
    {
        using var database = CreateDatabase();
        var kb = CreateKnowledgeBase(database);
        await kb.InitializeAsync();

        var keep = WriteFile("keep.txt", "kept content");
        var remove = WriteFile("gone.txt", "temporary content");
        await kb.AddDocumentAsync(keep);
        await kb.AddDocumentAsync(remove);

        File.Delete(remove);
        await kb.RebuildAsync();

        var documents = await kb.ListDocumentsAsync();
        Assert.Single(documents);
        Assert.Equal(Path.GetFullPath(keep), documents[0].SourcePath);
    }

    [Fact]
    public async Task Initialize_RecordsProviderSettings()
    {
        using var database = CreateDatabase();
        var store = new SqliteVectorStore(database);
        var kb = new KnowledgeBase(store, new SqliteGraphStore(database), new HashEmbeddingProvider(128), KnowledgeBaseFactory.CreateRegistry(new WhitespaceTokenizer()));
        await kb.InitializeAsync();

        Assert.Equal("hash", await store.GetSettingAsync("embedding.provider"));
        Assert.Equal("128", await store.GetSettingAsync("embedding.dimension"));
    }

    [Fact]
    public async Task Optimize_Succeeds()
    {
        using var database = CreateDatabase();
        var kb = CreateKnowledgeBase(database);
        await kb.InitializeAsync();
        await kb.AddDocumentAsync(WriteFile("a.txt", "some content"));

        await kb.OptimizeAsync();
    }

    [Fact]
    public async Task AddDocument_ThrowsForMissingFile()
    {
        using var database = CreateDatabase();
        var kb = CreateKnowledgeBase(database);
        await kb.InitializeAsync();

        await Assert.ThrowsAsync<FileNotFoundException>(() => kb.AddDocumentAsync(Path.Combine(_directory, "nope.txt")));
    }

    [Fact]
    public void ModelCatalog_ResolvesDefaultAndRejectsUnknownModels()
    {
        var catalog = new HuggingFaceModelCatalog(Path.Combine(_directory, "models"));

        Assert.Equal(HuggingFaceModelCatalog.BgeSmallEn, catalog.Resolve("default").Id);
        Assert.False(catalog.IsInstalled("default"));
        Assert.Contains(catalog.Available, m => m.Id == HuggingFaceModelCatalog.NomicEmbedText);
        Assert.Throws<ArgumentException>(() => catalog.Resolve("not-a-model"));
    }

    [Fact]
    public void ModelCatalog_RemoveIsSafeWhenNotInstalled()
    {
        var catalog = new HuggingFaceModelCatalog(Path.Combine(_directory, "models"));
        catalog.Remove("default");
    }
}
