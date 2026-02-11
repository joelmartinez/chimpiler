using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chimpiler.EfMigrate;

/// <summary>
/// Annotation keys for view metadata
/// </summary>
public static class ViewAnnotations
{
    public const string ViewDefinitionLambda = "Chimpiler:ViewDefinitionLambda";
    public const string ViewDefinitionContextType = "Chimpiler:ViewDefinitionContextType";
    public const string ViewSql = "Chimpiler:ViewSql";
    public const string WithSchemaBinding = "Chimpiler:WithSchemaBinding";
    public const string ClusteredIndexExpression = "Chimpiler:ClusteredIndexExpression";
}

/// <summary>
/// Fluent API extensions for defining views in Entity Framework Core
/// </summary>
public static class ViewExtensions
{
    /// <summary>
    /// Defines a view using a LINQ query that will be translated to SQL via EF Core's ToQueryString()
    /// </summary>
    /// <typeparam name="TEntity">The entity type that the view represents</typeparam>
    /// <typeparam name="TContext">The DbContext type that contains the source tables</typeparam>
    /// <param name="entityTypeBuilder">The entity type builder</param>
    /// <param name="queryBuilder">A lambda that takes a DbContext and returns an IQueryable of the view entity</param>
    /// <returns>The entity type builder for chaining</returns>
    public static EntityTypeBuilder<TEntity> HasViewDefinition<TEntity, TContext>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TContext, IQueryable<TEntity>>> queryBuilder)
        where TEntity : class
        where TContext : DbContext
    {
        // Compile the expression to get the actual Func
        var compiledFunc = queryBuilder.Compile();
        
        // Store the lambda and context type as annotations
        entityTypeBuilder.HasAnnotation(ViewAnnotations.ViewDefinitionLambda, compiledFunc);
        entityTypeBuilder.HasAnnotation(ViewAnnotations.ViewDefinitionContextType, typeof(TContext));
        
        return entityTypeBuilder;
    }

    /// <summary>
    /// Marks the view to be created WITH SCHEMABINDING (required for indexed views)
    /// </summary>
    /// <typeparam name="TEntity">The entity type</typeparam>
    /// <param name="entityTypeBuilder">The entity type builder</param>
    /// <returns>The entity type builder for chaining</returns>
    public static EntityTypeBuilder<TEntity> WithSchemaBinding<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder)
        where TEntity : class
    {
        entityTypeBuilder.HasAnnotation(ViewAnnotations.WithSchemaBinding, true);
        return entityTypeBuilder;
    }

    /// <summary>
    /// Defines a clustered index on the view (required as the first index for indexed views)
    /// </summary>
    /// <typeparam name="TEntity">The entity type</typeparam>
    /// <param name="entityTypeBuilder">The entity type builder</param>
    /// <param name="indexExpression">Expression identifying the columns for the clustered index</param>
    /// <returns>The entity type builder for chaining</returns>
    public static EntityTypeBuilder<TEntity> HasClusteredIndex<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Expression<Func<TEntity, object>> indexExpression)
        where TEntity : class
    {
        // Store the expression as an annotation
        // The expression will be parsed later during DACPAC generation
        entityTypeBuilder.HasAnnotation(ViewAnnotations.ClusteredIndexExpression, indexExpression);
        return entityTypeBuilder;
    }

    /// <summary>
    /// Provides raw SQL for the view definition (escape hatch for complex views)
    /// </summary>
    /// <typeparam name="TEntity">The entity type</typeparam>
    /// <param name="entityTypeBuilder">The entity type builder</param>
    /// <param name="sql">The SQL SELECT statement for the view (without CREATE VIEW wrapper)</param>
    /// <returns>The entity type builder for chaining</returns>
    public static EntityTypeBuilder<TEntity> HasViewSql<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string sql)
        where TEntity : class
    {
        entityTypeBuilder.HasAnnotation(ViewAnnotations.ViewSql, sql);
        return entityTypeBuilder;
    }
}
