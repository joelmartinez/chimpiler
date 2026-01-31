using System.Security.Cryptography;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Chimpiler.Core;

/// <summary>
/// Service for managing OpenClaw instances via Docker (Clawcker)
/// </summary>
public class ClawckerService
{
    private readonly Action<string>? _log;
    private readonly string _instancesDirectory;
    private const string OPENCLAW_IMAGE = "ghcr.io/phioranex/openclaw-docker:latest";
    private const int DEFAULT_PORT = 18789;

    public ClawckerService(Action<string>? log = null)
    {
        _log = log;
        
        // Store instances in ~/.chimpiler/clawcker/
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _instancesDirectory = Path.Combine(homeDir, ".chimpiler", "clawcker");
        
        // Ensure the directory exists
        Directory.CreateDirectory(_instancesDirectory);
    }

    /// <summary>
    /// Checks if Docker is installed and available
    /// </summary>
    public bool IsDockerAvailable()
    {
        try
        {
            var result = RunCommand("docker", "--version", captureOutput: true);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if Docker daemon is running
    /// </summary>
    public bool IsDockerRunning()
    {
        try
        {
            var result = RunCommand("docker", "info", captureOutput: true);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a new OpenClaw instance
    /// </summary>
    public void CreateInstance(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Instance name cannot be empty", nameof(name));
        }

        // Validate instance name (alphanumeric, dashes, underscores)
        if (!IsValidInstanceName(name))
        {
            throw new ArgumentException(
                "Instance name must contain only letters, numbers, dashes, and underscores", 
                nameof(name));
        }

        var instanceDir = Path.Combine(_instancesDirectory, name);
        
        if (Directory.Exists(instanceDir))
        {
            throw new InvalidOperationException($"Instance '{name}' already exists");
        }

        LogInfo($"Creating new Clawcker instance: {name}");

        // Check Docker prerequisites
        if (!IsDockerAvailable())
        {
            throw new InvalidOperationException(
                "Docker is not installed. Please install Docker from https://www.docker.com/get-started");
        }

        if (!IsDockerRunning())
        {
            throw new InvalidOperationException(
                "Docker daemon is not running. Please start Docker and try again");
        }

        // Create instance directory structure
        Directory.CreateDirectory(instanceDir);
        var configDir = Path.Combine(instanceDir, "config");
        var workspaceDir = Path.Combine(instanceDir, "workspace");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(workspaceDir);

        // Generate a secure gateway token
        var gatewayToken = GenerateSecureToken();

        // Create instance metadata
        var instance = new ClawckerInstance
        {
            Name = name,
            Port = DEFAULT_PORT,
            ConfigPath = configDir,
            WorkspacePath = workspaceDir,
            GatewayToken = gatewayToken,
            IsCreated = true,
            CreatedAt = DateTime.UtcNow
        };

        // Save instance metadata
        SaveInstanceMetadata(instance);

        LogInfo("Pulling OpenClaw Docker image...");
        LogInfo("(This may take a few minutes on first run)");
        LogInfo("");
        LogInfo("--- Docker Output ---");
        
        // Pull the Docker image
        var pullResult = RunCommand("docker", $"pull {OPENCLAW_IMAGE}");
        
        LogInfo("--- End Docker Output ---");
        LogInfo("");
        
        if (pullResult.ExitCode != 0)
        {
            // Clean up on failure
            Directory.Delete(instanceDir, true);
            throw new InvalidOperationException("Failed to pull OpenClaw Docker image");
        }

        LogInfo($"✓ Instance '{name}' created successfully");
        LogInfo($"  Configuration: {configDir}");
        LogInfo($"  Workspace: {workspaceDir}");
        LogInfo("");
        LogInfo($"Next steps:");
        LogInfo($"  1. Run 'chimpiler clawcker up {name}' to start the instance");
        LogInfo($"  2. Run 'chimpiler clawcker talk {name}' to open the web UI");
    }

    /// <summary>
    /// Starts an OpenClaw instance
    /// </summary>
    public void StartInstance(string name)
    {
        var instance = LoadInstanceMetadata(name);
        if (instance == null)
        {
            throw new InvalidOperationException($"Instance '{name}' not found. Create it first with 'chimpiler clawcker new {name}'");
        }

        LogInfo($"Starting Clawcker instance: {name}");

        // Check if container already exists
        var containerExists = CheckContainerExists(instance.ContainerName);
        
        if (containerExists)
        {
            // Check if it's running
            var isRunning = IsContainerRunning(instance.ContainerName);
            if (isRunning)
            {
                LogInfo($"Instance '{name}' is already running");
                LogInfo($"Access it at: http://localhost:{instance.Port}");
                return;
            }
            else
            {
                // Start existing container
                LogInfo("Starting existing container...");
                var startResult = RunCommand("docker", $"start {instance.ContainerName}");
                if (startResult.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to start container '{instance.ContainerName}'");
                }
            }
        }
        else
        {
            // Create and start new container
            LogInfo("Creating and starting container...");
            
            var dockerArgs = $"run -d " +
                $"--name {instance.ContainerName} " +
                $"--restart unless-stopped " +
                $"-e OPENCLAW_GATEWAY_TOKEN={instance.GatewayToken} " +
                $"-v \"{instance.ConfigPath}:/home/node/.openclaw\" " +
                $"-v \"{instance.WorkspacePath}:/home/node/.openclaw/workspace\" " +
                $"-p {instance.Port}:{DEFAULT_PORT} " +
                $"{OPENCLAW_IMAGE} " +
                $"gateway --allow-unconfigured";

            var runResult = RunCommand("docker", dockerArgs);
            if (runResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to start container '{instance.ContainerName}'");
            }
        }

        LogInfo($"✓ Instance '{name}' is now running");
        LogInfo($"  Access the web UI at: http://localhost:{instance.Port}/?token={instance.GatewayToken}");
        LogInfo($"  Container name: {instance.ContainerName}");
        LogInfo("");
        LogInfo($"To open the web UI in your browser, run:");
        LogInfo($"  chimpiler clawcker talk {name}");
    }

    /// <summary>
    /// Opens the web UI for an instance in the default browser
    /// </summary>
    public void OpenWebUI(string name)
    {
        var instance = LoadInstanceMetadata(name);
        if (instance == null)
        {
            throw new InvalidOperationException($"Instance '{name}' not found. Create it first with 'chimpiler clawcker new {name}'");
        }

        // Check if container is running
        if (!IsContainerRunning(instance.ContainerName))
        {
            throw new InvalidOperationException(
                $"Instance '{name}' is not running. Start it first with 'chimpiler clawcker up {name}'");
        }

        var url = $"http://localhost:{instance.Port}/?token={instance.GatewayToken}";
        LogInfo($"Opening web UI for instance '{name}'...");
        LogInfo($"URL: {url}");

        try
        {
            OpenBrowser(url);
            LogInfo("✓ Web UI opened in your default browser");
        }
        catch (Exception ex)
        {
            LogInfo($"Could not automatically open browser: {ex.Message}");
            LogInfo($"Please manually open: {url}");
        }
    }

    /// <summary>
    /// Stops a running OpenClaw instance
    /// </summary>
    public void StopInstance(string name)
    {
        var instance = LoadInstanceMetadata(name);
        if (instance == null)
        {
            throw new InvalidOperationException($"Instance '{name}' not found. Create it first with 'chimpiler clawcker new {name}'");
        }

        LogInfo($"Stopping Clawcker instance: {name}");

        // Check if container exists
        if (!CheckContainerExists(instance.ContainerName))
        {
            LogInfo($"Instance '{name}' has no running container");
            return;
        }

        // Check if it's running
        if (!IsContainerRunning(instance.ContainerName))
        {
            LogInfo($"Instance '{name}' is already stopped");
            return;
        }

        // Stop the container
        LogInfo("Stopping container...");
        var stopResult = RunCommand("docker", $"stop {instance.ContainerName}", captureOutput: true);
        if (stopResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to stop container '{instance.ContainerName}'");
        }

        LogInfo($"✓ Instance '{name}' stopped");
        LogInfo($"To start it again, run: chimpiler clawcker up {name}");
    }

    /// <summary>
    /// Lists all Clawcker instances
    /// </summary>
    public List<ClawckerInstance> ListInstances()
    {
        var instances = new List<ClawckerInstance>();
        
        if (!Directory.Exists(_instancesDirectory))
        {
            return instances;
        }

        foreach (var dir in Directory.GetDirectories(_instancesDirectory))
        {
            var name = Path.GetFileName(dir);
            var instance = LoadInstanceMetadata(name);
            if (instance != null)
            {
                instances.Add(instance);
            }
        }

        return instances;
    }

    /// <summary>
    /// Gets the status of an instance
    /// </summary>
    public string GetInstanceStatus(string name)
    {
        var instance = LoadInstanceMetadata(name);
        if (instance == null)
        {
            return "not found";
        }

        if (!CheckContainerExists(instance.ContainerName))
        {
            return "created (not started)";
        }

        if (IsContainerRunning(instance.ContainerName))
        {
            return "running";
        }

        return "stopped";
    }

    #region Helper Methods

    private bool IsValidInstanceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
    }

    private void SaveInstanceMetadata(ClawckerInstance instance)
    {
        var metadataPath = GetMetadataPath(instance.Name);
        var json = JsonSerializer.Serialize(instance, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        File.WriteAllText(metadataPath, json);
    }

    private ClawckerInstance? LoadInstanceMetadata(string name)
    {
        var metadataPath = GetMetadataPath(name);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(metadataPath);
            return JsonSerializer.Deserialize<ClawckerInstance>(json);
        }
        catch
        {
            return null;
        }
    }

    private string GetMetadataPath(string name)
    {
        return Path.Combine(_instancesDirectory, name, "instance.json");
    }

    private bool CheckContainerExists(string containerName)
    {
        var result = RunCommand("docker", $"ps -a --filter name=^/{containerName}$ --format {{{{.Names}}}}", captureOutput: true);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output) && result.Output.Trim() == containerName;
    }

    private bool IsContainerRunning(string containerName)
    {
        var result = RunCommand("docker", $"ps --filter name=^/{containerName}$ --format {{{{.Names}}}}", captureOutput: true);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output) && result.Output.Trim() == containerName;
    }

    private (int ExitCode, string Output) RunCommand(string command, string arguments, bool captureOutput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            UseShellExecute = false,
            CreateNoWindow = captureOutput
        };

        if (!captureOutput)
        {
            // Stream output to console in real-time
            startInfo.RedirectStandardOutput = false;
            startInfo.RedirectStandardError = false;
        }

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException($"Failed to start process: {command}");
        }

        string output = "";
        if (captureOutput)
        {
            output = process.StandardOutput.ReadToEnd();
        }

        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private void OpenBrowser(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Process.Start("xdg-open", url);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
        else
        {
            throw new PlatformNotSupportedException("Cannot determine platform to open browser");
        }
    }

    private void LogInfo(string message)
    {
        _log?.Invoke(message);
    }

    private string GenerateSecureToken()
    {
        // Generate a 64-character random token (256 bits of entropy)
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    #endregion
}
