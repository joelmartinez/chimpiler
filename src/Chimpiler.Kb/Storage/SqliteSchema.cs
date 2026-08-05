using Microsoft.Data.Sqlite;

namespace Chimpiler.Kb.Storage;

/// <summary>Versioned SQLite schema, applied incrementally and recorded in MigrationHistory.</summary>
public static class SqliteSchema
{
    /// <summary>Ordered list of migrations; append new entries, never edit existing ones.</summary>
    public static readonly IReadOnlyList<(string Id, string Sql)> Migrations = new List<(string, string)>
    {
        ("0001_initial", """
            CREATE TABLE IF NOT EXISTS Documents (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                SourcePath   TEXT NOT NULL UNIQUE,
                Title        TEXT NOT NULL,
                ContentHash  TEXT NOT NULL,
                ContentType  TEXT NOT NULL,
                UpdatedUtc   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Chunks (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                DocumentId INTEGER NOT NULL REFERENCES Documents(Id) ON DELETE CASCADE,
                Ordinal    INTEGER NOT NULL,
                Text       TEXT NOT NULL,
                Heading    TEXT NULL,
                TokenCount INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Chunks_DocumentId ON Chunks(DocumentId);

            CREATE TABLE IF NOT EXISTS Embeddings (
                ChunkId   INTEGER PRIMARY KEY REFERENCES Chunks(Id) ON DELETE CASCADE,
                Provider  TEXT NOT NULL,
                Dimension INTEGER NOT NULL,
                Vector    BLOB NOT NULL,
                Norm      REAL NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Nodes (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                Kind       TEXT NOT NULL,
                Key        TEXT NOT NULL,
                ChunkId    INTEGER NULL REFERENCES Chunks(Id) ON DELETE CASCADE,
                DocumentId INTEGER NULL REFERENCES Documents(Id) ON DELETE CASCADE,
                UNIQUE(Kind, Key)
            );

            CREATE TABLE IF NOT EXISTS Edges (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceNodeId INTEGER NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,
                TargetNodeId INTEGER NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,
                Kind         TEXT NOT NULL,
                Weight       REAL NOT NULL DEFAULT 1.0,
                UNIQUE(SourceNodeId, TargetNodeId, Kind)
            );
            CREATE INDEX IF NOT EXISTS IX_Edges_Source ON Edges(SourceNodeId);
            CREATE INDEX IF NOT EXISTS IX_Edges_Target ON Edges(TargetNodeId);

            CREATE TABLE IF NOT EXISTS NodeMetadata (
                NodeId INTEGER NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,
                Key    TEXT NOT NULL,
                Value  TEXT NOT NULL,
                PRIMARY KEY (NodeId, Key)
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """)
    };

    /// <summary>Applies any migrations that have not yet been recorded in the database.</summary>
    public static void Apply(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS MigrationHistory (
                    Id        TEXT PRIMARY KEY,
                    AppliedUtc TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM MigrationHistory;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetString(0));
            }
        }

        foreach (var (id, sql) in Migrations)
        {
            if (applied.Contains(id))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO MigrationHistory (Id, AppliedUtc) VALUES ($id, $utc);";
                record.Parameters.AddWithValue("$id", id);
                record.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
