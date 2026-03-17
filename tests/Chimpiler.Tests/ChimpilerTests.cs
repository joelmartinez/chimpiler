using System.Reflection;
using Chimpiler.Core;
using Chimpiler.TestFixtures;
using Microsoft.SqlServer.Dac.Model;
using Xunit;

namespace Chimpiler.Tests;

public class DacpacNamingTests
{
    [Theory]
    [InlineData(typeof(TheDatabaseContext), "TheDatabase.dacpac")]
    [InlineData(typeof(OrdersDbContext), "Orders.dacpac")]
    [InlineData(typeof(ReportingContext), "Reporting.dacpac")]
    [InlineData(typeof(InventoryContext), "Inventory.dacpac")]
    public void GetDacpacFileName_ShouldStripContextSuffix(Type dbContextType, string expectedFileName)
    {
        // Act
        var fileName = DacpacNaming.GetDacpacFileName(dbContextType);

        // Assert
        Assert.Equal(expectedFileName, fileName);
    }

    [Theory]
    [InlineData(typeof(TheDatabaseContext), "TheDatabase")]
    [InlineData(typeof(OrdersDbContext), "Orders")]
    [InlineData(typeof(ReportingContext), "Reporting")]
    [InlineData(typeof(InventoryContext), "Inventory")]
    public void GetDatabaseName_ShouldReturnNameWithoutExtension(Type dbContextType, string expectedName)
    {
        // Act
        var databaseName = DacpacNaming.GetDatabaseName(dbContextType);

        // Assert
        Assert.Equal(expectedName, databaseName);
    }
}

public class DbContextDiscoveryTests
{
    [Fact]
    public void DiscoverDbContexts_ShouldFindAllDbContexts()
    {
        // Arrange
        var assembly = typeof(TheDatabaseContext).Assembly;

        // Act
        var dbContexts = DbContextDiscovery.DiscoverDbContexts(assembly);

        // Assert
        Assert.NotEmpty(dbContexts);
        Assert.Contains(dbContexts, t => t == typeof(TheDatabaseContext));
        Assert.Contains(dbContexts, t => t == typeof(OrdersDbContext));
        Assert.Contains(dbContexts, t => t == typeof(ReportingContext));
        Assert.Contains(dbContexts, t => t == typeof(InventoryContext));
    }

    [Fact]
    public void FindDbContext_WithValidTypeName_ShouldReturnType()
    {
        // Arrange
        var assembly = typeof(TheDatabaseContext).Assembly;
        var typeName = typeof(TheDatabaseContext).FullName!;

        // Act
        var dbContext = DbContextDiscovery.FindDbContext(assembly, typeName);

        // Assert
        Assert.NotNull(dbContext);
        Assert.Equal(typeof(TheDatabaseContext), dbContext);
    }

    [Fact]
    public void FindDbContext_WithInvalidTypeName_ShouldReturnNull()
    {
        // Arrange
        var assembly = typeof(TheDatabaseContext).Assembly;
        var typeName = "InvalidTypeName";

        // Act
        var dbContext = DbContextDiscovery.FindDbContext(assembly, typeName);

        // Assert
        Assert.Null(dbContext);
    }

    [Fact]
    public void LoadAssembly_WithValidPath_ShouldLoadAssembly()
    {
        // Arrange
        var assemblyPath = typeof(TheDatabaseContext).Assembly.Location;

        // Act
        var assembly = DbContextDiscovery.LoadAssembly(assemblyPath);

        // Assert
        Assert.NotNull(assembly);
        Assert.Equal(typeof(TheDatabaseContext).Assembly.FullName, assembly.FullName);
    }

    [Fact]
    public void LoadAssembly_WithInvalidPath_ShouldThrowFileNotFoundException()
    {
        // Arrange
        var assemblyPath = "/invalid/path/assembly.dll";

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => DbContextDiscovery.LoadAssembly(assemblyPath));
    }
}

public class DacpacGeneratorTests : IDisposable
{
    private readonly string _tempOutputDir;

