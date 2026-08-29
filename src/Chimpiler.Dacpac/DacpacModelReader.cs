using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using DacIndex = Microsoft.SqlServer.Dac.Model.Index;

namespace Chimpiler.Dacpac;

public sealed class DacpacModelReader
{
    private const string DefaultSqlServerCollation = "SQL_Latin1_General_CP1_CI_AS";

    private static readonly HashSet<ModelTypeClass> AllowedObjectTypes =
    [
        ModelSchema.DatabaseOptions,
        ModelSchema.Schema,
        ModelSchema.Table,
        ModelSchema.Column,
        ModelSchema.DefaultConstraint,
        ModelSchema.PrimaryKeyConstraint,
        ModelSchema.UniqueConstraint,
        ModelSchema.ForeignKeyConstraint,
        ModelSchema.Index
    ];

    public DacpacSchema Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A DACPAC path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"DACPAC not found: {fullPath}", fullPath);
        }

        RejectDeploymentScripts(fullPath);

        using var model = TSqlModel.LoadFromDacpac(fullPath, new ModelLoadOptions());
        RejectUnsupportedObjects(model);
        RejectUnsupportedCollations(model);

        var defaults = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.DefaultConstraint)
            .Select(item => new
            {
                Target = item.GetReferenced(DefaultConstraint.TargetColumn).SingleOrDefault(),
                Expression = item.GetProperty(DefaultConstraint.Expression)?.ToString()
            })
            .Where(item => item.Target is not null)
            .ToDictionary(item => item.Target!, item => item.Expression);

        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table)
            .OrderBy(item => item.Name.ToString(), StringComparer.Ordinal)
            .Select(table => ReadTable(model, table, defaults))
            .ToArray();

        return new DacpacSchema(tables);
    }

    private static void RejectUnsupportedCollations(TSqlModel model)
    {
        var databaseCollations = model.GetObjects(DacQueryScopes.All, ModelSchema.DatabaseOptions)
            .Select(item => item.GetProperty<string>(DatabaseOptions.Collation))
            .Where(collation => !string.IsNullOrWhiteSpace(collation))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nonDefaultCollation = databaseCollations.FirstOrDefault(
            collation => !collation.Equals(DefaultSqlServerCollation, StringComparison.OrdinalIgnoreCase));
        if (nonDefaultCollation is not null)
        {
            throw new DacpacCompatibilityException(
                $"Database collation '{nonDefaultCollation}' is not supported for PostgreSQL translation. Chimpiler will not silently discard non-default SQL Server collation semantics.");
        }

        var explicitColumn = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table)
            .SelectMany(table => table.GetReferenced(Table.Columns))
            .Select(column => new
            {
                Column = column,
                Collation = column.GetProperty<string>(Column.Collation)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Collation))
            .OrderBy(item => item.Column.Name.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
        if (explicitColumn is not null)
        {
            throw new DacpacCompatibilityException(
                $"Column {explicitColumn.Column.Name} uses explicit collation '{explicitColumn.Collation}', which is not supported for PostgreSQL translation.");
        }
    }

    private static void RejectDeploymentScripts(string path)
    {
        using var package = DacPackage.Load(path);
        if (HasContent(package.PreDeploymentScript) || HasContent(package.PostDeploymentScript))
        {
            throw new DacpacCompatibilityException(
                "Pre-deployment and post-deployment scripts are not supported. Chimpiler fails closed because SQL Server scripts cannot be safely translated to PostgreSQL.");
        }
    }

    private static bool HasContent(Stream? stream)
    {
        if (stream is null)
        {
            return false;
        }

        using var reader = new StreamReader(stream, leaveOpen: true);
        return !string.IsNullOrWhiteSpace(reader.ReadToEnd());
    }

    private static void RejectUnsupportedObjects(TSqlModel model)
    {
        var unsupported = model.GetObjects(DacQueryScopes.UserDefined)
            .Where(item => !AllowedObjectTypes.Contains(item.ObjectType))
            .Select(item => $"{item.ObjectType.Name} {item.Name}")
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        if (unsupported.Length > 0)
        {
            throw new DacpacCompatibilityException(
                "The DACPAC contains unsupported objects:\n" + string.Join('\n', unsupported.Select(item => $"- {item}")));
        }
    }

    private static DacpacTable ReadTable(
        TSqlModel model,
        TSqlObject table,
        IReadOnlyDictionary<TSqlObject, string?> defaults)
    {
        var (schema, name) = GetSchemaAndName(table.Name);
        RejectUnsupportedTableOptions(table);

        var columns = table.GetReferenced(Table.Columns)
            .OrderBy(column => column.Name.Parts.Last(), StringComparer.Ordinal)
            .Select(column => ReadColumn(column, defaults))
            .ToArray();

        if (columns.Length == 0)
        {
            throw new DacpacCompatibilityException($"Table {table.Name} has no supported columns.");
        }

        var primaryKey = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.PrimaryKeyConstraint)
            .SingleOrDefault(item => References(item, PrimaryKeyConstraint.Host, table));
        var uniqueConstraints = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.UniqueConstraint)
            .Where(item => References(item, UniqueConstraint.Host, table))
            .OrderBy(item => item.Name.ToString(), StringComparer.Ordinal)
            .Select(item => ReadKey(item, UniqueConstraint.Columns))
            .ToArray();
        var foreignKeys = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.ForeignKeyConstraint)
            .Where(item => References(item, ForeignKeyConstraint.Host, table))
            .OrderBy(item => item.Name.ToString(), StringComparer.Ordinal)
            .Select(ReadForeignKey)
            .ToArray();
        var indexes = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Index)
            .Where(item => References(item, DacIndex.IndexedObject, table))
            .OrderBy(item => item.Name.ToString(), StringComparer.Ordinal)
            .Select(ReadIndex)
            .ToArray();

        return new DacpacTable(
            schema,
            name,
            columns,
            primaryKey is null ? null : ReadKey(primaryKey, PrimaryKeyConstraint.Columns),
            uniqueConstraints,
            foreignKeys,
            indexes);
    }

    private static void RejectUnsupportedTableOptions(TSqlObject table)
    {
        if (table.GetProperty<bool>(Table.MemoryOptimized) ||
            table.GetProperty<bool>(Table.IsNode) ||
            table.GetProperty<bool>(Table.IsEdge) ||
            table.GetReferenced(Table.TemporalSystemVersioningHistoryTable).Any())
        {
            throw new DacpacCompatibilityException(
                $"Table {table.Name} uses SQL Server-specific memory-optimized, graph, or temporal features.");
        }
    }

    private static DacpacColumn ReadColumn(
        TSqlObject column,
        IReadOnlyDictionary<TSqlObject, string?> defaults)
    {
        if (column.GetProperty(Column.Expression) is not null)
        {
            throw new DacpacCompatibilityException($"Computed column {column.Name} is not supported.");
        }

        if (column.GetProperty<bool>(Column.IsFileStream) ||
            column.GetProperty<bool>(Column.Sparse) ||
            column.GetProperty<bool>(Column.IsRowGuidCol) ||
            column.GetProperty<bool>(Column.IsHidden) ||
            column.GetReferenced(Column.ColumnEncryptionKey).Any())
        {
            throw new DacpacCompatibilityException($"Column {column.Name} uses unsupported SQL Server-specific features.");
        }

        var dataType = column.GetReferenced(Column.DataType).SingleOrDefault()
            ?? throw new DacpacCompatibilityException($"Column {column.Name} has no readable data type.");
        var sqlType = dataType.GetProperty<SqlDataType>(DataType.SqlDataType);
        if (sqlType == SqlDataType.Unknown)
        {
            throw new DacpacCompatibilityException($"Column {column.Name} uses a user-defined or unknown data type.");
        }

        IdentityDefinition? identity = null;
        if (column.GetProperty<bool>(Column.IsIdentity))
        {
            if (!long.TryParse(column.GetProperty<string>(Column.IdentitySeed), out var seed) ||
                !int.TryParse(column.GetProperty<string>(Column.IdentityIncrement), out var increment))
            {
                throw new DacpacCompatibilityException($"Column {column.Name} has an unsupported identity definition.");
            }

            identity = new IdentityDefinition(seed, increment);
        }

        defaults.TryGetValue(column, out var defaultExpression);
        return new DacpacColumn(
            column.Name.Parts.Last(),
            new SqlServerType(
                sqlType.ToString(),
                column.GetProperty<int>(Column.Length),
                column.GetProperty<int>(Column.Precision),
                column.GetProperty<int>(Column.Scale),
                column.GetProperty<bool>(Column.IsMax)),
            column.GetProperty<bool>(Column.Nullable),
            defaultExpression,
            identity);
    }

    private static DacpacKey ReadKey(TSqlObject key, ModelRelationshipClass columnsRelationship)
    {
        var columns = key.GetReferencedRelationshipInstances(columnsRelationship)
            .Select(item => item.ObjectName.Parts.Last())
            .ToArray();
        return new DacpacKey(key.Name.Parts.Last(), columns);
    }

    private static DacpacForeignKey ReadForeignKey(TSqlObject foreignKey)
    {
        var referencedTable = foreignKey.GetReferenced(ForeignKeyConstraint.ForeignTable).Single();
        var (referencedSchema, referencedName) = GetSchemaAndName(referencedTable.Name);

        return new DacpacForeignKey(
            foreignKey.Name.Parts.Last(),
            foreignKey.GetReferenced(ForeignKeyConstraint.Columns).Select(item => item.Name.Parts.Last()).ToArray(),
            referencedSchema,
            referencedName,
            foreignKey.GetReferenced(ForeignKeyConstraint.ForeignColumns).Select(item => item.Name.Parts.Last()).ToArray(),
            MapAction(foreignKey.GetProperty<ForeignKeyAction>(ForeignKeyConstraint.DeleteAction)),
            MapAction(foreignKey.GetProperty<ForeignKeyAction>(ForeignKeyConstraint.UpdateAction)));
    }

    private static DacpacIndex ReadIndex(TSqlObject index)
    {
        if (!string.IsNullOrWhiteSpace(index.GetProperty(DacIndex.FilterPredicate)?.ToString()) ||
            index.GetProperty<bool>(DacIndex.Hash) ||
            index.GetProperty<bool>(DacIndex.Clustered))
        {
            throw new DacpacCompatibilityException(
                $"Index {index.Name} is filtered, hash, or clustered; only ordinary indexes are supported.");
        }

        var columns = index.GetReferencedRelationshipInstances(DacIndex.Columns)
            .Select(item => new IndexColumn(
                item.ObjectName.Parts.Last(),
                item.GetProperty<bool>(DacIndex.ColumnsRelationship.Ascending)))
            .ToArray();

        return new DacpacIndex(
            index.Name.Parts.Last(),
            index.GetProperty<bool>(DacIndex.Unique),
            columns,
            index.GetReferenced(DacIndex.IncludedColumns).Select(item => item.Name.Parts.Last()).ToArray());
    }

    private static bool References(TSqlObject source, ModelRelationshipClass relationship, TSqlObject target) =>
        source.GetReferenced(relationship).Any(item => item.Equals(target));

    private static (string Schema, string Name) GetSchemaAndName(ObjectIdentifier identifier)
    {
        if (identifier.Parts.Count < 2)
        {
            throw new DacpacCompatibilityException($"Object {identifier} does not have a schema-qualified name.");
        }

        return (identifier.Parts[^2], identifier.Parts[^1]);
    }

    private static ReferentialAction MapAction(ForeignKeyAction action) => action switch
    {
        ForeignKeyAction.NoAction => ReferentialAction.NoAction,
        ForeignKeyAction.Cascade => ReferentialAction.Cascade,
        ForeignKeyAction.SetNull => ReferentialAction.SetNull,
        ForeignKeyAction.SetDefault => ReferentialAction.SetDefault,
        _ => throw new DacpacCompatibilityException($"Unsupported referential action: {action}.")
    };
}
