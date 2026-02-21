using System.Diagnostics;

namespace Chimpiler.Tests;

internal static class EfVersionFixtureBuilder
{
    public static FixtureBuildResult BuildFixtureAssembly(string fixtureName)
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(
            repoRoot,
            "tests",
            "fixtures",
            "ef-versions",
            fixtureName,
            $"{fixtureName}.csproj");

        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException($"Fixture project not found: {projectPath}");
        }

        var outputDir = Path.Combine(
            Path.GetTempPath(),
            "chimpiler-fixtures",
            fixtureName,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);

        RunDotNet($"build \"{projectPath}\" -c Release -nologo -o \"{outputDir}\"");

        var assemblyPath = Path.Combine(outputDir, $"{fixtureName}.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new InvalidOperationException($"Fixture assembly was not produced: {assemblyPath}");
        }

        return new FixtureBuildResult(assemblyPath, outputDir);
    }

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Chimpiler.slnx")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Unable to locate repository root from test context.");
    }

    private static void RunDotNet(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process == null)
        {
            throw new InvalidOperationException("Failed to start dotnet process for fixture build.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet command failed with exit code {process.ExitCode}: dotnet {arguments}{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }
}

internal sealed record FixtureBuildResult(string AssemblyPath, string OutputDirectory);
