using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace Chimpiler.Dacpac;

public sealed partial class PostgreSqlCatalogReader
{
    public async Task<DatabaseSchema> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var tables = new Dictionary<string, MutableTable>(StringComparer.Ordinal);
        await ReadColumnsAsync(connection, transaction, tables, cancellationToken);
        await ReadRowPresenceAsync(connection, transaction, tables, cancellationToken);
        await ReadConstraintsAsync(connection, transaction, tables, cancellationToken);
        await ReadIndexesAsync(connection, transaction, tables, cancellationToken);

        return new DatabaseSchema(tables.Values
            .OrderBy(table => table.Key, StringComparer.Ordinal)
            .Select(table => table.Freeze())
            .ToArray());
    }

    private static async Task ReadRowPresenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IDictionary<string, MutableTable> tables,
        CancellationToken cancellationToken)
    {
        foreach (var table in tables.Values.OrderBy(table => table.Key, StringComparer.Ordinal))
        {
            var sql = $"SELECT EXISTS (SELECT 1 FROM {PostgreSqlSqlGenerator.Qualified(table.Schema, table.Name)} LIMIT 1)";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            table.HasRows = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
    }

    public static DatabaseDefault? ParseDefault(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var value = expression.Trim();
        while (value.StartsWith('(') && value.EndsWith(')'))
        {
            value = value[1..^1].Trim();
        }

        value = PgCast().Replace(value, string.Empty);

        if (value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseDefault(expression, "null");
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseDefault(expression, "boolean:true");
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseDefault(expression, "boolean:false");
        }

        if (value.Equals("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("now()", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseDefault(expression, "timestamp:current");
        }

        if (value.Contains("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("UTC", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseDefault(expression, "timestamp:utc");
        }

        if (value.Equals("gen_random_uuid()", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseDefault(expression, "uuid:random");
        }

        var stringMatch = PgStringLiteral().Match(value);
        if (stringMatch.Success)
        {
            return new DatabaseDefault(expression,
                "string:" + stringMatch.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal));
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return new DatabaseDefault(expression, $"number:{number.ToString(CultureInfo.InvariantCulture)}");
        }

        return new DatabaseDefault(expression, $"postgres:{Normalize(value)}");
    }

    private static async Task ReadColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IDictionary<string, MutableTable> tables,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname,
                   c.relname,
                   a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   NOT a.attnotnull,
                   a.attidentity,
                   pg_get_expr(ad.adbin, ad.adrelid),
                   COALESCE(seq.seqstart, 1),
                   COALESCE(seq.seqincrement, 1)
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
            LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid = c.oid AND ad.adnum = a.attnum
            LEFT JOIN LATERAL (
              SELECT dep.objid
              FROM pg_catalog.pg_depend dep
              JOIN pg_catalog.pg_class seq_candidate ON seq_candidate.oid = dep.objid AND seq_candidate.relkind = 'S'
              WHERE dep.refobjid = c.oid
                AND dep.refobjsubid = a.attnum
                AND dep.deptype IN ('i', 'a')
              LIMIT 1
            ) identity_dep ON TRUE
            LEFT JOIN pg_catalog.pg_sequence seq ON seq.seqrelid = identity_dep.objid
            WHERE c.relkind IN ('r', 'p')
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND n.nspname <> 'information_schema'
              AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, a.attnum
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var table = GetTable(tables, schema, tableName);
            var identityKind = reader.GetChar(5);
            var identity = identityKind == '\0'
                ? null
                : new IdentityDefinition(reader.GetInt64(7), checked((int)reader.GetInt64(8)));
            table.Columns.Add(new DatabaseColumn(
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.IsDBNull(6) ? null : ParseDefault(reader.GetString(6)),
                identity));
        }
    }

    private static async Task ReadConstraintsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IDictionary<string, MutableTable> tables,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname,
                   c.relname,
                   con.conname,
                   con.contype,
                   ARRAY(
                     SELECT a.attname
                     FROM unnest(con.conkey) WITH ORDINALITY AS keys(attnum, ord)
                     JOIN pg_catalog.pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = keys.attnum
                     ORDER BY keys.ord
                   ),
                   rn.nspname,
                   rc.relname,
                   ARRAY(
                     SELECT a.attname
                     FROM unnest(con.confkey) WITH ORDINALITY AS keys(attnum, ord)
                     JOIN pg_catalog.pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = keys.attnum
                     ORDER BY keys.ord
                   ),
                   con.confdeltype,
                   con.confupdtype
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_class rc ON rc.oid = con.confrelid
            LEFT JOIN pg_catalog.pg_namespace rn ON rn.oid = rc.relnamespace
            WHERE con.contype IN ('p', 'u', 'f')
              AND n.nspname <> 'information_schema'
              AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, con.conname
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = GetTable(tables, reader.GetString(0), reader.GetString(1));
            var name = reader.GetString(2);
            var kind = reader.GetChar(3);
            var columns = reader.GetFieldValue<string[]>(4);
            switch (kind)
            {
                case 'p':
                    table.PrimaryKey = new DatabaseKey(name, columns);
                    break;
                case 'u':
                    table.UniqueConstraints.Add(new DatabaseKey(name, columns));
                    break;
                case 'f':
                    table.ForeignKeys.Add(new DatabaseForeignKey(
                        name,
                        columns,
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetFieldValue<string[]>(7),
                        ParseAction(reader.GetChar(8)),
                        ParseAction(reader.GetChar(9))));
                    break;
            }
        }
    }

    private static async Task ReadIndexesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IDictionary<string, MutableTable> tables,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname,
                   c.relname,
                   ic.relname,
                   i.indisunique,
                   ARRAY(
                     SELECT a.attname
                     FROM unnest(i.indkey::smallint[]) WITH ORDINALITY AS keys(attnum, ord)
                     JOIN pg_catalog.pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = keys.attnum
                     WHERE keys.ord <= i.indnkeyatts
                     ORDER BY keys.ord
                   ),
                   ARRAY(
                     SELECT (options.option_value & 1) = 0
                     FROM unnest(i.indoption::smallint[]) WITH ORDINALITY AS options(option_value, ord)
                     WHERE options.ord <= i.indnkeyatts
                     ORDER BY options.ord
                   ),
                   ARRAY(
                     SELECT a.attname
                     FROM unnest(i.indkey::smallint[]) WITH ORDINALITY AS keys(attnum, ord)
                     JOIN pg_catalog.pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = keys.attnum
                     WHERE keys.ord > i.indnkeyatts
                     ORDER BY keys.ord
                   )
            FROM pg_catalog.pg_index i
            JOIN pg_catalog.pg_class c ON c.oid = i.indrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid
            WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint con WHERE con.conindid = i.indexrelid)
              AND i.indexprs IS NULL
              AND i.indpred IS NULL
              AND n.nspname <> 'information_schema'
              AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, ic.relname
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = GetTable(tables, reader.GetString(0), reader.GetString(1));
            var columnNames = reader.GetFieldValue<string[]>(4);
            var ascending = reader.GetFieldValue<bool[]>(5);
            table.Indexes.Add(new DatabaseIndex(
                reader.GetString(2),
                reader.GetBoolean(3),
                columnNames.Select((name, index) => new IndexColumn(name, ascending[index])).ToArray(),
                reader.GetFieldValue<string[]>(6)));
        }
    }

    private static MutableTable GetTable(IDictionary<string, MutableTable> tables, string schema, string name)
    {
        var key = $"{schema}.{name}";
        if (!tables.TryGetValue(key, out var table))
        {
            table = new MutableTable(schema, name);
            tables.Add(key, table);
        }

        return table;
    }

    private static ReferentialAction ParseAction(char action) => action switch
    {
        'a' => ReferentialAction.NoAction,
        'r' => ReferentialAction.NoAction,
        'c' => ReferentialAction.Cascade,
        'n' => ReferentialAction.SetNull,
        'd' => ReferentialAction.SetDefault,
        _ => throw new DacpacCompatibilityException($"Unsupported PostgreSQL referential action '{action}'.")
    };

    private static string Normalize(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();

    [GeneratedRegex(@"::(?:[\w\s]+)(?:\(\d+(?:,\d+)?\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex PgCast();

    [GeneratedRegex(@"^'(?'value'(?:''|[^'])*)'$", RegexOptions.CultureInvariant)]
    private static partial Regex PgStringLiteral();

    private sealed class MutableTable(string schema, string name)
    {
        public string Schema { get; } = schema;
        public string Name { get; } = name;
        public string Key => $"{Schema}.{Name}";
        public List<DatabaseColumn> Columns { get; } = [];
        public DatabaseKey? PrimaryKey { get; set; }
        public List<DatabaseKey> UniqueConstraints { get; } = [];
        public List<DatabaseForeignKey> ForeignKeys { get; } = [];
        public List<DatabaseIndex> Indexes { get; } = [];
        public bool HasRows { get; set; }

        public DatabaseTable Freeze() => new(
            Schema,
            Name,
            Columns.ToArray(),
            PrimaryKey,
            UniqueConstraints.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            ForeignKeys.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            Indexes.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            HasRows);
    }
}
