using Microsoft.Data.Sqlite;

namespace Chimpiler.Kb.Storage;

/// <summary>Owns the single SQLite connection used by the vector and graph stores.</summary>
public sealed class KbDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _initialized;

    public KbDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        }.ToString());
        _connection.Open();
    }

    public string DatabasePath { get; }

    public SqliteConnection Connection => _connection;

    /// <summary>Applies pending schema migrations (idempotent).</summary>
    public void EnsureSchema()
    {
        if (_initialized)
        {
            return;
        }

        SqliteSchema.Apply(_connection);
        _initialized = true;
    }

    public SqliteCommand CreateCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public void Dispose() => _connection.Dispose();
}
