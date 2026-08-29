namespace Chimpiler.Dacpac;

public sealed record DacpacSchema(IReadOnlyList<DacpacTable> Tables);

public sealed record DacpacTable(
    string Schema,
    string Name,
    IReadOnlyList<DacpacColumn> Columns,
    DacpacKey? PrimaryKey,
    IReadOnlyList<DacpacKey> UniqueConstraints,
    IReadOnlyList<DacpacForeignKey> ForeignKeys,
    IReadOnlyList<DacpacIndex> Indexes)
{
    public string Key => $"{Schema}.{Name}";
}

public sealed record DacpacColumn(
    string Name,
    SqlServerType Type,
    bool IsNullable,
    string? DefaultExpression,
    IdentityDefinition? Identity);

public sealed record SqlServerType(
    string Name,
    int Length = 0,
    int Precision = 0,
    int Scale = 0,
    bool IsMax = false);

public sealed record IdentityDefinition(long Seed, int Increment);

public sealed record DacpacKey(string Name, IReadOnlyList<string> Columns);

public sealed record DacpacForeignKey(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    ReferentialAction OnDelete,
    ReferentialAction OnUpdate);

public sealed record DacpacIndex(
    string Name,
    bool IsUnique,
    IReadOnlyList<IndexColumn> Columns,
    IReadOnlyList<string> IncludedColumns);

public sealed record IndexColumn(string Name, bool Ascending);

public enum ReferentialAction
{
    NoAction,
    Cascade,
    SetNull,
    SetDefault
}

public sealed record DatabaseSchema(IReadOnlyList<DatabaseTable> Tables)
{
    public static DatabaseSchema Empty { get; } = new([]);
}

public sealed record DatabaseTable(
    string Schema,
    string Name,
    IReadOnlyList<DatabaseColumn> Columns,
    DatabaseKey? PrimaryKey,
    IReadOnlyList<DatabaseKey> UniqueConstraints,
    IReadOnlyList<DatabaseForeignKey> ForeignKeys,
    IReadOnlyList<DatabaseIndex> Indexes,
    bool HasRows = false)
{
    public string Key => $"{Schema}.{Name}";
}

public sealed record DatabaseColumn(
    string Name,
    string StoreType,
    bool IsNullable,
    DatabaseDefault? Default,
    IdentityDefinition? Identity);

public sealed record DatabaseDefault(string Sql, string Canonical);

public sealed record DatabaseKey(string Name, IReadOnlyList<string> Columns);

public sealed record DatabaseForeignKey(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    ReferentialAction OnDelete,
    ReferentialAction OnUpdate);

public sealed record DatabaseIndex(
    string Name,
    bool IsUnique,
    IReadOnlyList<IndexColumn> Columns,
    IReadOnlyList<string> IncludedColumns);

public sealed record DeploymentOperation(
    int Phase,
    string SortKey,
    string Description,
    string Sql,
    bool IsDestructive);

public sealed record DeploymentPlan(IReadOnlyList<DeploymentOperation> Operations)
{
    public bool HasDestructiveOperations => Operations.Any(operation => operation.IsDestructive);
}

public sealed record DacpacApplyOptions
{
    public required string DacpacPath { get; init; }
    public required string Provider { get; init; }
    public required string ConnectionString { get; init; }
    public bool DryRun { get; init; }
    public string? ScriptPath { get; init; }
    public bool AllowDestructive { get; init; }
}

public sealed record DeploymentResult(DeploymentPlan Plan, string Script, bool Applied);

public sealed class DacpacCompatibilityException(string message) : Exception(message);

public sealed class DestructiveChangesException(IReadOnlyList<DeploymentOperation> operations)
    : Exception(
        "Deployment contains destructive operations. Re-run with --allow-destructive after reviewing the generated plan:\n" +
        string.Join('\n', operations.Where(operation => operation.IsDestructive).Select(operation => $"- {operation.Description}")))
{
    public IReadOnlyList<DeploymentOperation> Operations { get; } = operations;
}
