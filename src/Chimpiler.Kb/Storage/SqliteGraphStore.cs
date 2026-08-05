using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Models;

namespace Chimpiler.Kb.Storage;

/// <summary>SQLite-backed knowledge graph. Traversal loads the relevant edges and walks them in memory.</summary>
public sealed class SqliteGraphStore : IGraphStore
{
    private readonly KbDatabase _database;

    public SqliteGraphStore(KbDatabase database) => _database = database;

    public async Task<long> UpsertNodeAsync(string kind, string key, long? chunkId, long? documentId, CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand("""
            INSERT INTO Nodes (Kind, Key, ChunkId, DocumentId)
            VALUES ($kind, $key, $chunk, $doc)
            ON CONFLICT(Kind, Key) DO UPDATE SET
                ChunkId = COALESCE(excluded.ChunkId, Nodes.ChunkId),
                DocumentId = COALESCE(excluded.DocumentId, Nodes.DocumentId)
            RETURNING Id;
            """);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$chunk", (object?)chunkId ?? DBNull.Value);
        command.Parameters.AddWithValue("$doc", (object?)documentId ?? DBNull.Value);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task AddEdgeAsync(long sourceNodeId, long targetNodeId, string kind, double weight = 1.0, CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand("""
            INSERT INTO Edges (SourceNodeId, TargetNodeId, Kind, Weight)
            VALUES ($source, $target, $kind, $weight)
            ON CONFLICT(SourceNodeId, TargetNodeId, Kind) DO UPDATE SET Weight = excluded.Weight;
            """);
        command.Parameters.AddWithValue("$source", sourceNodeId);
        command.Parameters.AddWithValue("$target", targetNodeId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$weight", weight);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetNodeMetadataAsync(long nodeId, string key, string value, CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand("""
            INSERT INTO NodeMetadata (NodeId, Key, Value) VALUES ($node, $key, $value)
            ON CONFLICT(NodeId, Key) DO UPDATE SET Value = excluded.Value;
            """);
        command.Parameters.AddWithValue("$node", nodeId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetNodeMetadataAsync(long nodeId, CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand("SELECT Key, Value FROM NodeMetadata WHERE NodeId = $node;");
        command.Parameters.AddWithValue("$node", nodeId);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            metadata[reader.GetString(0)] = reader.GetString(1);
        }

        return metadata;
    }

    public async Task<IReadOnlyList<KbNode>> GetNodesForChunksAsync(IReadOnlyCollection<long> chunkIds, CancellationToken cancellationToken = default)
    {
        if (chunkIds.Count == 0)
        {
            return Array.Empty<KbNode>();
        }

        var parameterNames = chunkIds.Select((_, index) => $"$id{index}").ToArray();
        using var command = _database.CreateCommand(
            $"SELECT Id, Kind, Key, ChunkId, DocumentId FROM Nodes WHERE ChunkId IN ({string.Join(", ", parameterNames)});");

        var i = 0;
        foreach (var id in chunkIds)
        {
            command.Parameters.AddWithValue(parameterNames[i++], id);
        }

        var nodes = new List<KbNode>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(new KbNode(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }

        return nodes;
    }

    public async Task<IReadOnlyList<long>> ExpandAsync(IReadOnlyCollection<long> nodeIds, int depth, CancellationToken cancellationToken = default)
    {
        if (nodeIds.Count == 0 || depth <= 0)
        {
            return Array.Empty<long>();
        }

        var adjacency = await LoadAdjacencyAsync(cancellationToken).ConfigureAwait(false);
        var visited = new HashSet<long>(nodeIds);
        var frontier = new List<long>(nodeIds);

        for (var level = 0; level < depth; level++)
        {
            var next = new List<long>();
            foreach (var nodeId in frontier)
            {
                if (!adjacency.TryGetValue(nodeId, out var neighbours))
                {
                    continue;
                }

                foreach (var neighbour in neighbours)
                {
                    if (visited.Add(neighbour))
                    {
                        next.Add(neighbour);
                    }
                }
            }

            if (next.Count == 0)
            {
                break;
            }

            frontier = next;
        }

        var reached = visited.Except(nodeIds).ToList();
        return await ResolveChunkIdsAsync(reached, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var command = _database.CreateCommand("DELETE FROM Edges; DELETE FROM NodeMetadata; DELETE FROM Nodes;");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<long, List<long>>> LoadAdjacencyAsync(CancellationToken cancellationToken)
    {
        var adjacency = new Dictionary<long, List<long>>();
        using var command = _database.CreateCommand("SELECT SourceNodeId, TargetNodeId FROM Edges;");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var source = reader.GetInt64(0);
            var target = reader.GetInt64(1);
            Add(adjacency, source, target);
            Add(adjacency, target, source);
        }

        return adjacency;

        static void Add(Dictionary<long, List<long>> map, long from, long to)
        {
            if (!map.TryGetValue(from, out var list))
            {
                list = new List<long>();
                map[from] = list;
            }

            list.Add(to);
        }
    }

    private async Task<IReadOnlyList<long>> ResolveChunkIdsAsync(IReadOnlyCollection<long> nodeIds, CancellationToken cancellationToken)
    {
        if (nodeIds.Count == 0)
        {
            return Array.Empty<long>();
        }

        var parameterNames = nodeIds.Select((_, index) => $"$id{index}").ToArray();
        using var command = _database.CreateCommand(
            $"SELECT DISTINCT ChunkId FROM Nodes WHERE ChunkId IS NOT NULL AND Id IN ({string.Join(", ", parameterNames)});");

        var i = 0;
        foreach (var id in nodeIds)
        {
            command.Parameters.AddWithValue(parameterNames[i++], id);
        }

        var chunkIds = new List<long>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chunkIds.Add(reader.GetInt64(0));
        }

        return chunkIds;
    }
}
