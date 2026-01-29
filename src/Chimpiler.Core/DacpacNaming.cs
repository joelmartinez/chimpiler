namespace Chimpiler.Core;

/// <summary>
/// Utilities for DACPAC file naming
/// </summary>
public static class DacpacNaming
{
    /// <summary>
    /// Generates a DACPAC filename from a DbContext type name
    /// Strips the "Context" suffix if present and appends ".dacpac"
    /// </summary>
    public static string GetDacpacFileName(Type dbContextType)
    {
        var typeName = dbContextType.Name;
        
        // Strip "Context" suffix if present
        if (typeName.EndsWith("Context", StringComparison.OrdinalIgnoreCase))
        {
            typeName = typeName.Substring(0, typeName.Length - "Context".Length);
        }
        // Also strip "DbContext" suffix if present
        else if (typeName.EndsWith("DbContext", StringComparison.OrdinalIgnoreCase))
        {
            typeName = typeName.Substring(0, typeName.Length - "DbContext".Length);
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
