using System.Data;
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
    private readonly Action<string>? _logger;

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
        // Create schemas
        var schemas = new HashSet<string>();
        foreach (var entityType in efModel.GetEntityTypes())
        {
            var schema = entityType.GetSchema() ?? "dbo";
            schemas.Add(schema);
        }

        foreach (var schema in schemas.Where(s => s != "dbo"))
        {
            Log($"  Creating schema: {schema}");
            CreateSchema(model, schema);
        }

        // Create tables
        foreach (var entityType in efModel.GetEntityTypes())
        {
            Log($"  Creating table: {entityType.GetTableName()}");
            CreateTable(model, entityType);
        }

        // Create foreign keys
        foreach (var entityType in efModel.GetEntityTypes())
        {
            CreateForeignKeys(model, entityType);
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
        for (int i = 0; i < properties.Count; i++)
        {
            var property = properties[i];
            var columnDef = GetColumnDefinition(property);
            sb.Append($"    {columnDef}");

            if (i < properties.Count - 1)
            {
                sb.AppendLine(",");
            }
            else
            {
                sb.AppendLine();
            }
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
                        (property.ClrType == typeof(int) || property.ClrType == typeof(long));

        var sb = new StringBuilder();
        sb.Append($"[{columnName}] {columnType}");

        if (isIdentity)
        {
            sb.Append(" IDENTITY(1,1)");
        }

        sb.Append(isNullable ? " NULL" : " NOT NULL");

        return sb.ToString();
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

    private void CreateForeignKeys(TSqlModel model, IEntityType entityType)
    {
        var schema = entityType.GetSchema() ?? "dbo";
        var tableName = entityType.GetTableName();

        foreach (var foreignKey in entityType.GetForeignKeys())
        {
            var principalTable = foreignKey.PrincipalEntityType.GetTableName();
            var principalSchema = foreignKey.PrincipalEntityType.GetSchema() ?? "dbo";

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

    private void Log(string message)
    {
        _logger?.Invoke(message);
    }
}
