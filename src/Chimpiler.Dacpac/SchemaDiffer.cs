namespace Chimpiler.Dacpac;

public sealed class SchemaDiffer
{
    private readonly PostgreSqlSqlGenerator _sql = new();

    public DeploymentPlan Diff(DatabaseSchema desired, DatabaseSchema current)
    {
        var operations = new List<DeploymentOperation>();
        var desiredTables = desired.Tables.ToDictionary(table => table.Key, StringComparer.Ordinal);
        var currentTables = current.Tables.ToDictionary(table => table.Key, StringComparer.Ordinal);
        var typeChangedColumns = FindTypeChangedColumns(desiredTables, currentTables);

        DropChangedAndRemovedObjects(desiredTables, currentTables, typeChangedColumns, operations);
        DropRemovedTablesAndColumns(desiredTables, currentTables, operations);
        CreateSchemasAndTables(desiredTables, currentTables, operations);
        AddAndAlterColumns(desiredTables, currentTables, operations);
        AddConstraintsAndIndexes(desiredTables, currentTables, typeChangedColumns, operations);

        return new DeploymentPlan(operations
            .OrderBy(operation => operation.Phase)
            .ThenBy(operation => operation.SortKey, StringComparer.Ordinal)
            .ToArray());
    }

    private void DropChangedAndRemovedObjects(
        IReadOnlyDictionary<string, DatabaseTable> desiredTables,
        IReadOnlyDictionary<string, DatabaseTable> currentTables,
        IReadOnlySet<string> typeChangedColumns,
        ICollection<DeploymentOperation> operations)
    {
        foreach (var (key, current) in currentTables.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!desiredTables.TryGetValue(key, out var desired))
            {
                foreach (var foreignKey in current.ForeignKeys)
                {
                    Add(operations, 11, $"{key}.fk.{foreignKey.Name}", $"Drop foreign key {foreignKey.Name} from {key}",
                        $"ALTER TABLE {PostgreSqlSqlGenerator.Qualified(current.Schema, current.Name)} DROP CONSTRAINT {PostgreSqlSqlGenerator.Quote(foreignKey.Name)}",
                        true);
                }
                continue;
            }

            DropIndexes(desired, current, typeChangedColumns, operations);
            DropForeignKeys(desired, current, typeChangedColumns, operations);
            DropKey(desired, current, current.PrimaryKey, desired.PrimaryKey, "primary key", typeChangedColumns, operations);

            var desiredUnique = desired.UniqueConstraints.ToDictionary(item => item.Name, StringComparer.Ordinal);
            foreach (var unique in current.UniqueConstraints)
            {
                if (!desiredUnique.TryGetValue(unique.Name, out var wanted) ||
                    !KeyEqual(unique, wanted) ||
                    unique.Columns.Any(column => IsTypeChanged(typeChangedColumns, key, column)))
                {
                    Add(operations, 12, $"{key}.uq.{unique.Name}", $"Drop unique constraint {unique.Name} from {key}",
                        $"ALTER TABLE {PostgreSqlSqlGenerator.Qualified(current.Schema, current.Name)} DROP CONSTRAINT {PostgreSqlSqlGenerator.Quote(unique.Name)}",
                        wanted is null);
                }
            }
        }
    }

    private static void DropRemovedTablesAndColumns(
        IReadOnlyDictionary<string, DatabaseTable> desiredTables,
        IReadOnlyDictionary<string, DatabaseTable> currentTables,
        ICollection<DeploymentOperation> operations)
    {
        foreach (var (key, current) in currentTables.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!desiredTables.TryGetValue(key, out var desired))
            {
                Add(operations, 20, key, $"Drop table {key}",
                    $"DROP TABLE {PostgreSqlSqlGenerator.Qualified(current.Schema, current.Name)}", true);
                continue;
            }

            var desiredColumns = desired.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var column in current.Columns.Where(column => !desiredColumns.Contains(column.Name)))
            {
                Add(operations, 20, $"{key}.{column.Name}", $"Drop column {key}.{column.Name}",
                    $"ALTER TABLE {PostgreSqlSqlGenerator.Qualified(current.Schema, current.Name)} DROP COLUMN {PostgreSqlSqlGenerator.Quote(column.Name)}",
                    true);
            }
        }
    }

    private void CreateSchemasAndTables(
        IReadOnlyDictionary<string, DatabaseTable> desiredTables,
        IReadOnlyDictionary<string, DatabaseTable> currentTables,
        ICollection<DeploymentOperation> operations)
    {
        var currentSchemas = currentTables.Values.Select(table => table.Schema).ToHashSet(StringComparer.Ordinal);
        foreach (var schema in desiredTables.Values.Select(table => table.Schema).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (schema != "public" && !currentSchemas.Contains(schema))
            {
                Add(operations, 40, schema, $"Create schema {schema}",
                    $"CREATE SCHEMA IF NOT EXISTS {PostgreSqlSqlGenerator.Quote(schema)}", false);
            }
        }

        foreach (var (key, table) in desiredTables.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!currentTables.ContainsKey(key))
            {
                Add(operations, 50, key, $"Create table {key}", _sql.CreateTable(table), false);
            }
        }
    }

    private void AddAndAlterColumns(
        IReadOnlyDictionary<string, DatabaseTable> desiredTables,
        IReadOnlyDictionary<string, DatabaseTable> currentTables,
        ICollection<DeploymentOperation> operations)
    {
        foreach (var (key, desired) in desiredTables.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!currentTables.TryGetValue(key, out var current))
            {
                continue;
            }

            var currentColumns = current.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
            foreach (var column in desired.Columns.OrderBy(column => column.Name, StringComparer.Ordinal))
            {
                if (!currentColumns.TryGetValue(column.Name, out var existing))
                {
                    Add(operations, 60, $"{key}.{column.Name}", $"Add column {key}.{column.Name}",
                        _sql.AddColumn(desired, column), false);
                    continue;
                }

                AddColumnAlterations(desired, column, existing, current.HasRows, operations);
            }
        }
    }

    private static void AddColumnAlterations(
        DatabaseTable table,
        DatabaseColumn desired,
        DatabaseColumn current,
        bool currentTableHasRows,
        ICollection<DeploymentOperation> operations)
    {
        var tableName = PostgreSqlSqlGenerator.Qualified(table.Schema, table.Name);
        var columnName = PostgreSqlSqlGenerator.Quote(desired.Name);
        var key = $"{table.Key}.{desired.Name}";

        if (!StoreTypeEqual(desired.StoreType, current.StoreType))
        {
            Add(operations, 61, $"{key}.type", $"Change type of {key} from {current.StoreType} to {desired.StoreType}",
                $"ALTER TABLE {tableName} ALTER COLUMN {columnName} TYPE {desired.StoreType} USING {columnName}::{desired.StoreType}",
                true);
        }

        if (desired.Identity != current.Identity)
        {
            if (current.Identity is null && desired.Identity is not null)
            {
                if (currentTableHasRows)
                {
                    throw new DacpacCompatibilityException(
                        $"Cannot add identity to populated column {key}. Existing values cannot be safely reconciled with the new PostgreSQL identity sequence.");
                }

                Add(operations, 62, $"{key}.identity", $"Add identity to {key}",
                    $"ALTER TABLE {tableName} ALTER COLUMN {columnName} ADD GENERATED BY DEFAULT AS IDENTITY (START WITH {desired.Identity.Seed} INCREMENT BY {desired.Identity.Increment})",
                    false);
            }
            else if (desired.Identity is null)
            {
                Add(operations, 62, $"{key}.identity", $"Drop identity from {key}",
                    $"ALTER TABLE {tableName} ALTER COLUMN {columnName} DROP IDENTITY", true);
            }
            else
            {
                if (desired.Identity!.Seed != current.Identity!.Seed)
                {
                    throw new DacpacCompatibilityException(
                        $"Cannot change identity seed for existing column {key} from {current.Identity.Seed} to {desired.Identity.Seed}. Chimpiler will not restart an existing PostgreSQL identity sequence because doing so can generate duplicate values.");
                }

                if (desired.Identity.Increment != current.Identity.Increment)
                {
                    Add(operations, 62, $"{key}.identity-increment", $"Change identity increment for {key}",
                        $"ALTER TABLE {tableName} ALTER COLUMN {columnName} SET INCREMENT BY {desired.Identity.Increment}", false);
                }
            }
        }

        if (desired.Default?.Canonical != current.Default?.Canonical)
        {
            var sql = desired.Default is null
                ? $"ALTER TABLE {tableName} ALTER COLUMN {columnName} DROP DEFAULT"
                : $"ALTER TABLE {tableName} ALTER COLUMN {columnName} SET DEFAULT {desired.Default.Sql}";
            Add(operations, 63, $"{key}.default", $"Change default for {key}", sql, desired.Default is null);
        }

        if (desired.IsNullable != current.IsNullable)
        {
            var action = desired.IsNullable ? "DROP NOT NULL" : "SET NOT NULL";
            Add(operations, 64, $"{key}.nullable", $"Change nullability of {key}",
                $"ALTER TABLE {tableName} ALTER COLUMN {columnName} {action}", false);
        }
    }

    private void AddConstraintsAndIndexes(
        IReadOnlyDictionary<string, DatabaseTable> desiredTables,
        IReadOnlyDictionary<string, DatabaseTable> currentTables,
        IReadOnlySet<string> typeChangedColumns,
        ICollection<DeploymentOperation> operations)
    {
        foreach (var (key, desired) in desiredTables.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            currentTables.TryGetValue(key, out var current);

            if (desired.PrimaryKey is not null &&
                (current?.PrimaryKey is null ||
                 !KeyEqual(desired.PrimaryKey, current.PrimaryKey) ||
                 desired.PrimaryKey.Columns.Any(column => IsTypeChanged(typeChangedColumns, key, column))))
            {
                Add(operations, 70, $"{key}.pk", $"Add primary key {desired.PrimaryKey.Name} to {key}",
                    _sql.AddPrimaryKey(desired, desired.PrimaryKey), false);
            }

            var currentUnique = current?.UniqueConstraints.ToDictionary(item => item.Name, StringComparer.Ordinal)
                ?? new Dictionary<string, DatabaseKey>(StringComparer.Ordinal);
            foreach (var unique in desired.UniqueConstraints.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (!currentUnique.TryGetValue(unique.Name, out var existing) ||
                    !KeyEqual(unique, existing) ||
                    unique.Columns.Any(column => IsTypeChanged(typeChangedColumns, key, column)))
                {
                    Add(operations, 71, $"{key}.uq.{unique.Name}", $"Add unique constraint {unique.Name} to {key}",
                        _sql.AddUniqueConstraint(desired, unique), false);
                }
            }

            var currentForeignKeys = current?.ForeignKeys.ToDictionary(item => item.Name, StringComparer.Ordinal)
                ?? new Dictionary<string, DatabaseForeignKey>(StringComparer.Ordinal);
            foreach (var foreignKey in desired.ForeignKeys.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (!currentForeignKeys.TryGetValue(foreignKey.Name, out var existing) ||
                    !ForeignKeyEqual(foreignKey, existing) ||
                    foreignKey.Columns.Any(column => IsTypeChanged(typeChangedColumns, key, column)) ||
                    foreignKey.ReferencedColumns.Any(column =>
                        IsTypeChanged(typeChangedColumns, $"{foreignKey.ReferencedSchema}.{foreignKey.ReferencedTable}", column)))
                {
                    Add(operations, 80, $"{key}.fk.{foreignKey.Name}", $"Add foreign key {foreignKey.Name} to {key}",
                        _sql.AddForeignKey(desired, foreignKey), false);
                }
            }

            var currentIndexes = current?.Indexes.ToDictionary(item => item.Name, StringComparer.Ordinal)
                ?? new Dictionary<string, DatabaseIndex>(StringComparer.Ordinal);
            foreach (var index in desired.Indexes.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (!currentIndexes.TryGetValue(index.Name, out var existing) ||
                    !IndexEqual(index, existing) ||
                    index.Columns.Any(column => IsTypeChanged(typeChangedColumns, key, column.Name)) ||
                    index.IncludedColumns.Any(column => IsTypeChanged(typeChangedColumns, key, column)))
                {
                    Add(operations, 90, $"{key}.ix.{index.Name}", $"Create index {index.Name} on {key}",
                        _sql.CreateIndex(desired, index), false);
                }
            }
        }
    }

    private static void DropIndexes(
        DatabaseTable desired,
        DatabaseTable current,
        IReadOnlySet<string> typeChangedColumns,
        ICollection<DeploymentOperation> operations)
    {
        var wanted = desired.Indexes.ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var index in current.Indexes)
        {
            if (!wanted.TryGetValue(index.Name, out var desiredIndex) ||
                !IndexEqual(index, desiredIndex) ||
                index.Columns.Any(column => IsTypeChanged(typeChangedColumns, current.Key, column.Name)) ||
                index.IncludedColumns.Any(column => IsTypeChanged(typeChangedColumns, current.Key, column)))
            {
                Add(operations, 10, $"{current.Key}.ix.{index.Name}", $"Drop index {index.Name} from {current.Key}",
                    $"DROP INDEX {PostgreSqlSqlGenerator.Qualified(current.Schema, index.Name)}",
                    desiredIndex is null);
            }
        }
    }

    private static void DropForeignKeys(
        DatabaseTable desired,
        DatabaseTable current,
        IReadOnlySet<string> typeChangedColumns,
        ICollection<DeploymentOperation> operations)
    {
        var wanted = desired.ForeignKeys.ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var foreignKey in current.ForeignKeys)
        {
            if (!wanted.TryGetValue(foreignKey.Name, out var desiredForeignKey) ||
                !ForeignKeyEqual(foreignKey, desiredForeignKey) ||
                foreignKey.Columns.Any(column => IsTypeChanged(typeChangedColumns, current.Key, column)) ||
                foreignKey.ReferencedColumns.Any(column =>
                    IsTypeChanged(typeChangedColumns, $"{foreignKey.ReferencedSchema}.{foreignKey.ReferencedTable}", column)))
            {
                Add(operations, 11, $"{current.Key}.fk.{foreignKey.Name}", $"Drop foreign key {foreignKey.Name} from {current.Key}",
                    $"ALTER TABLE {PostgreSqlSqlGenerator.Qualified(current.Schema, current.Name)} DROP CONSTRAINT {PostgreSqlSqlGenerator.Quote(foreignKey.Name)}",
                    desiredForeignKey is null);
            }
        }
    }

    private static void DropKey(
        DatabaseTable desired,
        DatabaseTable current,
        DatabaseKey? currentKey,
        DatabaseKey? desiredKey,
        string kind,
        IReadOnlySet<string> typeChangedColumns,
        ICollection<DeploymentOperation> operations)
    {
        if (currentKey is not null &&
            (desiredKey is null ||
             !KeyEqual(currentKey, desiredKey) ||
             currentKey.Columns.Any(column => IsTypeChanged(typeChangedColumns, current.Key, column))))
        {
            Add(operations, 12, $"{current.Key}.{kind}", $"Drop {kind} {currentKey.Name} from {current.Key}",
                $"ALTER TABLE {PostgreSqlSqlGenerator.Qualified(current.Schema, current.Name)} DROP CONSTRAINT {PostgreSqlSqlGenerator.Quote(currentKey.Name)}",
                desiredKey is null);
        }
    }

    private static bool StoreTypeEqual(string left, string right) =>
        Normalize(left) == Normalize(right);

    private static string Normalize(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();

    private static bool KeyEqual(DatabaseKey left, DatabaseKey right) =>
        left.Name == right.Name && left.Columns.SequenceEqual(right.Columns, StringComparer.Ordinal);

    private static bool ForeignKeyEqual(DatabaseForeignKey left, DatabaseForeignKey right) =>
        left.Name == right.Name &&
        left.ReferencedSchema == right.ReferencedSchema &&
        left.ReferencedTable == right.ReferencedTable &&
        left.OnDelete == right.OnDelete &&
        left.OnUpdate == right.OnUpdate &&
        left.Columns.SequenceEqual(right.Columns, StringComparer.Ordinal) &&
        left.ReferencedColumns.SequenceEqual(right.ReferencedColumns, StringComparer.Ordinal);

    private static bool IndexEqual(DatabaseIndex left, DatabaseIndex right) =>
        left.Name == right.Name &&
        left.IsUnique == right.IsUnique &&
        left.Columns.SequenceEqual(right.Columns) &&
        left.IncludedColumns.SequenceEqual(right.IncludedColumns, StringComparer.Ordinal);

    private static HashSet<string> FindTypeChangedColumns(
        IReadOnlyDictionary<string, DatabaseTable> desiredTables,
        IReadOnlyDictionary<string, DatabaseTable> currentTables)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, desired) in desiredTables)
        {
            if (!currentTables.TryGetValue(key, out var current))
            {
                continue;
            }

            var currentColumns = current.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
            foreach (var column in desired.Columns)
            {
                if (currentColumns.TryGetValue(column.Name, out var existing) &&
                    !StoreTypeEqual(column.StoreType, existing.StoreType))
                {
                    changed.Add($"{key}.{column.Name}");
                }
            }
        }

        return changed;
    }

    private static bool IsTypeChanged(IReadOnlySet<string> changed, string tableKey, string column) =>
        changed.Contains($"{tableKey}.{column}");

    private static void Add(
        ICollection<DeploymentOperation> operations,
        int phase,
        string sortKey,
        string description,
        string sql,
        bool destructive) =>
        operations.Add(new DeploymentOperation(phase, sortKey, description, sql, destructive));
}
