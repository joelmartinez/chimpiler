namespace Chimpiler.Core;

/// <summary>
/// Utilities for DACPAC file naming
/// </summary>
public static class DacpacNaming
{
    /// <summary>
    /// Generates a DACPAC filename from a DbContext type name
    /// Strips the "DbContext" or "Context" suffix if present and appends ".dacpac"
    /// Checks for "DbContext" first to handle edge cases correctly
    /// </summary>
    public static string GetDacpacFileName(Type dbContextType)
    {
        var typeName = dbContextType.Name;
        
        // Strip "DbContext" suffix first (longest suffix first)
        if (typeName.EndsWith("DbContext", StringComparison.OrdinalIgnoreCase))
        {
            typeName = typeName.Substring(0, typeName.Length - "DbContext".Length);
        }
        // Then check for "Context" suffix
        else if (typeName.EndsWith("Context", StringComparison.OrdinalIgnoreCase))
        {
            typeName = typeName.Substring(0, typeName.Length - "Context".Length);
        }

        return $"{typeName}.dacpac";
    }

    /// <summary>
    /// Generates a database name from a DbContext type name
    /// Same logic as GetDacpacFileName but without the .dacpac extension
    /// </summary>
    public static string GetDatabaseName(Type dbContextType)
    {
        var fileName = GetDacpacFileName(dbContextType);
        return Path.GetFileNameWithoutExtension(fileName);
    }
}