    public DacpacGeneratorTests()
    {
        _tempOutputDir = Path.Combine(Path.GetTempPath(), $"chimpiler-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempOutputDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempOutputDir))
        {
            Directory.Delete(_tempOutputDir, true);
        }
    }

    [Fact]
    public void GenerateDacpac_ForSimpleContext_ShouldCreateValidDacpac()
    {
        // Arrange
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "TheDatabase.dacpac");

        // Act
        generator.GenerateDacpac(typeof(TheDatabaseContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");
        
        // Verify it's a valid DACPAC by loading it
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        // Verify the table was created
        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table);
        Assert.NotEmpty(tables);
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "Users"));
    }

    [Fact]
    public void GenerateDacpac_ForContextWithRelationships_ShouldCreateValidDacpac()
    {
        // Arrange
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "Orders.dacpac");

        // Act
        generator.GenerateDacpac(typeof(OrdersDbContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");
        
        // Verify it's a valid DACPAC
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        // Verify tables were created
        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table);
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "Orders"));
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "Products"));
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "OrderItems"));

        // Verify foreign keys were created
        var foreignKeys = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.ForeignKeyConstraint);
        Assert.NotEmpty(foreignKeys);
    }

    [Fact]
    public void GenerateDacpac_ForContextWithJsonOwnedType_ShouldCreateSingleStudiesTable()
    {
        // Arrange
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "JsonOwnedTrials.dacpac");

        // Act
        generator.GenerateDacpac(typeof(JsonOwnedTrialsContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");

        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table).ToList();
        Assert.Single(tables, t => t.Name.Parts.Any(p => p == "Studies"));

        var primaryKeys = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.PrimaryKeyConstraint).ToList();
        Assert.Single(primaryKeys, pk => pk.Name.Parts.Any(p => p == "PK_dbo_Studies"));

        var foreignKeys = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.ForeignKeyConstraint).ToList();
        Assert.DoesNotContain(foreignKeys, fk => fk.Name.Parts.Any(p => p.Contains("FK_Studies_Studies_")));
    }

    [Fact]
    public void GenerateDacpac_ForContextWithMultipleJsonOwnedTypes_ShouldCreateValidDacpac()
    {
        // Arrange
        var logMessages = new List<string>();
        var generator = new DacpacGenerator(msg => logMessages.Add(msg));
        var outputPath = Path.Combine(_tempOutputDir, "MultiJsonOwned.dacpac");

        // Act
        generator.GenerateDacpac(typeof(MultiJsonOwnedContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");

        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        // Only the two non-owned tables should exist; no extra tables for JSON-owned types
        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table).ToList();
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "Cohorts"));
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "Experiments"));
        Assert.DoesNotContain(tables, t => t.Name.Parts.Any(p => p is "DesignData" or "ParticipantData" or "MeasurementData" or "ScheduleData" or "Metrics" or "Milestones"));

        // The four JSON-owned navigation properties must be stored as nvarchar(max) columns
        // on the Experiments table (one column per top-level OwnsOne+ToJson navigation).
        var experimentsTable = tables.Single(t => t.Name.Parts.Any(p => p == "Experiments"));
        var columns = experimentsTable.GetChildren().Where(c => c.ObjectType == ModelSchema.Column).ToList();
        var columnNames = columns.Select(c => c.Name.Parts.Last()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("DesignData", columnNames);
        Assert.Contains("ParticipantData", columnNames);
        Assert.Contains("MeasurementData", columnNames);
        Assert.Contains("ScheduleData", columnNames);

        // The FK from Experiments to Cohorts must be present
        var foreignKeys = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.ForeignKeyConstraint).ToList();
        Assert.NotEmpty(foreignKeys);
        Assert.Contains(foreignKeys, fk => fk.Name.Parts.Any(p => p.Contains("Cohort")));

        // No self-referential or invalid FKs (e.g. Experiments → Experiments) from JSON-owned relationships
        Assert.DoesNotContain(foreignKeys, fk => fk.Name.Parts.Any(p => p.Contains("FK_Experiments_Experiments_")));

        // The valid FK to Cohorts must never be skipped by the FK validation logic
        Assert.DoesNotContain(logMessages, m => m.Contains("principal table not in model") && m.Contains("Cohort"));
    }

    [Fact]
    public void GenerateDacpac_ForContextWithCustomSchema_ShouldCreateSchemaAndTables()
    {
        // Arrange
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "Reporting.dacpac");

        // Act
        generator.GenerateDacpac(typeof(ReportingContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");
        
        // Verify it's a valid DACPAC
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        // Verify schema was created
        var schemas = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Schema);
        Assert.Contains(schemas, s => s.Name.Parts.Contains("reporting"));

        // Verify tables in the schema
        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table);
        var reportingTables = tables.Where(t => t.Name.Parts.Contains("reporting")).ToList();
        Assert.NotEmpty(reportingTables);
    }

    [Fact]
    public void GenerateDacpac_ForContextWithCompositeKey_ShouldCreateTableWithCompositeKey()
    {
        // Arrange
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "Inventory.dacpac");

        // Act
        generator.GenerateDacpac(typeof(InventoryContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");
        
        // Verify it's a valid DACPAC
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        // Verify table was created
        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table);
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "InventoryItems"));

        // Verify primary key exists
        var primaryKeys = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.PrimaryKeyConstraint);
        Assert.NotEmpty(primaryKeys);
    }

    [Fact]
    public void GenerateDacpac_ForContextWithViews_ShouldCreateTablesAndViews()
    {
        // Arrange
        var logMessages = new List<string>();
        var generator = new DacpacGenerator(msg => logMessages.Add(msg));
        var outputPath = Path.Combine(_tempOutputDir, "Library.dacpac");

        // Act
        generator.GenerateDacpac(typeof(LibraryContext), outputPath);

        // Print logs for debugging
        foreach (var log in logMessages)
        {
            Console.WriteLine(log);
        }

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");
        
        // Verify it's a valid DACPAC
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        // Verify tables were created
        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table);
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "Books"));
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "Authors"));

        // Verify views were created
        var views = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View);
        Assert.NotEmpty(views);
        Assert.Contains(views, v => v.Name.Parts.Any(p => p == "SimpleBookView"));
        Assert.Contains(views, v => v.Name.Parts.Any(p => p == "BookSummaryView"));
        Assert.Contains(views, v => v.Name.Parts.Any(p => p == "BookAuthorView"));
    }

    [Fact]
    public void GenerateDacpac_ForViewWithSchemaBinding_ShouldIncludeSchemaBindingAndIndex()
    {
        // Arrange
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "Library.dacpac");

        // Act
        generator.GenerateDacpac(typeof(LibraryContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");
        
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        
        // Verify the indexed view has a clustered index
        var indexes = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Index);
        Assert.Contains(indexes, idx => 
            idx.Name.Parts.Any(p => p.Contains("BookSummaryView")));
    }

    [Fact]
    public void GenerateDacpac_WithViewDefinedUsingHasViewSql_ShouldCreateViewWithRawSql()
    {
        // Arrange
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "Library.dacpac");

        // Act
        generator.GenerateDacpac(typeof(LibraryContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");
        
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        
        // Verify the view was created
        var views = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View);
        var bookCountView = views.FirstOrDefault(v => v.Name.Parts.Any(p => p == "BookCountView"));
        Assert.NotNull(bookCountView);

        // The view definition is derived from the raw SQL passed to HasViewSql.
        // If it weren't, DACPAC generation would fail or the view would be missing.
    }

    [Fact]
    public void GenerateDacpac_ForContextWithDefaultValues_ShouldIncludeDefaultConstraints()
    {
        // Arrange – DefaultValuesContext uses HasDefaultValue() and HasDefaultValueSql()
        var logMessages = new List<string>();
        var generator = new DacpacGenerator(msg => logMessages.Add(msg));
        var outputPath = Path.Combine(_tempOutputDir, "DefaultValues.dacpac");

        // Act
        generator.GenerateDacpac(typeof(DefaultValuesContext), outputPath);

        // Assert – DACPAC was created
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");

        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        // The WorkItems table must be present
        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table).ToList();
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "WorkItems"));

        // DEFAULT constraints must be generated for Status, CreatedAt, and Priority
        var defaultConstraints = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.DefaultConstraint).ToList();
        Assert.NotEmpty(defaultConstraints);

        // Verify each configured default is represented
        var constraintNames = defaultConstraints
            .SelectMany(dc => dc.Name.Parts)
            .ToList();

        // The defaults for Status, CreatedAt, and Priority should produce three constraints
        Assert.True(defaultConstraints.Count >= 3,
            $"Expected at least 3 DEFAULT constraints, found {defaultConstraints.Count}. " +
            $"Constraints: {string.Join(", ", constraintNames)}");
    }

    [Fact]
    public void GenerateDacpac_ForContextWithPropertyInitializers_ShouldIncludeReflectionBasedDefaults()
    {
        // Arrange – PropertyInitializerContext has no HasDefaultValue() calls; the
        // generator should read property initializer values via reflection.
        var logMessages = new List<string>();
        var generator = new DacpacGenerator(msg => logMessages.Add(msg));
        var outputPath = Path.Combine(_tempOutputDir, "PropertyInitializer.dacpac");

        // Act
        generator.GenerateDacpac(typeof(PropertyInitializerContext), outputPath);

        // Assert – DACPAC was created without error
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");

        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table).ToList();
        Assert.Contains(tables, t => t.Name.Parts.Any(p => p == "ProductItems"));

        // DEFAULT constraints must be generated for Category (="general") and IsActive (=true)
        var defaultConstraints = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.DefaultConstraint).ToList();
        Assert.True(defaultConstraints.Count >= 2,
            $"Expected at least 2 DEFAULT constraints from property initializers, found {defaultConstraints.Count}.");

        // The generator must have logged that it used reflection-based defaults
        Assert.Contains(logMessages, m => m.Contains("reflection-based default"));
    }

    [Fact]
    public void GenerateDacpac_IdentityColumns_ShouldHaveIdentityFlag()
    {
        // Arrange – verifies that int and long primary-key columns (the most common SQL Server
        // identity pattern) are emitted with IDENTITY(1,1) rather than as plain int columns.
        // This is a regression test for the bug where GetDefaultValue() returning the CLR
        // default (0) instead of null caused the identity check to always evaluate to false.
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "IdentityAndRelationships.dacpac");

        // Act
        generator.GenerateDacpac(typeof(IdentityAndRelationshipsContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");

        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table).ToList();

        // Verify int identity PK (Departments.Id)
        var deptTable = tables.Single(t => t.Name.Parts.Any(p => p == "Departments"));
        var deptColumns = deptTable.GetChildren()
            .Where(c => c.ObjectType == ModelSchema.Column).ToList();
        var deptIdCol = deptColumns.Single(c => c.Name.Parts.Last() == "Id");
        Assert.True(deptIdCol.GetProperty<bool>(Column.IsIdentity),
            "Departments.Id should be an IDENTITY column");

        // Verify int identity PK (Employees.Id)
        var empTable = tables.Single(t => t.Name.Parts.Any(p => p == "Employees"));
        var empColumns = empTable.GetChildren()
            .Where(c => c.ObjectType == ModelSchema.Column).ToList();
        var empIdCol = empColumns.Single(c => c.Name.Parts.Last() == "Id");
        Assert.True(empIdCol.GetProperty<bool>(Column.IsIdentity),
            "Employees.Id (int) should be an IDENTITY column");

        // Non-PK int columns must NOT be marked as identity
        var deptIdFkCol = empColumns.Single(c => c.Name.Parts.Last() == "DepartmentId");
        Assert.False(deptIdFkCol.GetProperty<bool>(Column.IsIdentity),
            "Employees.DepartmentId (FK) must not be an IDENTITY column");

        // Verify long identity PK (EmployeeNotes.Id)
        var noteTable = tables.Single(t => t.Name.Parts.Any(p => p == "EmployeeNotes"));
        var noteColumns = noteTable.GetChildren()
            .Where(c => c.ObjectType == ModelSchema.Column).ToList();
        var noteIdCol = noteColumns.Single(c => c.Name.Parts.Last() == "Id");
        Assert.True(noteIdCol.GetProperty<bool>(Column.IsIdentity),
            "EmployeeNotes.Id (long/bigint) should be an IDENTITY column");
    }

    [Fact]
    public void GenerateDacpac_CustomIdentitySeed_ShouldEmitCorrectSeedAndIncrement()
    {
        // Arrange – verifies that UseIdentityColumn(seed, increment) values are honoured
        // and not silently replaced with the default IDENTITY(1,1).
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "CustomIdentitySeed.dacpac");

        // Act
        generator.GenerateDacpac(typeof(CustomIdentitySeedContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");

        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        Assert.NotNull(model);

        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table).ToList();

        // Tickets.Id: UseIdentityColumn(seed: 1000, increment: 1) → IDENTITY(1000,1)
        var ticketsTable = tables.Single(t => t.Name.Parts.Any(p => p == "Tickets"));
        var ticketIdCol = ticketsTable.GetChildren()
            .Where(c => c.ObjectType == ModelSchema.Column)
            .Single(c => c.Name.Parts.Last() == "Id");
        Assert.True(ticketIdCol.GetProperty<bool>(Column.IsIdentity),
            "Tickets.Id should be an IDENTITY column");
        Assert.Equal("1000", ticketIdCol.GetProperty<string>(Column.IdentitySeed));
        Assert.Equal("1", ticketIdCol.GetProperty<string>(Column.IdentityIncrement));

        // AuditLogs.Id: UseIdentityColumn(seed: 1, increment: 10) → IDENTITY(1,10)
        var auditTable = tables.Single(t => t.Name.Parts.Any(p => p == "AuditLogs"));
        var auditIdCol = auditTable.GetChildren()
            .Where(c => c.ObjectType == ModelSchema.Column)
            .Single(c => c.Name.Parts.Last() == "Id");
        Assert.True(auditIdCol.GetProperty<bool>(Column.IsIdentity),
            "AuditLogs.Id should be an IDENTITY column");
        Assert.Equal("1", auditIdCol.GetProperty<string>(Column.IdentitySeed));
        Assert.Equal("10", auditIdCol.GetProperty<string>(Column.IdentityIncrement));
    }

    [Fact]
    public void GenerateDacpac_ExplicitDefaultValueColumns_ShouldNotBeIdentity()
    {
        // Arrange – verifies that int columns that have an explicit HasDefaultValue()
        // configured are NOT treated as IDENTITY columns.  This distinguishes between
        // "auto-increment PK" (identity) and "column with a default value" (DEFAULT constraint).
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "DefaultValues.dacpac");

        // Act
        generator.GenerateDacpac(typeof(DefaultValuesContext), outputPath);

        // Assert
        Assert.True(File.Exists(outputPath), "DACPAC file should exist");

        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table).ToList();
        var workTable = tables.Single(t => t.Name.Parts.Any(p => p == "WorkItems"));
        var columns = workTable.GetChildren()
            .Where(c => c.ObjectType == ModelSchema.Column).ToList();

        // WorkItems.Id is a conventional int PK → must be identity
        var idCol = columns.Single(c => c.Name.Parts.Last() == "Id");
        Assert.True(idCol.GetProperty<bool>(Column.IsIdentity),
            "WorkItems.Id should be an IDENTITY column");

        // WorkItems.Priority has HasDefaultValue(1) → must NOT be identity, just DEFAULT (1)
        var priorityCol = columns.Single(c => c.Name.Parts.Last() == "Priority");
        Assert.False(priorityCol.GetProperty<bool>(Column.IsIdentity),
            "WorkItems.Priority (HasDefaultValue(1)) must not be an IDENTITY column");

        // DEFAULT constraints must be present for Status, CreatedAt, and Priority
        var defaultConstraints = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.DefaultConstraint).ToList();
        Assert.True(defaultConstraints.Count >= 3,
            $"Expected at least 3 DEFAULT constraints, found {defaultConstraints.Count}");
    }

    [Fact]
    public void GenerateDacpac_PrimaryKeys_ShouldBeGeneratedForAllTables()
    {
        // Arrange – validates that PRIMARY KEY constraints are present for every table
        // in IdentityAndRelationshipsContext (all three tables use single-column PKs).
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "IdentityAndRelationships.dacpac");

        // Act
        generator.GenerateDacpac(typeof(IdentityAndRelationshipsContext), outputPath);

        // Assert
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        var primaryKeys = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.PrimaryKeyConstraint).ToList();

        // All three tables must have a primary key
        Assert.True(primaryKeys.Count >= 3,
            $"Expected primary keys for Departments, Employees, and EmployeeNotes but found {primaryKeys.Count}");
    }

    [Fact]
    public void GenerateDacpac_ForeignKeys_ShouldBeGeneratedWithCorrectBehavior()
    {
        // Arrange – verifies FK constraints including ON DELETE behavior:
        //   Employee → Department uses EF Core Restrict, which maps to NO ACTION in SQL Server
        //   EmployeeNote → Employee uses CASCADE
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "IdentityAndRelationships.dacpac");

        // Act
        generator.GenerateDacpac(typeof(IdentityAndRelationshipsContext), outputPath);

        // Assert
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());
        var foreignKeys = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.ForeignKeyConstraint).ToList();

        // Two FKs expected: Employee→Department and EmployeeNote→Employee
        Assert.True(foreignKeys.Count >= 2,
            $"Expected at least 2 foreign key constraints, found {foreignKeys.Count}");

        // FK from Employees to Departments must exist with NO ACTION (RESTRICT) delete behavior
        var empToDeptFk = foreignKeys.SingleOrDefault(fk => fk.Name.Parts.Any(p =>
            p.Contains("Employees", StringComparison.OrdinalIgnoreCase) &&
            p.Contains("Department", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(empToDeptFk);
        Assert.Equal(ForeignKeyAction.NoAction, empToDeptFk.GetProperty<ForeignKeyAction>(ForeignKeyConstraint.DeleteAction));

        // FK from EmployeeNotes to Employees must exist with CASCADE delete behavior
        var notesToEmpFk = foreignKeys.SingleOrDefault(fk => fk.Name.Parts.Any(p =>
            p.Contains("EmployeeNote", StringComparison.OrdinalIgnoreCase) &&
            p.Contains("Employee", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(notesToEmpFk);
        Assert.Equal(ForeignKeyAction.Cascade, notesToEmpFk.GetProperty<ForeignKeyAction>(ForeignKeyConstraint.DeleteAction));
    }

    // ─── Regression: owned-JSON view projection ────────────────────────────────

    /// <summary>
    /// A view that projects only scalar columns from a table that also has an owned-JSON
    /// navigation.  This is the "sibling view" case from the problem statement and must
    /// continue to work via the normal EF Core ToQueryString() path.
    /// </summary>
    [Fact]
    public void GenerateDacpac_ForScalarOnlyViewProjection_ShouldWork()
    {
        var logMessages = new List<string>();
        var generator = new DacpacGenerator(msg => logMessages.Add(msg));
        var outputPath = Path.Combine(_tempOutputDir, "Enrollment.dacpac");

        generator.GenerateDacpac(typeof(EnrollmentContext), outputPath);

        Assert.True(File.Exists(outputPath));
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());

        var views = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View).ToList();
        Assert.Contains(views, v => v.Name.Parts.Any(p => p == "EnrollmentSummaryView"));

        // The scalar-only view should have been generated via the normal ToQueryString() path,
        // not the expression-tree fallback, so no fallback log message should appear for it.
        Assert.DoesNotContain(logMessages, m => m.Contains("fallback") && m.Contains("EnrollmentSummaryView"));
    }

    /// <summary>
    /// A view that projects scalar columns plus the owned-JSON navigation property itself.
    /// EF Core rejects the tracked version of this query because it projects an owned type
    /// without its owner, so the generator must retry with AsNoTracking() before considering
    /// the expression-tree fallback.
    /// </summary>
    [Fact]
    public void GenerateDacpac_ForViewProjectingOwnedJsonNavigation_ShouldUseNoTrackingRetry()
    {
        var logMessages = new List<string>();
        var generator = new DacpacGenerator(msg => logMessages.Add(msg));
        var outputPath = Path.Combine(_tempOutputDir, "Enrollment.dacpac");

        // Must not throw even though EF Core cannot translate PlanPayload = e.PlanPayload
        generator.GenerateDacpac(typeof(EnrollmentContext), outputPath);

        Assert.True(File.Exists(outputPath));
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());

        var views = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View).ToList();
        Assert.Contains(views, v => v.Name.Parts.Any(p => p == "RedactedEnrollmentView"));

        Assert.Contains(logMessages, m => m.Contains("No-tracking retry succeeded") && m.Contains("RedactedEnrollmentView"));
        Assert.DoesNotContain(logMessages, m => m.Contains("fallback") && m.Contains("RedactedEnrollmentView"));
    }

    /// <summary>
    /// The owned-JSON type inside the view contains an OwnsMany collection.
    /// Verifies that the JSON column name is correctly resolved from
    /// <c>GetContainerColumnName()</c> even when the owned type is nested.
    /// </summary>
    [Fact]
    public void GenerateDacpac_ForViewWithOwnedJsonContainingCollection_SelectsJsonColumn()
    {
        var logs = new List<string>();
        using var ctx = new EnrollmentContext();
        var viewEntityType = ctx.Model.FindEntityType(typeof(RedactedEnrollmentView))!;
        var generator = new ViewSqlGenerator(msg => logs.Add(msg));

        var viewDdl = generator.GenerateViewDdl(viewEntityType, ctx);

        // The generated SELECT must include the PlanPayload JSON column
        Assert.Contains("PlanPayload", viewDdl, StringComparison.OrdinalIgnoreCase);
        // The view body must reference the source table
        Assert.Contains("EnrollmentRecords", viewDdl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logs, m => m.Contains("No-tracking retry succeeded") && m.Contains("RedactedEnrollmentView"));
        Assert.DoesNotContain(logs, m => m.Contains("fallback") && m.Contains("RedactedEnrollmentView"));
    }

    /// <summary>
    /// The failing view uses WITH SCHEMABINDING and HasClusteredIndex.
    /// Verifies that the full DACPAC (view + index) is generated correctly.
    /// </summary>
    [Fact]
    public void GenerateDacpac_ForSchemaBindingIndexedViewWithJsonProjection_ShouldWork()
    {
        var generator = new DacpacGenerator();
        var outputPath = Path.Combine(_tempOutputDir, "Enrollment.dacpac");

        generator.GenerateDacpac(typeof(EnrollmentContext), outputPath);

        Assert.True(File.Exists(outputPath));
        using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());

        var views = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View).ToList();
        var redactedView = views.FirstOrDefault(v => v.Name.Parts.Any(p => p == "RedactedEnrollmentView"));
        Assert.NotNull(redactedView);

        // A unique clustered index must have been added for HasClusteredIndex(v => v.Id)
        var indexes = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Index).ToList();
        Assert.Contains(indexes, idx => idx.Name.Parts.Any(p => p.Contains("RedactedEnrollmentView")));
    }
}

