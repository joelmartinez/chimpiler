namespace Chimpiler.Core;

/// <summary>
/// Options for generating DACPACs from EF Core DbContexts
/// </summary>
public class EfMigrateOptions
{
    /// <summary>
    /// Path to the compiled .NET assembly containing DbContext types
    /// </summary>
    public required string AssemblyPath { get; set; }

    /// <summary>
    /// Optional fully qualified type name of a specific DbContext to process
    /// If null, all DbContexts in the assembly will be processed
    /// </summary>
    public string? ContextTypeName { get; set; }

    /// <summary>
    /// Output directory for generated DACPAC files
    /// </summary>
    public string OutputDirectory { get; set; } = "./output";

    /// <summary>
    /// Enable verbose logging
    /// </summary>
    public bool Verbose { get; set; }
}
