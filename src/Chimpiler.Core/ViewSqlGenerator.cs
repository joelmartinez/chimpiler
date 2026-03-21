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
        var definitionExpr = entityType.FindAnnotation(ViewAnnotations.ViewDefinitionExpression)?.Value as LambdaExpression;

        if (lambda == null || contextType == null)
        {
            throw new InvalidOperationException(
                $"View {viewName} does not have a view definition. " +
                $"Use HasViewDefinition<TContext>() or HasViewSql() to define the view.");
        }

        // Generate SQL from the lambda
        var viewSql = GenerateSqlFromLambda(lambda, context, contextType, entityType, definitionExpr);
        viewSql = NormalizeProjectedColumns(viewSql, entityType, definitionExpr);

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

    private string GenerateSqlFromLambda(object lambda, DbContext context, Type contextType, IEntityType entityType, LambdaExpression? definitionExpr = null)
    {
        Exception? translationException = null;

        try
        {
            var queryable = InvokeViewDefinitionLambda(lambda, context);
            return GenerateSqlFromQueryable(queryable, entityType);
        }
        catch (Exception ex)
        {
            // Unwrap TargetInvocationException so we can inspect the real cause.
            translationException = ex is TargetInvocationException tie && tie.InnerException != null
                ? tie.InnerException
                : ex;
        }

        if (definitionExpr != null)
        {
            try
            {
                var noTrackingQueryable = InvokeNoTrackingViewDefinitionLambda(definitionExpr, context);
                var noTrackingSql = GenerateSqlFromQueryable(noTrackingQueryable, entityType);
                Log($"No-tracking retry succeeded for {entityType.Name}.");
                return noTrackingSql;
            }
            catch (Exception ex)
            {
                var noTrackingException = ex is TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException
                    : ex;
                Log($"No-tracking retry failed for {entityType.Name} ({noTrackingException.Message}).");
            }
        }

        // ToQueryString() failed (e.g. the projection includes an owned-JSON navigation
        // property whose collection members EF Core cannot translate to SQL).
        // Fall back to building the SELECT statement directly from the expression tree
        // and EF Core metadata – this handles the common pattern of projecting scalar columns
        // together with a ToJson()-mapped owned navigation.
        if (definitionExpr != null)
        {
            Log($"ToQueryString failed for {entityType.Name} ({translationException?.Message}); attempting expression-tree fallback.");
            var fallbackSql = TryGenerateSqlFromExpressionTree(definitionExpr, context, entityType);
            if (!string.IsNullOrEmpty(fallbackSql))
            {
                Log($"Expression-tree fallback succeeded for {entityType.Name}.");
                return fallbackSql;
            }
        }

        throw new InvalidOperationException(
            $"Failed to generate SQL from view definition lambda for {entityType.Name}: {translationException?.Message}",
            translationException);
    }

    private static object InvokeViewDefinitionLambda(object lambda, DbContext context)
    {
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

        return queryable;
    }

    private static object InvokeNoTrackingViewDefinitionLambda(LambdaExpression definitionExpr, DbContext context)
    {
        var rewrittenBody = new NoTrackingDbSetVisitor().Visit(definitionExpr.Body)
            ?? throw new InvalidOperationException("Failed to rewrite view definition expression.");

        var rewrittenLambda = Expression.Lambda(rewrittenBody, definitionExpr.Parameters);
        var queryable = rewrittenLambda.Compile().DynamicInvoke(context);
        if (queryable == null)
        {
            throw new InvalidOperationException("No-tracking lambda returned null.");
        }

        return queryable;
    }

    private static string GenerateSqlFromQueryable(object queryable, IEntityType entityType)
    {
        var queryableType = queryable.GetType();

        // Find the ToQueryString method - it's an instance method in EF Core 5+
        var toQueryStringMethod = queryableType.GetMethod("ToQueryString",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            Type.EmptyTypes,
            null);

        if (toQueryStringMethod != null)
        {
            var sql = toQueryStringMethod.Invoke(queryable, null) as string;
            if (!string.IsNullOrEmpty(sql))
            {
                return sql;
            }
        }

        // If instance method not found, try extension method approach
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

    /// <summary>
    /// Builds a SELECT statement for a simple single-table projection view by walking the
    /// stored <see cref="LambdaExpression"/> instead of asking EF Core to translate it.
    /// This handles projections that include owned-JSON navigation properties (ToJson()),
    /// which EF Core's LINQ translator cannot convert to SQL when the JSON type contains
    /// owned collection navigations.
    ///
    /// Supported shape:
    ///   <c>ctx =&gt; ctx.SourceSet.Select(e =&gt; new TView { Prop = e.Prop, … })</c>
    ///
    /// Returns <c>null</c> when the expression shape is not recognised (callers should
    /// then re-throw the original exception).
    /// </summary>
    internal string? TryGenerateSqlFromExpressionTree(
        LambdaExpression definitionExpr,
        DbContext context,
        IEntityType viewEntityType)
    {
        // ── 1. Unpack  ctx => <body>  ───────────────────────────────────────────
        var body = definitionExpr.Body;

        // ── 2. Find the .Select(selector) call and the source DbSet access ──────
        var (selectorLambda, sourceAccess) = ExtractSelectCall(body);
        if (selectorLambda == null || sourceAccess == null)
        {
            Log($"Expression-tree fallback: could not locate a Select() call for {viewEntityType.Name}.");
            return null;
        }

        // ── 3. Resolve source entity type from the DbSet<T> property ────────────
        if (sourceAccess.Member is not PropertyInfo dbSetProp)
            return null;

        var dbSetType = dbSetProp.PropertyType;
        if (!dbSetType.IsGenericType)
            return null;

        var sourceClrType = dbSetType.GetGenericArguments()[0];
        var sourceEntityType = viewEntityType.Model.FindEntityType(sourceClrType);
        if (sourceEntityType == null)
            return null;

        var sourceTable = sourceEntityType.GetTableName();
        if (string.IsNullOrEmpty(sourceTable))
            return null;

        var sourceSchema = sourceEntityType.GetSchema() ?? "dbo";

        // ── 4. Build the SELECT list from MemberInit bindings ───────────────────
        if (selectorLambda.Body is not MemberInitExpression memberInit)
            return null;

        if (selectorLambda.Parameters.Count == 0)
            return null;

        var sourceParam = selectorLambda.Parameters[0];
        var selectParts = new List<string>();

        foreach (var binding in memberInit.Bindings)
        {
            if (binding is not MemberAssignment assignment)
                return null; // unknown binding shape – bail

            var part = ResolveColumnRef(
                assignment.Expression,
                sourceParam,
                sourceEntityType,
                viewEntityType,
                assignment.Member.Name);

            if (part == null)
            {
                Log($"Expression-tree fallback: could not resolve binding for '{assignment.Member.Name}' in {viewEntityType.Name}; abandoning fallback.");
                return null;
            }

            selectParts.Add(part);
        }

        if (selectParts.Count == 0)
            return null;

        return $"SELECT {string.Join(", ", selectParts)}\nFROM [{sourceSchema}].[{sourceTable}]";
    }

    /// <summary>
    /// Walks the expression to find the innermost <c>.Select(selector)</c> call and
    /// returns the selector lambda together with the outermost source expression
    /// (the <c>ctx.DbSetProperty</c> member access).
    /// </summary>
    private static (LambdaExpression? selector, MemberExpression? source) ExtractSelectCall(Expression expr)
    {
        // We expect: ctx.Source.Select(e => new TView { … })
        // With possible intermediate Where/OrderBy/etc – for those we only support
        // the simple single-table case where the root of the chain is a MemberExpression.
        if (expr is not MethodCallExpression call || call.Method.Name != "Select" || call.Arguments.Count < 2)
            return (null, null);

        // Argument[1] is the selector, wrapped in a Quote node for Queryable.Select
        var selectorArg = call.Arguments[1];
        if (selectorArg is UnaryExpression unary && unary.NodeType == ExpressionType.Quote)
            selectorArg = unary.Operand;

        var selector = selectorArg as LambdaExpression;

        // Walk up the chain to find the root source MemberExpression
        var source = FindRootMemberAccess(call.Arguments[0]);

        return (selector, source);
    }

    private static MemberExpression? FindRootMemberAccess(Expression expr)
    {
        if (expr is MemberExpression m)
            return m;

        // Unwrap extension-method chains (Where, AsNoTracking, etc.)
        if (expr is MethodCallExpression mc && mc.Arguments.Count >= 1)
            return FindRootMemberAccess(mc.Arguments[0]);

        return null;
    }

    private sealed class NoTrackingDbSetVisitor : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            var visited = base.VisitMember(node);

            if (visited is not MemberExpression memberExpr)
                return visited;

            if (!memberExpr.Type.IsGenericType || memberExpr.Type.GetGenericTypeDefinition() != typeof(DbSet<>))
                return memberExpr;

            var entityType = memberExpr.Type.GetGenericArguments()[0];
            var asNoTrackingMethod = typeof(EntityFrameworkQueryableExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(EntityFrameworkQueryableExtensions.AsNoTracking) &&
                            m.IsGenericMethodDefinition &&
                            m.GetParameters().Length == 1)
                .MakeGenericMethod(entityType);

            return Expression.Call(asNoTrackingMethod, memberExpr);
        }
    }

    /// <summary>
    /// Resolves a single member-binding source expression to its SQL column reference
    /// string (e.g. <c>[ColumnName]</c> or <c>[SourceCol] AS [ViewCol]</c>).
    /// Returns <c>null</c> if the expression cannot be resolved.
    /// </summary>
    private string? ResolveColumnRef(
        Expression expr,
        ParameterExpression sourceParam,
        IEntityType sourceEntityType,
        IEntityType viewEntityType,
        string viewMemberName)
    {
        // Unwrap implicit casts introduced by the C# compiler
        while (expr is UnaryExpression u &&
               (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
        {
            expr = u.Operand;
        }

        // We only handle direct member access on the source parameter: e.Property
        if (expr is not MemberExpression memberAccess || memberAccess.Expression != sourceParam)
            return null;

        var sourceMemberName = memberAccess.Member.Name;

        // ── Scalar property ─────────────────────────────────────────────────────
        var sourceProperty = sourceEntityType.FindProperty(sourceMemberName);
        if (sourceProperty != null)
        {
            var sourceCol = sourceProperty.GetColumnName();
            var viewProperty = viewEntityType.FindProperty(viewMemberName);
            var viewCol = viewProperty?.GetColumnName() ?? viewMemberName;
            return sourceCol == viewCol
                ? $"[{sourceCol}]"
                : $"[{sourceCol}] AS [{viewCol}]";
        }

        // ── Owned-JSON navigation (ToJson()) ────────────────────────────────────
        var sourceNav = sourceEntityType.FindNavigation(sourceMemberName);
        if (sourceNav != null)
        {
            var targetType = sourceNav.TargetEntityType;
            if (targetType.IsMappedToJson())
            {
                var sourceJsonCol = targetType.GetContainerColumnName() ?? sourceMemberName;

                // Determine the column name on the view side
                var viewNav = viewEntityType.FindNavigation(viewMemberName);
                var viewJsonCol = viewNav?.TargetEntityType?.GetContainerColumnName() ?? viewMemberName;

                return sourceJsonCol == viewJsonCol
                    ? $"[{sourceJsonCol}]"
                    : $"[{sourceJsonCol}] AS [{viewJsonCol}]";
            }
        }

        return null;
    }

    private string NormalizeProjectedColumns(string sql, IEntityType entityType, LambdaExpression? definitionExpr)
    {
        if (definitionExpr == null)
        {
            return sql;
        }

        var projectedColumns = GetProjectedViewColumnNames(definitionExpr, entityType);
        if (projectedColumns.Count == 0)
        {
            return sql;
        }

        if (!TrySplitSelectStatement(sql, out var selectClause, out var fromClause))
        {
            return sql;
        }

        var actualSelectParts = SplitSelectColumns(selectClause);
        if (actualSelectParts.Count < projectedColumns.Count)
        {
            return sql;
        }

        var normalizedParts = new List<string>(projectedColumns.Count);
        var changed = actualSelectParts.Count != projectedColumns.Count;

        for (var i = 0; i < projectedColumns.Count; i++)
        {
            var normalizedPart = EnsureProjectionAlias(actualSelectParts[i], projectedColumns[i]);
            normalizedParts.Add(normalizedPart);

            if (!string.Equals(normalizedPart, actualSelectParts[i].Trim(), StringComparison.Ordinal))
            {
                changed = true;
            }
        }

        if (!changed)
        {
            return sql;
        }

        if (actualSelectParts.Count > projectedColumns.Count)
        {
            Log($"Projection normalization trimmed {actualSelectParts.Count - projectedColumns.Count} hidden column(s) from {entityType.Name}.");
        }

        return $"SELECT {string.Join(", ", normalizedParts)} {fromClause}";
    }

    private static bool TrySplitSelectStatement(string sql, out string selectClause, out string fromClause)
    {
        var match = Regex.Match(
            sql,
            @"^\s*SELECT\s+(?<select>.*?)\s+(?<from>FROM\s+.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success)
        {
            selectClause = string.Empty;
            fromClause = string.Empty;
            return false;
        }

        selectClause = match.Groups["select"].Value;
        fromClause = match.Groups["from"].Value.Trim();
        return true;
    }

    private List<string> GetProjectedViewColumnNames(LambdaExpression definitionExpr, IEntityType viewEntityType)
    {
        var projectionLambda = ExtractProjectionLambda(definitionExpr.Body);
        if (projectionLambda?.Body is not MemberInitExpression memberInit)
        {
            return [];
        }

        var projectedColumns = new List<string>(memberInit.Bindings.Count);
        foreach (var binding in memberInit.Bindings)
        {
            if (binding is not MemberAssignment assignment)
            {
                return [];
            }

            projectedColumns.Add(GetViewColumnName(viewEntityType, assignment.Member.Name));
        }

        return projectedColumns;
    }

    private static LambdaExpression? ExtractProjectionLambda(Expression expr)
    {
        if (expr is not MethodCallExpression call)
        {
            return null;
        }

        if (call.Method.Name == "Select" && call.Arguments.Count >= 2)
        {
            return UnwrapQuotedLambda(call.Arguments[1]);
        }

        if (call.Method.Name == "Join" && call.Arguments.Count >= 5)
        {
            return UnwrapQuotedLambda(call.Arguments[4]);
        }

        return null;
    }

    private static LambdaExpression? UnwrapQuotedLambda(Expression expr)
    {
        if (expr is UnaryExpression unary && unary.NodeType == ExpressionType.Quote)
        {
            expr = unary.Operand;
        }

        return expr as LambdaExpression;
    }

    private string EnsureProjectionAlias(string selectPart, string expectedColumnName)
    {
        var trimmedPart = selectPart.Trim();
        var actualOutputColumnName = ParseOutputColumnName(trimmedPart);
        if (string.Equals(actualOutputColumnName, expectedColumnName, StringComparison.OrdinalIgnoreCase))
        {
            return trimmedPart;
        }

        var expressionWithoutAlias = Regex.Replace(
            trimmedPart,
            @"\s+AS\s+\[[^\]]+\]\s*$",
            string.Empty,
            RegexOptions.IgnoreCase);

        return $"{expressionWithoutAlias} AS [{expectedColumnName}]";
    }

    private string? ParseOutputColumnName(string selectPart)
    {
        var aliasMatch = Regex.Match(selectPart, @"AS\s+\[([^\]]+)\]\s*$", RegexOptions.IgnoreCase);
        if (aliasMatch.Success)
        {
            return aliasMatch.Groups[1].Value;
        }

        var columnMatch = Regex.Match(selectPart, @"\[([^\]]+)\]\s*$");
        return columnMatch.Success
            ? columnMatch.Groups[1].Value
            : null;
    }

    private IEnumerable<string> GetExpectedViewColumnNames(IEntityType entityType)
    {
        foreach (var property in entityType.GetProperties())
        {
            yield return property.GetColumnName();
        }

        foreach (var navigation in entityType.GetNavigations())
        {
            if (!navigation.TargetEntityType.IsMappedToJson())
            {
                continue;
            }

            yield return GetViewColumnName(entityType, navigation.Name);
        }
    }

    private string GetViewColumnName(IEntityType viewEntityType, string memberName)
    {
        var property = viewEntityType.FindProperty(memberName);
        if (property != null)
        {
            return property.GetColumnName();
        }

        var navigation = viewEntityType.FindNavigation(memberName);
        if (navigation?.TargetEntityType.IsMappedToJson() == true)
        {
            return navigation.TargetEntityType.GetContainerColumnName() ?? memberName;
        }

        return memberName;
    }

    private void ValidateViewColumns(IEntityType entityType, string sql, string viewName)
    {
        // Get expected columns from the entity
        var expectedColumns = GetExpectedViewColumnNames(entityType)
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
