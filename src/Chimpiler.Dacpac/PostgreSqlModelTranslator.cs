using System.Globalization;
using System.Text.RegularExpressions;

namespace Chimpiler.Dacpac;

public sealed partial class PostgreSqlModelTranslator
{
    public DatabaseSchema Translate(DacpacSchema source)
    {
        ValidateSource(source);
        var tables = source.Tables
            .Select(TranslateTable)
            .OrderBy(table => table.Key, StringComparer.Ordinal)
            .ToArray();

        var duplicateTarget = tables.GroupBy(table => table.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTarget is not null)
        {
            throw new DacpacCompatibilityException(
                $"Multiple DACPAC tables map to PostgreSQL table '{duplicateTarget.Key}'.");
        }

        ValidatePostgreSqlIndexNames(tables);
        return new DatabaseSchema(tables);
    }

    public string MapType(SqlServerType source)
    {
        var name = source.Name.ToLowerInvariant();
        return name switch
        {
            "bigint" => "bigint",
            "int" => "integer",
            "smallint" or "tinyint" => "smallint",
            "bit" => "boolean",
            "decimal" or "numeric" => $"numeric({source.Precision},{source.Scale})",
            "money" => "numeric(19,4)",
            "smallmoney" => "numeric(10,4)",
            "float" => "double precision",
            "real" => "real",
            "date" => "date",
            "time" => source.Scale is > 0 and <= 6 ? $"time({source.Scale}) without time zone" : "time without time zone",
            "datetime" or "smalldatetime" => "timestamp without time zone",
            "datetime2" => source.Scale is > 0 and <= 6 ? $"timestamp({source.Scale}) without time zone" : "timestamp without time zone",
            "datetimeoffset" => source.Scale is > 0 and <= 6 ? $"timestamp({source.Scale}) with time zone" : "timestamp with time zone",
            "char" or "nchar" => $"character({RequireLength(source)})",
            "varchar" or "nvarchar" => source.IsMax ? "text" : $"character varying({RequireLength(source)})",
            "text" or "ntext" => "text",
            "binary" or "varbinary" or "image" => "bytea",
            "uniqueidentifier" => "uuid",
            "xml" => "xml",
            "json" => "jsonb",
            _ => throw new DacpacCompatibilityException($"SQL Server type '{source.Name}' is not supported by the PostgreSQL provider.")
        };
    }

