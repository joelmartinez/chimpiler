using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.SqlServer.Dac.Model;

namespace Chimpiler.Core;

/// <summary>
/// Generates DACPAC files from EF Core DbContext models
/// </summary>
public class DacpacGenerator
{
    private const string JsonColumnType = "nvarchar(max)";

    private readonly Action<string>? _logger;
    private Type? _currentDbContextType;

    public DacpacGenerator(Action<string>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generates a DACPAC for a specific DbContext type
    /// </summary>
    public void GenerateDacpac(Type dbContextType, string outputPath)
    {
        Log($"Generating DACPAC for {dbContextType.Name}...");

        _currentDbContextType = dbContextType;

        // Get the database name
        var databaseName = DacpacNaming.GetDatabaseName(dbContextType);

        // Create a TSqlModel
        using var sqlModel = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions
        {
            // Use case-insensitive collation by default
            Collation = "SQL_Latin1_General_CP1_CI_AS"
        });

        // Create and use the DbContext to get the model
        using (var context = CreateDbContext(dbContextType))
        {
            var model = context.Model;

            // Generate schema objects from the EF Core model
            GenerateSchemaObjects(sqlModel, model, databaseName);
        }

        // Save the DACPAC
        Log($"Writing DACPAC to {outputPath}...");
        
        // Write the model to a DACPAC file
        Microsoft.SqlServer.Dac.DacPackageExtensions.BuildPackage(
            outputPath,
            sqlModel,
            new Microsoft.SqlServer.Dac.PackageMetadata
            {
                Name = databaseName,
                Description = $"Generated from {dbContextType.FullName}",
                Version = "1.0.0.0"
            });

        Log($"Successfully generated {outputPath}");
    }

