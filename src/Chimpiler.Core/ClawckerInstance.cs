namespace Chimpiler.Core;

/// <summary>
/// Represents a Clawcker instance configuration
/// </summary>
public class ClawckerInstance
{
    /// <summary>
    /// The unique name of the instance
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The Docker container name
    /// </summary>
    public string ContainerName => $"clawcker-{Name}";

    /// <summary>
    /// The port on which the OpenClaw gateway is exposed
    /// </summary>
    public int Port { get; set; } = 18789;

    /// <summary>
    /// The path where instance configuration is stored
    /// </summary>
    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>
    /// The path where the workspace is stored
    /// </summary>
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether the instance has been created and configured
    /// </summary>
    public bool IsCreated { get; set; }

    /// <summary>
    /// Creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
