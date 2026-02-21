using Chimpiler.Core;

namespace Chimpiler.Tests;

public class EfCrossVersionTests : IDisposable
{
    private readonly string _tempOutputDir;

    public EfCrossVersionTests()
    {
        _tempOutputDir = Path.Combine(Path.GetTempPath(), $"chimpiler-cross-version-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempOutputDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempOutputDir))
        {
            Directory.Delete(_tempOutputDir, true);
        }
    }

    [Theory]
    [InlineData("EfCore1002Fixture", "EfCore1002Fixture.FixtureDbContext")]
    [InlineData("EfCore1003Fixture", "EfCore1003Fixture.FixtureDbContext")]
    public void Execute_WithCrossPatchFixtures_ShouldGenerateDacpac(string fixtureName, string contextTypeName)
    {
        var fixture = EfVersionFixtureBuilder.BuildFixtureAssembly(fixtureName);
        var service = new EfMigrateService();

        service.Execute(new EfMigrateOptions
        {
            AssemblyPath = fixture.AssemblyPath,
            ContextTypeName = contextTypeName,
            OutputDirectory = _tempOutputDir
        });

        var dacpacPath = Path.Combine(_tempOutputDir, "Fixture.dacpac");
        Assert.True(File.Exists(dacpacPath));
    }

    [Fact]
    public void Execute_WithMajorMismatchFixture_ShouldThrowExplicitMajorMismatchError()
    {
        var fixture = EfVersionFixtureBuilder.BuildFixtureAssembly("EfCore9ReferenceMismatchFixture");
        var service = new EfMigrateService();

        var ex = Assert.Throws<InvalidOperationException>(() => service.Execute(new EfMigrateOptions
        {
            AssemblyPath = fixture.AssemblyPath,
            OutputDirectory = _tempOutputDir
        }));

        Assert.Contains("EF Core major version mismatch", ex.Message);
        Assert.Contains($"major {EfCoreVersionInfo.RuntimeMajor}", ex.Message);
    }

    [Fact]
    public void Execute_WithSameMajorButMissingDependency_ShouldThrowExplicitDependencyResolutionError()
    {
        var fixture = EfVersionFixtureBuilder.BuildFixtureAssembly("EfCore1002MissingDependencyFixture");
        var missingDependencyPath = Path.Combine(fixture.OutputDirectory, "MissingDependencyLib.dll");
        if (File.Exists(missingDependencyPath))
        {
            File.Delete(missingDependencyPath);
        }

        var service = new EfMigrateService();
        var ex = Assert.Throws<InvalidOperationException>(() => service.Execute(new EfMigrateOptions
        {
            AssemblyPath = fixture.AssemblyPath,
            ContextTypeName = "EfCore1002MissingDependencyFixture.FixtureDbContext",
            OutputDirectory = _tempOutputDir
        }));

        Assert.True(ex.Message.Contains("dependencies could not be resolved", StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"tool major {EfCoreVersionInfo.RuntimeMajor}", ex.Message);
    }
}
