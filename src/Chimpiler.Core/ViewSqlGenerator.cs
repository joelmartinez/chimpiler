using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Chimpiler.EfMigrate;

namespace Chimpiler.Core;

/// <summary>
/// Generates SQL DDL for views from EF Core entity metadata
/// </summary>
public class ViewSqlGenerator
{
    private readonly Action<string>? _logger;

    public ViewSqlGenerator(Action<string>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks if an entity type represents a view
    /// </summary>
    public static bool IsView(IEntityType entityType)
    {
        return entityType.GetViewName() != null;
    }

    /// <summary>
    /// Generates CREATE VIEW DDL for a view entity
    /// </summary>
    public string GenerateViewDdl(IEntityType entityType, DbContext context)
    {
        var viewName = entityType.GetViewName();
        var schema = entityType.GetViewSchema() ?? entityType.GetSchema() ?? "dbo";

        if (string.IsNullOrEmpty(viewName))
        {
            throw new InvalidOperationException($"Entity type {entityType.Name} is not configured as a view");
        }

        Log($"Generating view DDL for [{schema}].[{viewName}]");

        // Try to get raw SQL first (escape hatch)
        var rawSql = entityType.FindAnnotation(ViewAnnotations.ViewSql)?.Value as string;
        if (!string.IsNullOrEmpty(rawSql))
        {
            return GenerateViewDdlFromRawSql(entityType, schema, viewName, rawSql);
        }

        // Get the view definition lambda
        var lambda = entityType.FindAnnotation(ViewAnnotations.ViewDefinitionLambda)?.Value;
        var contextType = entityType.FindAnnotation(ViewAnnotations.ViewDefinitionContextType)?.Value as Type;

        if (lambda == null || contextType == null)
        {
            throw new InvalidOperationException(
                $"View {viewName} does not have a view definition. " +
                $"Use HasViewDefinition<TContext>() or HasViewSql() to define the view.");
        }

        // Generate SQL from the lambda
        var viewSql = GenerateSqlFromLambda(lambda, context, contextType, entityType);

        // Validate the SQL columns match the entity properties
        ValidateViewColumns(entityType, viewSql, viewName);

        return GenerateViewDdlFromSql(entityType, schema, viewName, viewSql);
    }

    /// <summary>
    /// Generates CREATE INDEX DDL for a view's clustered index (if specified)
    /// </summary>
    public string? GenerateClusteredIndexDdl(IEntityType entityType)
    {
        var viewName = entityType.GetViewName();
        var schema = entityType.GetViewSchema() ?? entityType.GetSchema() ?? "dbo";

        if (string.IsNullOrEmpty(viewName))
        {
            return null;
        }

        var indexExpression = entityType.FindAnnotation(ViewAnnotations.ClusteredIndexExpression)?.Value;
        if (indexExpression == null)
        {
            return null;
        }

        // Parse the expression to get column names
        var columns = ParseIndexExpression(indexExpression, entityType);
        if (columns.Count == 0)
        {
            Log($"Warning: Could not parse clustered index expression for view {viewName}");
            return null;
        }

        var indexName = $"UCIX_{viewName}_{string.Join("_", columns)}";
        var columnList = string.Join(", ", columns.Select(c => $"[{c}]"));

        return $"CREATE UNIQUE CLUSTERED INDEX [{indexName}] ON [{schema}].[{viewName}] ({columnList})";
    }

    private string GenerateViewDdlFromRawSql(IEntityType entityType, string schema, string viewName, string sql)
    {
        var sb = new StringBuilder();
        
        // Check for SCHEMABINDING
        var withSchemaBinding = entityType.FindAnnotation(ViewAnnotations.WithSchemaBinding)?.Value as bool? ?? false;

        sb.AppendLine($"CREATE VIEW [{schema}].[{viewName}]");
        if (withSchemaBinding)
        {
            sb.AppendLine("WITH SCHEMABINDING");
        }
        sb.AppendLine("AS");
        sb.Append(sql); // Don't add a final newline

        return sb.ToString();
    }

    private string GenerateViewDdlFromSql(IEntityType entityType, string schema, string viewName, string sql)
    {
        var sb = new StringBuilder();
        
        // Check for SCHEMABINDING
        var withSchemaBinding = entityType.FindAnnotation(ViewAnnotations.WithSchemaBinding)?.Value as bool? ?? false;

        sb.AppendLine($"CREATE VIEW [{schema}].[{viewName}]");
        if (withSchemaBinding)
        {
            sb.AppendLine("WITH SCHEMABINDING");
        }
        sb.AppendLine("AS");
        
        // Wrap the SQL to ensure it works with SCHEMABINDING
        // EF Core's ToQueryString() doesn't include schema names, so we need to be careful
        if (withSchemaBinding)
        {
            // For SCHEMABINDING, we need to ensure all table references include schema names
            sql = EnsureSchemaQualifiedTableNames(sql);
        }
        
        sb.Append(sql); // Don't add a final newline

        return sb.ToString();
    }

    private string EnsureSchemaQualifiedTableNames(string sql)
    {
        // This is a simple approach - EF Core's ToQueryString() typically outputs
        // table names as [TableName], so we need to ensure they are [schema].[TableName]
        // For the MVP, we'll use a simple regex to add [dbo]. prefix if schema is missing
        
        // Match [TableName] patterns that aren't already schema-qualified
        var pattern = @"\bFROM\s+\[([^\]\.]+)\](?!\s*\.)|\bJOIN\s+\[([^\]\.]+)\](?!\s*\.)";
        
        return Regex.Replace(sql, pattern, match =>
        {
            var tableName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var prefix = match.Value.StartsWith("FROM") ? "FROM" : "JOIN";
            return $"{prefix} [dbo].[{tableName}]";
        }, RegexOptions.IgnoreCase);
    }

    private List<string> ParseIndexExpression(object expression, IEntityType entityType)
    {
        var columns = new List<string>();

        if (expression is LambdaExpression lambda)
        {
            var body = lambda.Body;

            // Handle x => x.PropertyName (single property)
            if (body is MemberExpression memberExpr)
            {
                var property = entityType.FindProperty(memberExpr.Member.Name);
                if (property != null)
                {
                    columns.Add(property.GetColumnName());
                }
            }
            // Handle x => new { x.Prop1, x.Prop2 } (composite index)
            else if (body is NewExpression newExpr)
            {
                foreach (var arg in newExpr.Arguments)
                {
                    if (arg is MemberExpression argMember)
                    {
                        var property = entityType.FindProperty(argMember.Member.Name);
                        if (property != null)
                        {
                            columns.Add(property.GetColumnName());
                        }
                    }
                }
            }
            // Handle x => x.PropertyName where PropertyName needs conversion (e.g., object)
            else if (body is UnaryExpression unaryExpr && unaryExpr.Operand is MemberExpression unaryMember)
            {
                var property = entityType.FindProperty(unaryMember.Member.Name);
                if (property != null)
                {
                    columns.Add(property.GetColumnName());
                }
            }
        }

        return columns;
    }

    private string GenerateSqlFromLambda(object lambda, DbContext context, Type contextType, IEntityType entityType)
    {
        try
        {
            // Invoke the lambda to get the IQueryable
            var method = lambda.GetType().GetMethod("Invoke");
            if (method == null)
            {
                throw new InvalidOperationException("Could not find Invoke method on lambda");
            }

            var queryable = method.Invoke(lambda, new[] { context });
            if (queryable == null)
            {
                throw new InvalidOperationException("Lambda returned null");
            }

            // Use the EntityFrameworkQueryableExtensions.ToQueryString method
            // This is available in EF Core and works on IQueryable<T>
            var queryableType = queryable.GetType();
            
            // Find the ToQueryString method - it's an instance method in EF Core 5+
            var toQueryStringMethod = queryableType.GetMethod("ToQueryString", 
                BindingFlags.Public | BindingFlags.Instance, 
                null, 
                Type.EmptyTypes, 
                null);
            
            if (toQueryStringMethod != null)
            {
                // EF Core 5+ - ToQueryString is an instance method
                var sql = toQueryStringMethod.Invoke(queryable, null) as string;
                if (!string.IsNullOrEmpty(sql))
                {
                    return sql;
                }
            }

            // If instance method not found, try extension method approach
            // Find EntityFrameworkQueryableExtensions.ToQueryString
            var efAssembly = typeof(DbContext).Assembly;
            var extensionsType = efAssembly.GetTypes()
                .FirstOrDefault(t => t.Name == "EntityFrameworkQueryableExtensions" || 
                                   t.Name == "RelationalQueryableExtensions");
            
            if (extensionsType != null)
            {
                var extensionMethod = extensionsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "ToQueryString" && m.GetParameters().Length == 1);
                
                if (extensionMethod != null)
                {
                    // Make it generic if needed
                    if (extensionMethod.IsGenericMethodDefinition)
                    {
                        extensionMethod = extensionMethod.MakeGenericMethod(entityType.ClrType);
                    }
                    
                    var sql = extensionMethod.Invoke(null, new[] { queryable }) as string;
                    if (!string.IsNullOrEmpty(sql))
                    {
                        return sql;
                    }
                }
            }

            throw new InvalidOperationException("Could not find ToQueryString method");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to generate SQL from view definition lambda for {entityType.Name}: {ex.Message}",
                ex);
        }
    }

    private void ValidateViewColumns(IEntityType entityType, string sql, string viewName)
    {
        // Get expected columns from the entity
        var expectedColumns = entityType.GetProperties()
            .Select(p => p.GetColumnName())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Parse SQL to get actual columns
        // This is a simple regex-based parser for the SELECT clause
        var actualColumns = ParseSelectColumns(sql);

        // Compare
        var missing = expectedColumns.Except(actualColumns, StringComparer.OrdinalIgnoreCase).ToList();
        var extra = actualColumns.Except(expectedColumns, StringComparer.OrdinalIgnoreCase).ToList();

        if (missing.Any() || extra.Any())
        {
            var errorMsg = new StringBuilder();
            errorMsg.AppendLine($"View '{viewName}' column mismatch:");
            if (missing.Any())
            {
                errorMsg.AppendLine($"  Missing columns in SQL: {string.Join(", ", missing)}");
            }
            if (extra.Any())
            {
                errorMsg.AppendLine($"  Extra columns in SQL: {string.Join(", ", extra)}");
            }

            Log($"Warning: {errorMsg}");
            // For now, just log a warning instead of throwing
            // This allows for flexibility while still providing visibility
        }
    }

    private HashSet<string> ParseSelectColumns(string sql)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Find the SELECT clause (between SELECT and FROM)
        var selectPattern = @"SELECT\s+(.*?)\s+FROM";
        var match = Regex.Match(sql, selectPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success)
        {
            Log($"Warning: Could not parse SELECT clause from SQL");
            return columns;
        }

        var selectClause = match.Groups[1].Value;

        // Split by comma, handling nested expressions
        var columnParts = SplitSelectColumns(selectClause);

        foreach (var part in columnParts)
        {
            // Look for "AS [ColumnName]" or "[ColumnName]" patterns
            var asMatch = Regex.Match(part, @"AS\s+\[([^\]]+)\]", RegexOptions.IgnoreCase);
            if (asMatch.Success)
            {
                columns.Add(asMatch.Groups[1].Value);
                continue;
            }

            // Look for just [ColumnName] at the end
            var columnMatch = Regex.Match(part.Trim(), @"\[([^\]]+)\]$");
            if (columnMatch.Success)
            {
                columns.Add(columnMatch.Groups[1].Value);
            }
        }

        return columns;
    }

    private List<string> SplitSelectColumns(string selectClause)
    {
        // Simple split by comma, accounting for nested parentheses and brackets
        var parts = new List<string>();
        var current = new StringBuilder();
        int parenDepth = 0;
        int bracketDepth = 0;

        foreach (char c in selectClause)
        {
            if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
            else if (c == '[') bracketDepth++;
            else if (c == ']') bracketDepth--;
            else if (c == ',' && parenDepth == 0 && bracketDepth == 0)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString().Trim());
        }

        return parts;
    }

    private void Log(string message)
    {
        _logger?.Invoke(message);
    }
}
