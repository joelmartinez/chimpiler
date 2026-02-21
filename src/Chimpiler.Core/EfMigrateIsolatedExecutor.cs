using System.Reflection;
using System.Runtime.Loader;

namespace Chimpiler.Core;

internal sealed class EfMigrateIsolatedExecutor
{
    private readonly Action<string>? _logger;

    public EfMigrateIsolatedExecutor(Action<string>? logger = null)
    {
        _logger = logger;
    }

    public void Execute(EfMigrateOptions options)
    {
        var coreAssemblyPath = typeof(EfMigrateIsolatedExecutor).Assembly.Location;
        var fullTargetAssemblyPath = Path.GetFullPath(options.AssemblyPath);
        if (!File.Exists(fullTargetAssemblyPath))
        {
            throw new FileNotFoundException($"Assembly not found: {fullTargetAssemblyPath}");
        }

        var loadContext = new EfMigrateLoadContext(fullTargetAssemblyPath, coreAssemblyPath);
        try
        {
            var coreAssembly = loadContext.LoadFromAssemblyPath(coreAssemblyPath);
            var workerType = coreAssembly.GetType("Chimpiler.Core.EfMigrateWorker")
                ?? throw new InvalidOperationException("Failed to find ef-migrate worker type.");
            var runMethod = workerType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Failed to find ef-migrate worker entry point.");
            runMethod.Invoke(null, new object?[]
            {
                fullTargetAssemblyPath,
                options.ContextTypeName,
                options.OutputDirectory,
                options.Verbose,
                options.Verbose ? _logger : null
            });
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
        finally
        {
            loadContext.Unload();
        }
    }
}

internal sealed class EfMigrateLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _targetResolver;
    private readonly AssemblyDependencyResolver _coreResolver;

    public EfMigrateLoadContext(string targetAssemblyPath, string coreAssemblyPath)
        : base(isCollectible: true)
    {
        _targetResolver = new AssemblyDependencyResolver(targetAssemblyPath);
        _coreResolver = new AssemblyDependencyResolver(coreAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null &&
            (assemblyName.Name.StartsWith("Microsoft.SqlServer.Dac", StringComparison.Ordinal) ||
             assemblyName.Name.StartsWith("Microsoft.Data.Tools", StringComparison.Ordinal) ||
             assemblyName.Name.Equals("Microsoft.Data.SqlClient", StringComparison.Ordinal)))
        {
            return null;
        }

        var path = _targetResolver.ResolveAssemblyToPath(assemblyName)
            ?? _coreResolver.ResolveAssemblyToPath(assemblyName);

        return path == null ? null : LoadFromAssemblyPath(path);
    }
}

public static class EfMigrateWorker
{
    public static void Run(
        string assemblyPath,
        string? contextTypeName,
        string outputDirectory,
        bool verbose,
        Action<string>? logger)
    {
        var service = new EfMigrateService(logger);
        service.ExecuteInCurrentContext(new EfMigrateOptions
        {
            AssemblyPath = assemblyPath,
            ContextTypeName = contextTypeName,
            OutputDirectory = outputDirectory,
            Verbose = verbose
        });
    }
}
