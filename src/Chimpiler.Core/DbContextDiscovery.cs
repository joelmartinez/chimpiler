using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Chimpiler.Core;

/// <summary>
/// Discovers DbContext types in a given assembly
/// </summary>
public class DbContextDiscovery
{
    /// <summary>
    /// Discovers all DbContext types in the specified assembly
    /// </summary>
    public static List<Type> DiscoverDbContexts(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(DbContext).IsAssignableFrom(t))
            .ToList();
    }

    /// <summary>
    /// Finds a specific DbContext by fully qualified type name
    /// </summary>
    public static Type? FindDbContext(Assembly assembly, string fullyQualifiedTypeName)
    {
        var dbContexts = DiscoverDbContexts(assembly);
        return dbContexts.FirstOrDefault(t => t.FullName == fullyQualifiedTypeName);
    }

    /// <summary>
    /// Loads an assembly from the specified path
    /// </summary>
    public static Assembly LoadAssembly(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Assembly not found: {assemblyPath}");
        }

        // Load the assembly from the specified path
        return Assembly.LoadFrom(assemblyPath);
    }
}
