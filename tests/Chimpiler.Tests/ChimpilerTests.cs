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
    [InlineData(typeof(OrdersDbContext), "OrdersDb.dacpac")]
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
    [InlineData(typeof(OrdersDbContext), "OrdersDb")]
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
        var outputPath = Path.Combine(_tempOutputDir, "OrdersDb.dacpac");

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
        Assert.Contains(dacpacFiles, f => Path.GetFileName(f) == "OrdersDb.dacpac");
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
