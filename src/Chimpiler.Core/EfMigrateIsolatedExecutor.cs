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
        var runtimeEfMajor = EfCoreVersionInfo.RuntimeMajor;
        var runtimeEfVersion = EfCoreVersionInfo.RuntimeVersion;
        int? targetEfMajor = null;
        Version? targetEfVersion = null;

        try
        {
            var targetAssembly = loadContext.LoadFromAssemblyPath(fullTargetAssemblyPath);
            var efCoreReference = targetAssembly.GetReferencedAssemblies()
                .FirstOrDefault(a => a.Name == "Microsoft.EntityFrameworkCore");
            if (efCoreReference?.Version != null)
            {
                targetEfVersion = efCoreReference.Version;
                targetEfMajor = efCoreReference.Version.Major;
            }

            if (targetEfMajor == null)
            {
                throw new InvalidOperationException(
                    "Unable to determine EF Core reference from the target assembly. Ensure it references Microsoft.EntityFrameworkCore.");
            }

            if (targetEfMajor.Value != runtimeEfMajor)
            {
                throw new InvalidOperationException(
                    $"EF Core major version mismatch: ef-migrate uses EF Core {runtimeEfVersion} (major {runtimeEfMajor}) but the target assembly references Microsoft.EntityFrameworkCore {targetEfVersion} (major {targetEfMajor}). Major versions must match.");
            }

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
            if (targetEfMajor == runtimeEfMajor && IsAssemblyResolutionFailure(ex.InnerException))
            {
                throw new InvalidOperationException(
                    $"Target assembly dependencies could not be resolved even though EF Core major versions match (tool major {runtimeEfMajor}, target major {targetEfMajor}). Ensure the target assembly output includes all transitive dependencies and matching runtime assets. Root error: {ex.InnerException.Message}",
                    ex.InnerException);
            }
            throw ex.InnerException;
        }
        catch (Exception ex) when (targetEfMajor == runtimeEfMajor && IsAssemblyResolutionFailure(ex))
        {
            throw new InvalidOperationException(
                $"Target assembly dependencies could not be resolved even though EF Core major versions match (tool major {runtimeEfMajor}, target major {targetEfMajor}). Ensure the target assembly output includes all transitive dependencies and matching runtime assets. Root error: {ex.Message}",
                ex);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static bool IsAssemblyResolutionFailure(Exception ex)
    {
        if (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return true;
        }

        if (ex is ReflectionTypeLoadException reflectionTypeLoadException)
        {
            return reflectionTypeLoadException.LoaderExceptions.Any(e => e is FileNotFoundException or FileLoadException or BadImageFormatException);
        }

        if (!string.IsNullOrEmpty(ex.Message) && ex.Message.Contains("Could not load file or assembly", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ex.InnerException != null && IsAssemblyResolutionFailure(ex.InnerException);
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
