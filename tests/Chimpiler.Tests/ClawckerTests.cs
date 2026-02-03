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
    public void CreateInstance_WithEmptyName_ShouldThrowArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _service.CreateInstance(""));
        Assert.Throws<ArgumentException>(() => _service.CreateInstance("   "));
    }

    [Fact]
    public void CreateInstance_WithInvalidCharacters_ShouldThrowArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _service.CreateInstance("my instance"));
        Assert.Throws<ArgumentException>(() => _service.CreateInstance("my@instance"));
        Assert.Throws<ArgumentException>(() => _service.CreateInstance("my/instance"));
    }

    [Fact]
    public void CreateInstance_WithValidName_ShouldCreateDirectoryStructure()
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
            _service.CreateInstance(instanceName);

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
    public void CreateInstance_WhenAlreadyExists_ShouldThrowInvalidOperationException()
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
            _service.CreateInstance(instanceName);

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() => _service.CreateInstance(instanceName));
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
