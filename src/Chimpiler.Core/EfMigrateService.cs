using System.Reflection;

namespace Chimpiler.Core;

/// <summary>
/// Main service for the ef-migrate command
/// </summary>
public class EfMigrateService
{
    private readonly Action<string>? _logger;

    public EfMigrateService(Action<string>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Executes the ef-migrate command with the specified options
    /// </summary>
    public void Execute(EfMigrateOptions options)
    {
        Log($"ef-migrate runtime EF Core: {EfCoreVersionInfo.RuntimeVersion} (major {EfCoreVersionInfo.RuntimeMajor})");

        try
        {
            var executor = new EfMigrateIsolatedExecutor(_logger);
            executor.Execute(options);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException($"Failed to load assembly: {ex.Message}", ex);
        }
    }

    internal void ExecuteInCurrentContext(EfMigrateOptions options)
    {
        Log($"Loading assembly: {options.AssemblyPath}");

        // Load the assembly
        Assembly assembly;
        try
        {
            assembly = DbContextDiscovery.LoadAssembly(options.AssemblyPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load assembly: {ex.Message}", ex);
        }

        Log($"Assembly loaded: {assembly.FullName}");

        // Discover DbContexts
        List<Type> dbContexts;
        if (!string.IsNullOrEmpty(options.ContextTypeName))
        {
            Log($"Looking for specific context: {options.ContextTypeName}");
            var dbContext = DbContextDiscovery.FindDbContext(assembly, options.ContextTypeName);
            if (dbContext == null)
            {
                throw new InvalidOperationException(
                    $"DbContext type '{options.ContextTypeName}' not found in assembly {assembly.FullName}");
            }
            dbContexts = new List<Type> { dbContext };
        }
        else
        {
            Log("Discovering all DbContexts in assembly...");
            dbContexts = DbContextDiscovery.DiscoverDbContexts(assembly);
            if (dbContexts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No DbContext types found in assembly {assembly.FullName}");
            }
        }

        Log($"Found {dbContexts.Count} DbContext(s): {string.Join(", ", dbContexts.Select(t => t.Name))}");

        // Create output directory
        if (!Directory.Exists(options.OutputDirectory))
        {
            Log($"Creating output directory: {options.OutputDirectory}");
            Directory.CreateDirectory(options.OutputDirectory);
        }

        // Generate DACPACs
        var generator = new DacpacGenerator(options.Verbose ? _logger : null);
        var results = new List<string>();

        foreach (var dbContext in dbContexts)
        {
            var fileName = DacpacNaming.GetDacpacFileName(dbContext);
            var outputPath = Path.Combine(options.OutputDirectory, fileName);

            Log($"\nProcessing {dbContext.Name} -> {fileName}");

            try
            {
                generator.GenerateDacpac(dbContext, outputPath);
                results.Add(outputPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to generate DACPAC for {dbContext.FullName}: {ex.Message}", ex);
            }
        }

        Log($"\nSuccessfully generated {results.Count} DACPAC(s):");
        foreach (var result in results)
        {
            Log($"  - {result}");
        }
    }

    private void Log(string message)
    {
        _logger?.Invoke(message);
    }
}