    private DbContext CreateDbContext(Type dbContextType)
    {
        try
        {
            // Try to create an instance using parameterless constructor
            var instance = Activator.CreateInstance(dbContextType);
            if (instance == null)
            {
                throw new InvalidOperationException($"Failed to create instance of {dbContextType.FullName}");
            }
            return (DbContext)instance;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to instantiate DbContext type {dbContextType.FullName}. " +
                $"Ensure it has a parameterless constructor or configure OnConfiguring. Error: {ex.Message}",
                ex);
        }
    }

    private void GenerateSchemaObjects(TSqlModel model, IModel efModel, string databaseName)
    {
        // Separate entities into tables and views
        var allEntities = efModel.GetEntityTypes().ToList();
        var tables = allEntities
            .Where(e => !ViewSqlGenerator.IsView(e))
            .Where(e => !e.IsOwned())
            .Where(e => !string.IsNullOrEmpty(e.GetTableName()))
            .ToList();
        var views = allEntities.Where(e => ViewSqlGenerator.IsView(e)).ToList();

        // Create schemas
        var schemas = new HashSet<string>();
        foreach (var entityType in allEntities)
        {
            var schema = entityType.GetSchema() ?? entityType.GetViewSchema() ?? "dbo";
            schemas.Add(schema);
        }

        foreach (var schema in schemas.Where(s => s != "dbo"))
        {
            Log($"  Creating schema: {schema}");
            CreateSchema(model, schema);
        }

        // Create tables first (views may depend on them)
        foreach (var entityType in tables)
        {
            Log($"  Creating table: {entityType.GetTableName()}");
            CreateTable(model, entityType);
        }

        // Build a set of all table names that exist in the model, used to validate FK constraints.
        // Owned types mapped to JSON have no separate table and must not be referenced by FK constraints.
        var knownTableNames = new HashSet<string>(
            tables.Select(t => $"{t.GetSchema() ?? "dbo"}.{t.GetTableName()}"),
            StringComparer.OrdinalIgnoreCase);

        // Create foreign keys for tables
        foreach (var entityType in tables)
        {
            CreateForeignKeys(model, entityType, knownTableNames);
        }

        // Create views after tables
        if (views.Any())
        {
            Log($"  Creating {views.Count} view(s)");
            CreateViews(model, views);
        }
    }

    private void CreateSchema(TSqlModel model, string schemaName)
    {
        var schemaScript = $"CREATE SCHEMA [{schemaName}]";
        model.AddObjects(schemaScript);
    }

    private void CreateTable(TSqlModel model, IEntityType entityType)
    {
        var schema = entityType.GetSchema() ?? "dbo";
        var tableName = entityType.GetTableName();

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{schema}].[{tableName}] (");

        var properties = entityType.GetProperties().ToList();

        // JSON-owned types (OwnsOne/OwnsMany with ToJson) are not returned by GetProperties()
        // on the parent entity. They are stored as a single nvarchar(max) JSON column whose
        // name comes from GetContainerColumnName() on the owned entity type.
        // Collect unique top-level JSON column names for this table.
        var jsonColumns = entityType.Model.GetEntityTypes()
            .Where(e => e.IsOwned() && e.IsMappedToJson() && e.GetTableName() == tableName && (e.GetSchema() ?? "dbo") == schema)
            .Select(e => e.GetContainerColumnName())
            .Where(col => !string.IsNullOrEmpty(col))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList();

        var totalColumnItems = properties.Count + jsonColumns.Count;
        var itemIndex = 0;

        for (int i = 0; i < properties.Count; i++, itemIndex++)
        {
            var property = properties[i];
            var columnDef = GetColumnDefinition(property);
            sb.Append($"    {columnDef}");
            sb.AppendLine(itemIndex < totalColumnItems - 1 ? "," : "");
        }

        foreach (var jsonColumn in jsonColumns)
        {
            sb.Append($"    [{jsonColumn}] {JsonColumnType} NULL");
            sb.AppendLine(itemIndex < totalColumnItems - 1 ? "," : "");
            itemIndex++;
        }

        // Add primary key
        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey != null)
        {
            var keyColumns = string.Join(", ", primaryKey.Properties.Select(p => $"[{p.GetColumnName()}]"));
            // Include schema in constraint name to avoid collisions
            sb.AppendLine($"    CONSTRAINT [PK_{schema}_{tableName}] PRIMARY KEY ({keyColumns})");
        }

        sb.AppendLine(")");

        model.AddObjects(sb.ToString());

        // Create indexes
        CreateIndexes(model, entityType);
    }

    private string GetColumnDefinition(IProperty property)
    {
        var columnName = property.GetColumnName();
        var columnType = property.GetColumnType();
        var isNullable = property.IsNullable;
        var isIdentity = property.ValueGenerated == ValueGenerated.OnAdd && 
                        (property.ClrType == typeof(int) || property.ClrType == typeof(long)) &&
                        property.GetDefaultValue() == null &&
                        string.IsNullOrEmpty(property.GetDefaultValueSql());

        var sb = new StringBuilder();
        sb.Append($"[{columnName}] {columnType}");

        if (isIdentity)
        {
            sb.Append(" IDENTITY(1,1)");
        }

        sb.Append(isNullable ? " NULL" : " NOT NULL");

        // Emit a DEFAULT constraint so that deploying a new NOT NULL column onto an existing
        // table (which already has rows) does not fail.  Identity columns manage their own
        // value generation and must not receive an additional DEFAULT clause.
        if (!isIdentity)
        {
            var defaultConstraintSql = GetDefaultConstraintSql(property);
            if (defaultConstraintSql != null)
            {
                sb.Append($" DEFAULT ({defaultConstraintSql})");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the SQL expression to use inside a DEFAULT (...) constraint for the given
    /// property, or <c>null</c> if no default should be emitted.
    /// Resolution order:
    ///   1. Explicit SQL expression configured via <c>HasDefaultValueSql()</c>.
    ///   2. CLR value configured via <c>HasDefaultValue()</c>.
    ///   3. Reflection-based fallback: reads the CLR property initializer from a freshly
    ///      constructed instance of the entity class.  This covers the common case where a
    ///      developer adds a NOT NULL property with a C# default (e.g.
    ///      <c>public string Status { get; set; } = "pending";</c>) but forgets to call
    ///      <c>HasDefaultValue()</c> in <c>OnModelCreating</c>.
    /// </summary>
    private string? GetDefaultConstraintSql(IProperty property)
    {
        // 1. Explicit SQL default expression.
        var defaultValueSql = property.GetDefaultValueSql();
        if (!string.IsNullOrEmpty(defaultValueSql))
        {
            return defaultValueSql;
        }

        // 2. CLR default value set via HasDefaultValue().
        var defaultValue = property.GetDefaultValue();
        if (defaultValue != null)
        {
            return ConvertToSqlLiteral(defaultValue);
        }

        // 3. Reflection-based fallback for NOT NULL columns only.
        if (!property.IsNullable && property.PropertyInfo != null && !property.IsPrimaryKey())
        {
            return TryGetPropertyInitializerDefault(property);
        }

        return null;
    }

    /// <summary>
    /// Attempts to derive a SQL default value by creating an instance of the declaring CLR
    /// type and reading the property's initializer value via reflection.  Returns <c>null</c>
    /// when no stable default can be determined.
    /// </summary>
    private string? TryGetPropertyInitializerDefault(IProperty property)
    {
        try
        {
            // DateTime/DateTimeOffset initializers (e.g. DateTime.UtcNow) yield the value at
            // the moment of instantiation – not a stable SQL default.  Skip them so that the
            // developer can explicitly call HasDefaultValueSql("GETDATE()") instead.
            var underlyingType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
            {
                return null;
            }

            var entityClrType = property.DeclaringType.ClrType;
            if (entityClrType.GetConstructor(Type.EmptyTypes) == null)
            {
                return null;
            }

            var instance = Activator.CreateInstance(entityClrType);
            if (instance == null)
            {
                return null;
            }

            var value = property.PropertyInfo!.GetValue(instance);
            if (value == null)
            {
                return null;
            }

            // Ignore values that are the CLR type's zero/default (0, false, Guid.Empty, …)
            // because they are almost certainly not intentional database defaults.
            var clrTypeDefault = underlyingType.IsValueType ? Activator.CreateInstance(underlyingType) : null;
            if (value.Equals(clrTypeDefault))
            {
                return null;
            }

            // Ignore empty strings – they are rarely an intentional default constraint.
            if (value is string s && string.IsNullOrEmpty(s))
            {
                return null;
            }

            var literal = ConvertToSqlLiteral(value);
            if (literal != null)
            {
                Log($"  Using reflection-based default for [{property.DeclaringType.ClrType.Name}].[{property.Name}]: {literal}");
            }
            return literal;
        }
        catch
        {
            // If anything goes wrong (no parameterless ctor, property throws, etc.) just
            // skip the reflection default rather than crashing DACPAC generation.
            return null;
        }
    }

    /// <summary>
    /// Converts a CLR value to a SQL literal string suitable for use inside DEFAULT (...).
    /// Returns <c>null</c> for types that cannot be represented as a literal.
    /// </summary>
    private static string? ConvertToSqlLiteral(object value)
    {
        return value switch
        {
            string s    => $"'{s.Replace("'", "''")}'",
            bool b      => b ? "1" : "0",
            byte by     => by.ToString(CultureInfo.InvariantCulture),
            short sh    => sh.ToString(CultureInfo.InvariantCulture),
            int i       => i.ToString(CultureInfo.InvariantCulture),
            long l      => l.ToString(CultureInfo.InvariantCulture),
            decimal d   => d.ToString(CultureInfo.InvariantCulture),
            double dbl  => dbl.ToString("G17", CultureInfo.InvariantCulture),
            float f     => f.ToString("G9", CultureInfo.InvariantCulture),
            Guid g      => $"'{g}'",
            _           => null
        };
    }

    private void CreateIndexes(TSqlModel model, IEntityType entityType)
    {
        var schema = entityType.GetSchema() ?? "dbo";
        var tableName = entityType.GetTableName();

        foreach (var index in entityType.GetIndexes())
        {
            // Skip primary key - it's handled by the table definition
            var isPrimaryKeyIndex = index.Properties.SequenceEqual(entityType.FindPrimaryKey()?.Properties ?? Enumerable.Empty<IProperty>());
            if (isPrimaryKeyIndex)
            {
                continue;
            }

            var indexName = index.GetDatabaseName() ?? $"IX_{schema}_{tableName}_{string.Join("_", index.Properties.Select(p => p.Name))}";
            var columns = string.Join(", ", index.Properties.Select(p => $"[{p.GetColumnName()}]"));
            var unique = index.IsUnique ? "UNIQUE " : "";

            var indexScript = $"CREATE {unique}INDEX [{indexName}] ON [{schema}].[{tableName}] ({columns})";
            model.AddObjects(indexScript);
        }
    }

    private void CreateForeignKeys(TSqlModel model, IEntityType entityType, HashSet<string> knownTableNames)
    {
        var schema = entityType.GetSchema() ?? "dbo";
        var tableName = entityType.GetTableName();

        foreach (var foreignKey in entityType.GetForeignKeys())
        {
            var principalTable = foreignKey.PrincipalEntityType.GetTableName();
            var principalSchema = foreignKey.PrincipalEntityType.GetSchema() ?? "dbo";

            // Skip FK if the principal table is not in the model.
            // This happens when the principal is an owned type stored as a JSON column
            // rather than a separate table (e.g. OwnsOne(...).ToJson()).
            if (string.IsNullOrEmpty(principalTable) ||
                !knownTableNames.Contains($"{principalSchema}.{principalTable}"))
            {
                Log($"  Skipping FK from [{schema}].[{tableName}] to [{principalSchema}].[{(string.IsNullOrEmpty(principalTable) ? "(no table)" : principalTable)}]: principal table not in model");
                continue;
            }

            var fkName = foreignKey.GetConstraintName() ?? 
                        $"FK_{schema}_{tableName}_{principalSchema}_{principalTable}_{string.Join("_", foreignKey.Properties.Select(p => p.Name))}";

            var fkColumns = string.Join(", ", foreignKey.Properties.Select(p => $"[{p.GetColumnName()}]"));
            var pkColumns = string.Join(", ", foreignKey.PrincipalKey.Properties.Select(p => $"[{p.GetColumnName()}]"));

            var onDelete = foreignKey.DeleteBehavior switch
            {
                DeleteBehavior.Cascade => "ON DELETE CASCADE",
                DeleteBehavior.SetNull => "ON DELETE SET NULL",
                DeleteBehavior.Restrict => "ON DELETE NO ACTION",
                _ => "ON DELETE NO ACTION"
            };

            var fkScript = $@"ALTER TABLE [{schema}].[{tableName}] 
    ADD CONSTRAINT [{fkName}] FOREIGN KEY ({fkColumns}) 
    REFERENCES [{principalSchema}].[{principalTable}] ({pkColumns}) 
    {onDelete}";

            model.AddObjects(fkScript);
        }
    }

    private void CreateViews(TSqlModel model, List<IEntityType> views)
    {
        // We need a DbContext instance to generate the view SQL
        // Use the context type from the first view's annotation, or create a new instance
        using var context = CreateDbContext(_currentDbContextType!);
        
        var viewGenerator = new ViewSqlGenerator(_logger);

        foreach (var view in views)
        {
            var viewName = view.GetViewName();
            Log($"  Creating view: {viewName}");

            string? viewDdl = null;
            try
            {
                viewDdl = viewGenerator.GenerateViewDdl(view, context);
                Log($"Generated view DDL for {viewName}:\n{viewDdl}");
                model.AddObjects(viewDdl);
                
                // Create clustered index separately if needed
                var indexDdl = viewGenerator.GenerateClusteredIndexDdl(view);
                if (!string.IsNullOrEmpty(indexDdl))
                {
                    Log($"Generated index DDL for {viewName}:\n{indexDdl}");
                    model.AddObjects(indexDdl);
                }
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to generate view {viewName}: {ex.Message}";
                if (viewDdl != null)
                {
                    errorMsg += $"\nGenerated SQL was:\n{viewDdl}";
                }
                throw new InvalidOperationException(errorMsg, ex);
            }
        }
    }

    private void Log(string message)
    {
        _logger?.Invoke(message);
    }
}