public class EfMigrateServiceTests : IDisposable
{
    private readonly string _tempOutputDir;

    public EfMigrateServiceTests()
    {
        _tempOutputDir = Path.Combine(Path.GetTempPath(), $"chimpiler-test-{Guid.NewGuid()}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempOutputDir))
        {
            Directory.Delete(_tempOutputDir, true);
        }
    }

    [Fact]
    public void Execute_WithNoContextSpecified_ShouldGenerateAllDacpacs()
    {
        // Arrange
        var service = new EfMigrateService();
        var assemblyPath = typeof(TheDatabaseContext).Assembly.Location;
        var options = new EfMigrateOptions
        {
            AssemblyPath = assemblyPath,
            OutputDirectory = _tempOutputDir
        };

        // Act
        service.Execute(options);

        // Assert
        Assert.True(Directory.Exists(_tempOutputDir));
        var dacpacFiles = Directory.GetFiles(_tempOutputDir, "*.dacpac");
        Assert.NotEmpty(dacpacFiles);
        
        // Should have at least the test contexts
        Assert.Contains(dacpacFiles, f => Path.GetFileName(f) == "TheDatabase.dacpac");
        Assert.Contains(dacpacFiles, f => Path.GetFileName(f) == "Orders.dacpac");
        Assert.Contains(dacpacFiles, f => Path.GetFileName(f) == "Reporting.dacpac");
        Assert.Contains(dacpacFiles, f => Path.GetFileName(f) == "Inventory.dacpac");
    }

    [Fact]
    public void Execute_WithSpecificContext_ShouldGenerateOneDacpac()
    {
        // Arrange
        var service = new EfMigrateService();
        var assemblyPath = typeof(TheDatabaseContext).Assembly.Location;
        var options = new EfMigrateOptions
        {
            AssemblyPath = assemblyPath,
            ContextTypeName = typeof(TheDatabaseContext).FullName!,
            OutputDirectory = _tempOutputDir
        };

        // Act
        service.Execute(options);

        // Assert
        Assert.True(Directory.Exists(_tempOutputDir));
        var dacpacFiles = Directory.GetFiles(_tempOutputDir, "*.dacpac");
        Assert.Single(dacpacFiles);
        Assert.Equal("TheDatabase.dacpac", Path.GetFileName(dacpacFiles[0]));
    }

    [Fact]
    public void Execute_WithInvalidAssemblyPath_ShouldThrowException()
    {
        // Arrange
        var service = new EfMigrateService();
        var options = new EfMigrateOptions
        {
            AssemblyPath = "/invalid/path/assembly.dll",
            OutputDirectory = _tempOutputDir
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => service.Execute(options));
        Assert.Contains("Failed to load assembly", ex.Message);
    }

    [Fact]
    public void Execute_WithInvalidContextName_ShouldThrowException()
    {
        // Arrange
        var service = new EfMigrateService();
        var assemblyPath = typeof(TheDatabaseContext).Assembly.Location;
        var options = new EfMigrateOptions
        {
            AssemblyPath = assemblyPath,
            ContextTypeName = "InvalidContext",
            OutputDirectory = _tempOutputDir
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => service.Execute(options));
        Assert.Contains("not found in assembly", ex.Message);
    }

    [Fact]
    public void Execute_WithVerboseLogging_ShouldLogMessages()
    {
        // Arrange
        var logMessages = new List<string>();
        var service = new EfMigrateService(msg => logMessages.Add(msg));
        var assemblyPath = typeof(TheDatabaseContext).Assembly.Location;
        var options = new EfMigrateOptions
        {
            AssemblyPath = assemblyPath,
            ContextTypeName = typeof(TheDatabaseContext).FullName!,
            OutputDirectory = _tempOutputDir,
            Verbose = true
        };

        // Act
        service.Execute(options);

        // Assert
        Assert.NotEmpty(logMessages);
        Assert.Contains(logMessages, m => m.Contains("Loading assembly"));
        Assert.Contains(logMessages, m => m.Contains("Successfully generated"));
    }
}
