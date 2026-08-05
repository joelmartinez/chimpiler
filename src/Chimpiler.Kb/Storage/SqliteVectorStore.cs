using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Models;
using Microsoft.Data.Sqlite;

namespace Chimpiler.Kb.Storage;

/// <summary>SQLite-backed vector store. Vectors are stored as float32 blobs and scored with cosine similarity.</summary>
public sealed class SqliteVectorStore : IVectorStore
{
    private readonly KbDatabase _database;

    public SqliteVectorStore(KbDatabase database) => _database = database;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _database.EnsureSchema();
        return Task.CompletedTask;
    }

    public async Task<long> UpsertDocumentAsync(string sourcePath, string title, string contentHash, string contentType, CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand("""
            INSERT INTO Documents (SourcePath, Title, ContentHash, ContentType, UpdatedUtc)
            VALUES ($path, $title, $hash, $type, $utc)
            ON CONFLICT(SourcePath) DO UPDATE SET
                Title = excluded.Title,
                ContentHash = excluded.ContentHash,
                ContentType = excluded.ContentType,
                UpdatedUtc = excluded.UpdatedUtc
            RETURNING Id;
            """);
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$hash", contentHash);
        command.Parameters.AddWithValue("$type", contentType);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    public async Task<KbDocument?> GetDocumentAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand(
            "SELECT Id, SourcePath, Title, ContentHash, ContentType, UpdatedUtc FROM Documents WHERE SourcePath = $path;");
        command.Parameters.AddWithValue("$path", sourcePath);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDocument(reader) : null;
    }

    public async Task<IReadOnlyList<KbDocument>> ListDocumentsAsync(CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand(
            "SELECT Id, SourcePath, Title, ContentHash, ContentType, UpdatedUtc FROM Documents ORDER BY SourcePath;");

        var documents = new List<KbDocument>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(ReadDocument(reader));
        }

        return documents;
    }

    public async Task RemoveDocumentAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand("DELETE FROM Documents WHERE SourcePath = $path;");
        command.Parameters.AddWithValue("$path", sourcePath);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearChunksAsync(long documentId, CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand("DELETE FROM Chunks WHERE DocumentId = $id;");
        command.Parameters.AddWithValue("$id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> AddChunkAsync(long documentId, int ordinal, string text, string? heading, int tokenCount, float[] embedding, string providerName, CancellationToken cancellationToken = default)
    {
        long chunkId;
        using (var command = _database.CreateCommand("""
            INSERT INTO Chunks (DocumentId, Ordinal, Text, Heading, TokenCount)
            VALUES ($doc, $ordinal, $text, $heading, $tokens)
            RETURNING Id;
            """))
        {
            command.Parameters.AddWithValue("$doc", documentId);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$heading", (object?)heading ?? DBNull.Value);
            command.Parameters.AddWithValue("$tokens", tokenCount);
            chunkId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        using (var command = _database.CreateCommand("""
            INSERT INTO Embeddings (ChunkId, Provider, Dimension, Vector, Norm)
            VALUES ($chunk, $provider, $dim, $vector, $norm);
            """))
        {
            command.Parameters.AddWithValue("$chunk", chunkId);
            command.Parameters.AddWithValue("$provider", providerName);
            command.Parameters.AddWithValue("$dim", embedding.Length);
            command.Parameters.AddWithValue("$vector", VectorCodec.Encode(embedding));
            command.Parameters.AddWithValue("$norm", VectorCodec.Norm(embedding));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return chunkId;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(float[] queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        if (topK <= 0)
        {
            return Array.Empty<SearchResult>();
        }

        var queryNorm = VectorCodec.Norm(queryEmbedding);
        var results = new List<SearchResult>();

        using var command = _database.CreateCommand("""
            SELECT c.Id, c.DocumentId, d.SourcePath, c.Text, c.Heading, e.Vector, e.Norm, e.Dimension
            FROM Embeddings e
            JOIN Chunks c ON c.Id = e.ChunkId
            JOIN Documents d ON d.Id = c.DocumentId;
            """);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dimension = reader.GetInt32(7);
            if (dimension != queryEmbedding.Length)
            {
                continue;
            }

            var vector = VectorCodec.Decode((byte[])reader["Vector"], dimension);
            var score = VectorCodec.CosineSimilarity(queryEmbedding, queryNorm, vector, reader.GetDouble(6));

            results.Add(new SearchResult
            {
                ChunkId = reader.GetInt64(0),
                DocumentId = reader.GetInt64(1),
                SourcePath = reader.GetString(2),
                Text = reader.GetString(3),
                Heading = reader.IsDBNull(4) ? null : reader.GetString(4),
                Score = score
            });
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    public async Task<IReadOnlyList<SearchResult>> GetChunksAsync(IReadOnlyCollection<long> chunkIds, CancellationToken cancellationToken = default)
    {
        if (chunkIds.Count == 0)
        {
            return Array.Empty<SearchResult>();
        }

        var parameterNames = chunkIds.Select((_, index) => $"$id{index}").ToArray();
        using var command = _database.CreateCommand($"""
            SELECT c.Id, c.DocumentId, d.SourcePath, c.Text, c.Heading
            FROM Chunks c
            JOIN Documents d ON d.Id = c.DocumentId
            WHERE c.Id IN ({string.Join(", ", parameterNames)});
            """);

        var i = 0;
        foreach (var id in chunkIds)
        {
            command.Parameters.AddWithValue(parameterNames[i++], id);
        }

        var results = new List<SearchResult>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SearchResult
            {
                ChunkId = reader.GetInt64(0),
                DocumentId = reader.GetInt64(1),
                SourcePath = reader.GetString(2),
                Text = reader.GetString(3),
                Heading = reader.IsDBNull(4) ? null : reader.GetString(4),
                Score = 0,
                FromGraphExpansion = true
            });
        }

        return results;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand("SELECT Value FROM Settings WHERE Key = $key;");
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand("""
            INSERT INTO Settings (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        using var analyze = _database.CreateCommand("ANALYZE;");
        await analyze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using var vacuum = _database.CreateCommand("VACUUM;");
        await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static KbDocument ReadDocument(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5)));
}
