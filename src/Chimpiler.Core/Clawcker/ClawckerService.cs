using System.Security.Cryptography;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Net.Http;

namespace Chimpiler.Core.Clawcker;

/// <summary>
/// Service for managing OpenClaw instances via Docker (Clawcker)
/// </summary>
public class ClawckerService
{
    private readonly Action<string>? _log;
    private readonly string _instancesDirectory;
    private const string OPENCLAW_IMAGE = "ghcr.io/phioranex/openclaw-docker:latest";
    private const int BASE_PORT = 18789;
    private const int CONTAINER_PORT = 18789;

    public ClawckerService(Action<string>? log = null)
    {
        _log = log;
        
        // Store instances in ./.clawcker/ (current working directory)
        _instancesDirectory = Path.Combine(Directory.GetCurrentDirectory(), ".clawcker");
        
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
    /// Creates a new OpenClaw instance with interactive prompts for credentials
    /// </summary>
    public async Task CreateInstanceAsync(string name, string? provider = null, string? apiKey = null)
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

        // Prompt for provider if not specified
        if (string.IsNullOrWhiteSpace(provider))
        {
            LogInfo("Select AI provider:");
            LogInfo("  1. Anthropic (Claude)");
            LogInfo("  2. OpenAI");
            LogInfo("  3. OpenRouter");
            LogInfo("  4. Google (Gemini)");
            LogInfo("");
            Console.Write("Enter choice [1-4] (default: 1): ");
            var choice = Console.ReadLine()?.Trim();
            
            provider = choice switch
            {
                "2" => "openai",
                "3" => "openrouter",
                "4" => "gemini",
                _ => "anthropic"
            };
        }

        // Prompt for API key if not specified
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var providerName = provider switch
            {
                "openai" => "OpenAI",
                "openrouter" => "OpenRouter",
                "gemini" => "Google Gemini",
                _ => "Anthropic"
            };
            
            LogInfo("");
            LogInfo($"Enter your {providerName} API key:");
            LogInfo($"(You can get one from: {GetProviderUrl(provider)})");
            Console.Write("API Key: ");
            apiKey = ReadPassword();
            
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("API key is required");
            }
        }

        LogInfo("");
        LogInfo($"Creating new Clawcker instance: {name}");
        LogInfo($"Provider: {GetProviderDisplayName(provider)}");

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

        // Create OpenClaw configuration
        CreateOpenClawConfig(configDir, gatewayToken);

        // Find next available port
        var port = GetNextAvailablePort();

        // Create instance metadata
        var instance = new ClawckerInstance
        {
            Name = name,
            Port = port,
            ConfigPath = configDir,
            WorkspacePath = workspaceDir,
            GatewayToken = gatewayToken,
            IsCreated = true,
            CreatedAt = DateTime.UtcNow,
            Provider = provider,
            ApiKey = apiKey
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

        // Start the container temporarily to run onboarding
        LogInfo("Starting container for configuration...");
        await StartContainerForOnboarding(instance, provider!, apiKey!);

        LogInfo($"✓ Instance '{name}' created and configured successfully");
        LogInfo($"  Configuration: {configDir}");
        LogInfo($"  Workspace: {workspaceDir}");
        LogInfo($"  Provider: {GetProviderDisplayName(provider!)}");
        LogInfo("");
        
        // Auto-start the instance after creation
        LogInfo("Starting the instance...");
        StartInstance(name);
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
            
            // Build API key env var based on provider
            var apiKeyEnv = GetApiKeyEnvVar(instance.Provider, instance.ApiKey);
            
            var dockerArgs = $"run -d " +
                $"--name {instance.ContainerName} " +
                $"--restart unless-stopped " +
                $"-e OPENCLAW_GATEWAY_TOKEN={instance.GatewayToken} " +
                $"{apiKeyEnv}" +
                $"-v \"{instance.ConfigPath}:/home/node/.openclaw\" " +
                $"-v \"{instance.WorkspacePath}:/home/node/.openclaw/workspace\" " +
                $"-p {instance.Port}:{CONTAINER_PORT} " +
                $"{OPENCLAW_IMAGE} " +
                $"gateway";

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
                $"Instance '{name}' is not running. Start it first with 'chimpiler clawcker start {name}'");
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
    /// Configures or reconfigures an OpenClaw instance with a new provider/model
    /// </summary>
    public async Task ConfigureInstanceAsync(string name, string? provider = null, string? apiKey = null)
    {
        var instance = LoadInstanceMetadata(name);
        if (instance == null)
        {
            throw new InvalidOperationException($"Instance '{name}' not found. Create it first with 'chimpiler clawcker new {name}'");
        }

        // Prompt for provider if not specified
        if (string.IsNullOrWhiteSpace(provider))
        {
            LogInfo("Select AI provider:");
            LogInfo("  1. Anthropic (Claude)");
            LogInfo("  2. OpenAI");
            LogInfo("  3. OpenRouter");
            LogInfo("  4. Google (Gemini)");
            LogInfo("");
            Console.Write("Enter choice [1-4] (default: 1): ");
            var choice = Console.ReadLine()?.Trim();
            
            provider = choice switch
            {
                "2" => "openai",
                "3" => "openrouter",
                "4" => "gemini",
                _ => "anthropic"
            };
        }

        // Prompt for API key if not specified
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var providerName = provider switch
            {
                "openai" => "OpenAI",
                "openrouter" => "OpenRouter",
                "gemini" => "Google Gemini",
                _ => "Anthropic"
            };
            
            LogInfo("");
            LogInfo($"Enter your {providerName} API key:");
            LogInfo($"(You can get one from: {GetProviderUrl(provider)})");
            Console.Write("API Key: ");
            apiKey = ReadPassword();
            
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("API key is required");
            }
        }

        LogInfo("");
        LogInfo($"Configuring instance '{name}' with provider: {GetProviderDisplayName(provider)}");

        // Check if container is running and stop it temporarily
        var wasRunning = IsContainerRunning(instance.ContainerName);
        if (wasRunning)
        {
            LogInfo("Stopping running instance for reconfiguration...");
            RunCommand("docker", $"stop {instance.ContainerName}", captureOutput: true);
        }

        // Remove existing container if it exists
        if (CheckContainerExists(instance.ContainerName))
        {
            RunCommand("docker", $"rm {instance.ContainerName}", captureOutput: true);
        }

        // Remove any existing temporary config container from previous failed runs
        var tempContainerName = $"{instance.ContainerName}-config";
        if (CheckContainerExists(tempContainerName))
        {
            RunCommand("docker", $"rm -f {tempContainerName}", captureOutput: true);
        }

        // Start a temporary container and run the models/auth commands
        LogInfo("Starting temporary container for configuration...");
        var dockerArgs = $"run -d " +
            $"--name {tempContainerName} " +
            $"-e OPENCLAW_GATEWAY_TOKEN={instance.GatewayToken} " +
            $"-v \"{instance.ConfigPath}:/home/node/.openclaw\" " +
            $"-v \"{instance.WorkspacePath}:/home/node/.openclaw/workspace\" " +
            $"{OPENCLAW_IMAGE} " +
            $"tail -f /dev/null";

        var runResult = RunCommand("docker", dockerArgs, captureOutput: true);
        if (runResult.ExitCode != 0)
        {
            throw new InvalidOperationException("Failed to start temporary container for configuration");
        }

        try
        {
            await Task.Delay(2000);

            // Determine provider name and model based on provider
            var (providerName, model) = provider switch
            {
                "openai" => ("openai", "openai/gpt-5.2"),
                "openrouter" => ("openrouter", "openrouter/anthropic/claude-sonnet-4"),
                "gemini" => ("google-gemini", "google-gemini/gemini-2.5-pro"),
                _ => ("anthropic", "anthropic/claude-sonnet-4")
            };

            // Add auth credentials using paste-token with stdin
            LogInfo("Configuring authentication...");
            var authArgs = $"exec -i {tempContainerName} " +
                $"node /app/dist/entry.js models auth paste-token --provider {providerName}";
            var authResult = RunCommandWithInput("docker", authArgs, apiKey);
            if (authResult.ExitCode != 0)
            {
                LogInfo("⚠ Auth configuration completed with warnings");
            }

            // Set the default model
            LogInfo($"Setting default model to {model}...");
            var modelArgs = $"exec {tempContainerName} " +
                $"node /app/dist/entry.js models set {model}";
            var modelResult = RunCommand("docker", modelArgs, captureOutput: true);
            if (modelResult.ExitCode != 0)
            {
                LogInfo("⚠ Model configuration completed with warnings");
            }

            LogInfo("✓ Configuration updated successfully");
        }
        finally
        {
            // Stop and remove the temporary container
            RunCommand("docker", $"rm -f {tempContainerName}", captureOutput: true);
        }

        // Update instance metadata with new provider/apiKey
        instance.Provider = provider;
        instance.ApiKey = apiKey;
        SaveInstanceMetadata(instance);

        // Restart the instance if it was running before
        if (wasRunning)
        {
            LogInfo("");
            LogInfo("Restarting instance...");
            StartInstance(name);
        }
        else
        {
            LogInfo("");
            LogInfo($"To start the instance, run: chimpiler clawcker start {name}");
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
        LogInfo($"To start it again, run: chimpiler clawcker start {name}");
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

    /// <summary>
    /// Checks if the OpenClaw gateway is healthy and responding
    /// </summary>
    public async Task<bool> IsInstanceHealthy(string name, int timeoutSeconds = 5)
    {
        var instance = LoadInstanceMetadata(name);
        if (instance == null)
        {
            return false;
        }

        // First check if container is running
        if (!IsContainerRunning(instance.ContainerName))
        {
            return false;
        }

        // Try to connect to the gateway endpoint
        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };

            // Try the root endpoint (should redirect or return a page)
            var url = $"http://localhost:{instance.Port}/";
            var response = await httpClient.GetAsync(url);

            // If we get any response (even redirect), the gateway is responding
            return response.IsSuccessStatusCode || 
                   response.StatusCode == System.Net.HttpStatusCode.Redirect ||
                   response.StatusCode == System.Net.HttpStatusCode.MovedPermanently ||
                   response.StatusCode == System.Net.HttpStatusCode.Found ||
                   response.StatusCode == System.Net.HttpStatusCode.Unauthorized; // Auth required means it's up
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for an instance to become healthy
    /// </summary>
    public async Task<bool> WaitForInstanceHealthy(string name, int maxWaitSeconds = 30)
    {
        var startTime = DateTime.UtcNow;
        var checkIntervalMs = 500; // Check every 500ms

        while ((DateTime.UtcNow - startTime).TotalSeconds < maxWaitSeconds)
        {
            if (await IsInstanceHealthy(name, timeoutSeconds: 2))
            {
                return true;
            }

            await Task.Delay(checkIntervalMs);
        }

        return false;
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

    private (int ExitCode, string Output) RunCommandWithInput(string command, string arguments, string input)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException($"Failed to start process: {command}");
        }

        // Begin reading stdout and stderr asynchronously to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Write input and close stdin
        process.StandardInput.WriteLine(input);
        process.StandardInput.Close();

        process.WaitForExit();

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        var combinedOutput = string.IsNullOrEmpty(stderr)
            ? stdout
            : (string.IsNullOrEmpty(stdout) ? stderr : stdout + Environment.NewLine + stderr);

        return (process.ExitCode, combinedOutput);
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

    private int GetNextAvailablePort()
    {
        // Get all existing instances and their ports
        var existingPorts = new HashSet<int>();
        
        if (Directory.Exists(_instancesDirectory))
        {
            foreach (var dir in Directory.GetDirectories(_instancesDirectory))
            {
                var name = Path.GetFileName(dir);
                var instance = LoadInstanceMetadata(name);
                if (instance != null)
                {
                    existingPorts.Add(instance.Port);
                }
            }
        }

        // Find the next available port starting from BASE_PORT
        var port = BASE_PORT;
        while (existingPorts.Contains(port))
        {
            port++;
        }

        return port;
    }

    private void CreateOpenClawConfig(string configDir, string gatewayToken)
    {
        var config = new
        {
            gateway = new
            {
                mode = "local",
                bind = "lan",
                auth = new
                {
                    mode = "token",
                    token = gatewayToken
                },
                controlUi = new
                {
                    allowInsecureAuth = true
                }
            }
        };

        var configPath = Path.Combine(configDir, "openclaw.json");
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        File.WriteAllText(configPath, json);
        
        LogInfo($"Created OpenClaw configuration at: {configPath}");
    }

    private async Task StartContainerForOnboarding(ClawckerInstance instance, string provider, string apiKey)
    {
        // Start container temporarily
        var dockerArgs = $"run -d " +
            $"--name {instance.ContainerName}-setup " +
            $"-e OPENCLAW_GATEWAY_TOKEN={instance.GatewayToken} " +
            $"-v \"{instance.ConfigPath}:/home/node/.openclaw\" " +
            $"-v \"{instance.WorkspacePath}:/home/node/.openclaw/workspace\" " +
            $"{OPENCLAW_IMAGE} " +
            $"tail -f /dev/null";  // Keep container running

        var runResult = RunCommand("docker", dockerArgs, captureOutput: true);
        if (runResult.ExitCode != 0)
        {
            throw new InvalidOperationException("Failed to start temporary container for setup");
        }

        try
        {
            // Wait a moment for container to be ready
            await Task.Delay(2000);

            // Run onboard with the API key and set default model for the provider
            var (apiKeyArg, modelArg) = provider switch
            {
                "openai" => ($"--openai-api-key {apiKey}", "--model openai/gpt-5.2"),
                "openrouter" => ($"--openrouter-api-key {apiKey}", "--model openrouter/anthropic/claude-sonnet-4"),
                "gemini" => ($"--gemini-api-key {apiKey}", "--model google-gemini/gemini-2.5-pro"),
                _ => ($"--anthropic-api-key {apiKey}", "--model anthropic/claude-sonnet-4")
            };

            LogInfo("Configuring OpenClaw with your credentials...");
            var onboardArgs = $"exec {instance.ContainerName}-setup " +
                $"node /app/dist/entry.js onboard " +
                $"--non-interactive " +
                $"--accept-risk " +
                $"--flow quickstart " +
                $"--mode local " +
                $"{apiKeyArg} " +
                $"{modelArg} " +
                $"--gateway-port {CONTAINER_PORT} " +
                $"--gateway-bind lan " +
                $"--gateway-auth token " +
                $"--gateway-token {instance.GatewayToken} " +
                $"--skip-daemon " +
                $"--skip-channels " +
                $"--skip-ui";

            var onboardResult = RunCommand("docker", onboardArgs);
            if (onboardResult.ExitCode != 0)
            {
                LogInfo("⚠ Onboarding completed with warnings (this is usually fine)");
            }
            else
            {
                LogInfo("✓ OpenClaw configured successfully");
            }
        }
        finally
        {
            // Stop and remove the temporary container
            RunCommand("docker", $"rm -f {instance.ContainerName}-setup", captureOutput: true);
        }
    }

    private string ReadPassword()
    {
        var password = "";
        ConsoleKeyInfo key;
        
        do
        {
            key = Console.ReadKey(true);
            
            if (key.Key != ConsoleKey.Backspace && key.Key != ConsoleKey.Enter)
            {
                password += key.KeyChar;
                Console.Write("*");
            }
            else if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password = password[0..^1];
                Console.Write("\b \b");
            }
        }
        while (key.Key != ConsoleKey.Enter);
        
        Console.WriteLine();
        return password;
    }

    private string GetProviderUrl(string provider)
    {
        return provider switch
        {
            "openai" => "https://platform.openai.com/api-keys",
            "openrouter" => "https://openrouter.ai/keys",
            "gemini" => "https://aistudio.google.com/app/apikey",
            _ => "https://console.anthropic.com/settings/keys"
        };
    }

    private string GetProviderDisplayName(string provider)
    {
        return provider switch
        {
            "openai" => "OpenAI",
            "openrouter" => "OpenRouter",
            "gemini" => "Google Gemini",
            _ => "Anthropic (Claude)"
        };
    }

    private string GetApiKeyEnvVar(string? provider, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
            return "";
            
        var envVarName = provider switch
        {
            "openai" => "OPENAI_API_KEY",
            "openrouter" => "OPENROUTER_API_KEY",
            "gemini" => "GOOGLE_API_KEY",
            _ => "ANTHROPIC_API_KEY"
        };
        
        return $"-e {envVarName}={apiKey} ";
    }

    #endregion
}