    public DatabaseDefault TranslateDefault(string expression, SqlServerType columnType)
    {
        var value = StripOuterParentheses(expression.Trim());
        var type = columnType.Name.ToLowerInvariant();

        if (value.Equals("getdate()", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("sysdatetime()", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("current_timestamp", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseDefault("CURRENT_TIMESTAMP", "timestamp:current");
        }

        if (value.Equals("getutcdate()", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("sysutcdatetime()", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseDefault("(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')", "timestamp:utc");
        }

        if (value.Equals("newid()", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseDefault("gen_random_uuid()", "uuid:random");
        }

        if (value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return new DatabaseDefault("NULL", "null");
        }

        var stringMatch = SqlStringLiteral().Match(value);
        if (stringMatch.Success)
        {
            var unescaped = stringMatch.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal);
            var escaped = unescaped.Replace("'", "''", StringComparison.Ordinal);
            return new DatabaseDefault($"'{escaped}'", $"string:{unescaped}");
        }

        if (BooleanType(type) && (value == "0" || value == "1"))
        {
            return value == "1"
                ? new DatabaseDefault("TRUE", "boolean:true")
                : new DatabaseDefault("FALSE", "boolean:false");
        }

        if (NumericLiteral().IsMatch(value))
        {
            var canonical = decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : value;
            return new DatabaseDefault(value, $"number:{canonical}");
        }

        throw new DacpacCompatibilityException(
            $"Default expression '{expression}' is not in the supported safe subset. Supported defaults are literals, CURRENT_TIMESTAMP-style functions, and NEWID().");
    }

    private DatabaseTable TranslateTable(DacpacTable source)
    {
        var schema = source.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? "public" : source.Schema;
        return new DatabaseTable(
            schema,
            source.Name,
            source.Columns.Select(column => new DatabaseColumn(
                column.Name,
                MapType(column.Type),
                column.IsNullable,
                column.DefaultExpression is null ? null : TranslateDefault(column.DefaultExpression, column.Type),
                column.Identity)).ToArray(),
            source.PrimaryKey is null ? null : new DatabaseKey(source.PrimaryKey.Name, source.PrimaryKey.Columns),
            source.UniqueConstraints.Select(key => new DatabaseKey(key.Name, key.Columns)).ToArray(),
            source.ForeignKeys.Select(foreignKey => new DatabaseForeignKey(
                foreignKey.Name,
                foreignKey.Columns,
                foreignKey.ReferencedSchema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? "public" : foreignKey.ReferencedSchema,
                foreignKey.ReferencedTable,
                foreignKey.ReferencedColumns,
                foreignKey.OnDelete,
                foreignKey.OnUpdate)).ToArray(),
            source.Indexes.Select(index => new DatabaseIndex(
                index.Name,
                index.IsUnique,
                index.Columns,
                index.IncludedColumns)).ToArray());
    }

    private static void ValidateSource(DacpacSchema source)
    {
        var tables = source.Tables.ToDictionary(table => table.Key, StringComparer.Ordinal);
        foreach (var table in source.Tables)
        {
            var columnDefinitions = table.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
            var columns = columnDefinitions.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var column in table.Columns)
            {
                if (column.Identity is not null)
                {
                    var type = column.Type.Name.ToLowerInvariant();
                    if (type is not ("tinyint" or "smallint" or "int" or "bigint"))
                    {
                        throw new DacpacCompatibilityException(
                            $"Identity column {table.Key}.{column.Name} uses unsupported type '{column.Type.Name}'.");
                    }

                    if (column.Identity.Increment == 0)
                    {
                        throw new DacpacCompatibilityException(
                            $"Identity column {table.Key}.{column.Name} has a zero increment.");
                    }
                }
            }

            ValidateKey(table.Key, table.PrimaryKey, columns);
            foreach (var key in table.UniqueConstraints)
            {
                ValidateKey(table.Key, key, columns);
                RejectNullableUniqueColumns(table.Key, key.Name, key.Columns, columnDefinitions);
            }

            foreach (var index in table.Indexes)
            {
                if (index.Columns.Count == 0)
                {
                    throw new DacpacCompatibilityException($"Index {table.Key}.{index.Name} has no key columns.");
                }

                ValidateColumns(table.Key, index.Name, index.Columns.Select(column => column.Name), columns);
                ValidateColumns(table.Key, index.Name, index.IncludedColumns, columns);
                if (index.IsUnique)
                {
                    RejectNullableUniqueColumns(
                        table.Key,
                        index.Name,
                        index.Columns.Select(column => column.Name),
                        columnDefinitions);
                }
            }

            foreach (var foreignKey in table.ForeignKeys)
            {
                ValidateColumns(table.Key, foreignKey.Name, foreignKey.Columns, columns);
                var referencedKey = $"{foreignKey.ReferencedSchema}.{foreignKey.ReferencedTable}";
                if (!tables.TryGetValue(referencedKey, out var referencedTable))
                {
                    throw new DacpacCompatibilityException(
                        $"Foreign key {table.Key}.{foreignKey.Name} references table {referencedKey}, which is not defined in the DACPAC.");
                }

                ValidateColumns(
                    referencedKey,
                    foreignKey.Name,
                    foreignKey.ReferencedColumns,
                    referencedTable.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal));
            }
        }
    }

    private static void ValidatePostgreSqlIndexNames(IReadOnlyList<DatabaseTable> tables)
    {
        var indexBackedObjects = tables.SelectMany(table =>
        {
            var objects = new List<(string Schema, string Name, string Owner)>();
            if (table.PrimaryKey is not null)
            {
                objects.Add((table.Schema, table.PrimaryKey.Name, $"{table.Key} primary key"));
            }

            objects.AddRange(table.UniqueConstraints.Select(
                key => (table.Schema, key.Name, $"{table.Key} unique constraint")));
            objects.AddRange(table.Indexes.Select(
                index => (table.Schema, index.Name, $"{table.Key} index")));
            return objects;
        });

        var collision = indexBackedObjects
            .GroupBy(item => (item.Schema, item.Name))
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key.Schema, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (collision is null)
        {
            return;
        }

        var owners = collision
            .Select(item => item.Owner)
            .OrderBy(owner => owner, StringComparer.Ordinal);
        throw new DacpacCompatibilityException(
            $"PostgreSQL index name '{collision.Key.Name}' collides within schema '{collision.Key.Schema}': {string.Join(", ", owners)}. PostgreSQL index names must be unique within a schema.");
    }

    private static void RejectNullableUniqueColumns(
        string tableKey,
        string objectName,
        IEnumerable<string> keyColumns,
        IReadOnlyDictionary<string, DacpacColumn> columns)
    {
        var nullableColumns = keyColumns
            .Where(column => columns[column].IsNullable)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (nullableColumns.Length > 0)
        {
            throw new DacpacCompatibilityException(
                $"Unique object {tableKey}.{objectName} contains nullable column(s) {string.Join(", ", nullableColumns)}. SQL Server and PostgreSQL have different NULL uniqueness semantics, so Chimpiler fails closed.");
        }
    }

    private static void ValidateKey(string tableKey, DacpacKey? key, IReadOnlySet<string> columns)
    {
        if (key is null)
        {
            return;
        }

        if (key.Columns.Count == 0)
        {
            throw new DacpacCompatibilityException($"Constraint {tableKey}.{key.Name} has no columns.");
        }

        ValidateColumns(tableKey, key.Name, key.Columns, columns);
    }

    private static void ValidateColumns(
        string tableKey,
        string objectName,
        IEnumerable<string> referencedColumns,
        IReadOnlySet<string> columns)
    {
        var missing = referencedColumns.Where(column => !columns.Contains(column)).ToArray();
        if (missing.Length > 0)
        {
            throw new DacpacCompatibilityException(
                $"Object {tableKey}.{objectName} references missing column(s): {string.Join(", ", missing)}.");
        }
    }

    private static int RequireLength(SqlServerType source)
    {
        if (source.Length <= 0)
        {
            throw new DacpacCompatibilityException($"SQL Server type '{source.Name}' has an invalid length.");
        }

        return source.Length;
    }

    private static bool BooleanType(string type) => type == "bit";

    private static string StripOuterParentheses(string value)
    {
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')' && IsSingleOuterPair(value))
        {
            value = value[1..^1].Trim();
        }

        return value;
    }

    private static bool IsSingleOuterPair(string value)
    {
        var depth = 0;
        var inString = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\'' && (index + 1 >= value.Length || value[index + 1] != '\''))
            {
                inString = !inString;
            }
            else if (value[index] == '\'' && inString && index + 1 < value.Length && value[index + 1] == '\'')
            {
                index++;
            }
            else if (!inString && value[index] == '(')
            {
                depth++;
            }
            else if (!inString && value[index] == ')' && --depth == 0 && index != value.Length - 1)
            {
                return false;
            }
        }

        return depth == 0 && !inString;
    }

    [GeneratedRegex(@"^N?'(?<value>(?:''|[^'])*)'$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlStringLiteral();

    [GeneratedRegex(@"^[+-]?\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericLiteral();
}
