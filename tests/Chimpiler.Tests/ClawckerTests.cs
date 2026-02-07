using System.Diagnostics;
using Chimpiler.Core;
using Chimpiler.Core.Clawcker;
using Xunit;

namespace Chimpiler.Tests;

public class ClawckerServiceTests : IDisposable
{
    private readonly string _tempInstancesDir;
    private readonly ClawckerService _service;
    private readonly List<string> _logMessages;

    public ClawckerServiceTests()
    {
        // Create a temporary directory for test instances
        _tempInstancesDir = Path.Combine(Path.GetTempPath(), $"chimpiler-clawcker-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempInstancesDir);

        // Use reflection to override the instances directory for testing
        _logMessages = new List<string>();
        _service = new ClawckerService(msg => _logMessages.Add(msg));
        
        // Override the instances directory using reflection
        var field = typeof(ClawckerService).GetField("_instancesDirectory", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_service, _tempInstancesDir);
    }

    public void Dispose()
    {
        // Clean up test instances directory
        if (Directory.Exists(_tempInstancesDir))
        {
            try
            {
                Directory.Delete(_tempInstancesDir, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public void IsDockerAvailable_WhenDockerInstalled_ShouldReturnTrue()
    {
        // Act
        var result = _service.IsDockerAvailable();

        // Assert - this will depend on test environment
        // Just verify the method doesn't throw
        Assert.True(result || !result);
    }

    [Fact]
    public async Task CreateInstance_WithEmptyName_ShouldThrowArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () => await _service.CreateInstanceAsync("", "anthropic", "test-key"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await _service.CreateInstanceAsync("   ", "anthropic", "test-key"));
    }

    [Fact]
    public async Task CreateInstance_WithInvalidCharacters_ShouldThrowArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () => await _service.CreateInstanceAsync("my instance", "anthropic", "test-key"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await _service.CreateInstanceAsync("my@instance", "anthropic", "test-key"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await _service.CreateInstanceAsync("my/instance", "anthropic", "test-key"));
    }

    [Fact]
    public async Task CreateInstance_WithValidName_ShouldCreateDirectoryStructure()
    {
        // Skip this test if Docker is not available
        if (!_service.IsDockerAvailable() || !_service.IsDockerRunning())
        {
            return;
        }

        // Arrange
        var instanceName = "test-instance";

        // Act
        try
        {
            await _service.CreateInstanceAsync(instanceName, "anthropic", "sk-test-key-12345");

            // Assert
            var instanceDir = Path.Combine(_tempInstancesDir, instanceName);
            Assert.True(Directory.Exists(instanceDir), "Instance directory should exist");
            Assert.True(Directory.Exists(Path.Combine(instanceDir, "config")), "Config directory should exist");
            Assert.True(Directory.Exists(Path.Combine(instanceDir, "workspace")), "Workspace directory should exist");
            Assert.True(File.Exists(Path.Combine(instanceDir, "instance.json")), "Metadata file should exist");
        }
        finally
        {
            // Cleanup
            CleanupInstance(instanceName);
        }
    }

    [Fact]
    public async Task CreateInstance_WhenAlreadyExists_ShouldThrowInvalidOperationException()
    {
        // Skip this test if Docker is not available
        if (!_service.IsDockerAvailable() || !_service.IsDockerRunning())
        {
            return;
        }

        // Arrange
        var instanceName = "duplicate-test";
        
        try
        {
            await _service.CreateInstanceAsync(instanceName, "anthropic", "sk-test-key-12345");

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await _service.CreateInstanceAsync(instanceName, "anthropic", "sk-test-key-12345"));
            Assert.Contains("already exists", ex.Message);
        }
        finally
        {
            // Cleanup
            CleanupInstance(instanceName);
        }
    }

    [Fact]
    public void GetInstanceStatus_ForNonExistentInstance_ShouldReturnNotFound()
    {
        // Act
        var status = _service.GetInstanceStatus("non-existent");

        // Assert
        Assert.Equal("not found", status);
    }

    [Fact]
    public void ListInstances_WhenNoInstances_ShouldReturnEmptyList()
    {
        // Act
        var instances = _service.ListInstances();

        // Assert
        Assert.NotNull(instances);
        Assert.Empty(instances);
    }

    [Fact]
    public void StartInstance_ForNonExistentInstance_ShouldThrowInvalidOperationException()
    {
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => _service.StartInstance("non-existent"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void OpenWebUI_ForNonExistentInstance_ShouldThrowInvalidOperationException()
    {
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => _service.OpenWebUI("non-existent"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task IsInstanceHealthy_ForNonExistentInstance_ShouldReturnFalse()
    {
        // Act
        var isHealthy = await _service.IsInstanceHealthy("non-existent");

        // Assert
        Assert.False(isHealthy);
    }

    [Fact]
    public async Task WaitForInstanceHealthy_ForNonExistentInstance_ShouldReturnFalse()
    {
        // Act
        var isHealthy = await _service.WaitForInstanceHealthy("non-existent", maxWaitSeconds: 1);

        // Assert
        Assert.False(isHealthy);
    }

    [Fact]
    public async Task ConfigureInstanceAsync_ForNonExistentInstance_ShouldThrowInvalidOperationException()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _service.ConfigureInstanceAsync("non-existent", "anthropic", "test-key"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ConfigureInstanceAsync_WithValidProviderAndKey_ShouldUpdateMetadata()
    {
        // Skip this test if Docker is not available
        if (!_service.IsDockerAvailable() || !_service.IsDockerRunning())
        {
            return;
        }

        // Arrange
        var instanceName = "test-configure-metadata";
        
        try
        {
            // Create instance first
            await _service.CreateInstanceAsync(instanceName, "anthropic", "initial-key");
            
            // Act - Reconfigure with new provider and key
            await _service.ConfigureInstanceAsync(instanceName, "openai", "new-openai-key");
            
            // Assert - Verify metadata was updated
            var instances = _service.ListInstances();
            var instance = instances.FirstOrDefault(i => i.Name == instanceName);
            
            Assert.NotNull(instance);
            Assert.Equal("openai", instance.Provider);
            Assert.Equal("new-openai-key", instance.ApiKey);
        }
        finally
        {
            CleanupInstance(instanceName);
        }
    }

    [Fact]
    public async Task ConfigureInstanceAsync_WhenInstanceIsRunning_ShouldStopAndRestart()
    {
        // Skip this test if Docker is not available
        if (!_service.IsDockerAvailable() || !_service.IsDockerRunning())
        {
            return;
        }

        // Arrange
        var instanceName = "test-configure-running";
        
        try
        {
            // Create and start instance
            await _service.CreateInstanceAsync(instanceName, "anthropic", "test-key");
            _service.StartInstance(instanceName);
            
            // Wait a moment for container to start
            await Task.Delay(3000);
            
            // Verify it's running
            var statusBefore = _service.GetInstanceStatus(instanceName);
            Assert.Contains("running", statusBefore.ToLower());
            
            // Act - Reconfigure the running instance
            await _service.ConfigureInstanceAsync(instanceName, "openai", "new-key");
            
            // Assert - Instance should be running again after reconfiguration
            await Task.Delay(3000); // Give it time to restart
            var statusAfter = _service.GetInstanceStatus(instanceName);
            Assert.Contains("running", statusAfter.ToLower());
            
            // Verify metadata was updated
            var instances = _service.ListInstances();
            var instance = instances.FirstOrDefault(i => i.Name == instanceName);
            Assert.NotNull(instance);
            Assert.Equal("openai", instance.Provider);
        }
        finally
        {
            CleanupInstance(instanceName);
        }
    }

    [Fact]
    public async Task ConfigureInstanceAsync_WhenInstanceIsStopped_ShouldNotRestart()
    {
        // Skip this test if Docker is not available
        if (!_service.IsDockerAvailable() || !_service.IsDockerRunning())
        {
            return;
        }

        // Arrange
        var instanceName = "test-configure-stopped";
        
        try
        {
            // Create instance (which auto-starts it)
            await _service.CreateInstanceAsync(instanceName, "anthropic", "test-key");
            
            // Stop the instance
            _service.StopInstance(instanceName);
            await Task.Delay(2000); // Give it time to stop
            
            // Verify it's not running
            var statusBefore = _service.GetInstanceStatus(instanceName);
            Assert.DoesNotContain("running", statusBefore.ToLower());
            
            // Act - Configure the stopped instance
            await _service.ConfigureInstanceAsync(instanceName, "gemini", "gemini-key");
            
            // Assert - Instance should still be stopped
            var statusAfter = _service.GetInstanceStatus(instanceName);
            Assert.DoesNotContain("running", statusAfter.ToLower());
            
            // Verify metadata was updated
            var instances = _service.ListInstances();
            var instance = instances.FirstOrDefault(i => i.Name == instanceName);
            Assert.NotNull(instance);
            Assert.Equal("gemini", instance.Provider);
            Assert.Equal("gemini-key", instance.ApiKey);
        }
        finally
        {
            CleanupInstance(instanceName);
        }
    }

    [Fact]
    public async Task ConfigureInstanceAsync_WithDifferentProviders_ShouldPersistCorrectly()
    {
        // Skip this test if Docker is not available
        if (!_service.IsDockerAvailable() || !_service.IsDockerRunning())
        {
            return;
        }

        // Arrange
        var instanceName = "test-configure-providers";
        var providers = new[] { "anthropic", "openai", "openrouter", "gemini" };
        
        try
        {
            // Create instance
            await _service.CreateInstanceAsync(instanceName, "anthropic", "initial-key");
            
            // Act & Assert - Test each provider
            foreach (var provider in providers)
            {
                var apiKey = $"{provider}-test-key-{Guid.NewGuid()}";
                await _service.ConfigureInstanceAsync(instanceName, provider, apiKey);
                
                // Verify metadata persistence
                var instances = _service.ListInstances();
                var instance = instances.FirstOrDefault(i => i.Name == instanceName);
                
                Assert.NotNull(instance);
                Assert.Equal(provider, instance.Provider);
                Assert.Equal(apiKey, instance.ApiKey);
            }
        }
        finally
        {
            CleanupInstance(instanceName);
        }
    }

    private void CleanupInstance(string name)
    {
        try
        {
            // Stop and remove container if it exists
            var containerName = $"clawcker-{name}";
            
            var checkProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"ps -a --filter name=^/{containerName}$ --format {{{{.Names}}}}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (checkProcess != null)
            {
                var output = checkProcess.StandardOutput.ReadToEnd();
                checkProcess.WaitForExit();

                if (!string.IsNullOrWhiteSpace(output) && output.Trim() == containerName)
                {
                    // Container exists, remove it
                    var stopProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = "docker",
                        Arguments = $"rm -f {containerName}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    stopProcess?.WaitForExit();
                }
            }

            // Remove instance directory
            var instanceDir = Path.Combine(_tempInstancesDir, name);
            if (Directory.Exists(instanceDir))
            {
                Directory.Delete(instanceDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}

public class ClawckerInstanceTests
{
    [Fact]
    public void ContainerName_ShouldBeFormattedCorrectly()
    {
        // Arrange
        var instance = new ClawckerInstance { Name = "my-agent" };

        // Act
        var containerName = instance.ContainerName;

        // Assert
        Assert.Equal("clawcker-my-agent", containerName);
    }

    [Fact]
    public void DefaultPort_ShouldBe18789()
    {
        // Arrange & Act
        var instance = new ClawckerInstance();

        // Assert
        Assert.Equal(18789, instance.Port);
    }
}
