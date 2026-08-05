using System.Diagnostics;

namespace Chimpiler.Kb.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class KbCliIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "chimpiler-kb-integration", Guid.NewGuid().ToString("N"));

    public KbCliIntegrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task DownloadedBgeModel_IndexesAndFindsKnownCorporaThroughTheCli()
    {
        var database = Path.Combine(_directory, "kb.db");
        var corpus = Path.Combine(AppContext.BaseDirectory, "Fixtures", "corpus");
        var batchCorpus = Path.Combine(_directory, "batch.md");
        await File.WriteAllTextAsync(
            batchCorpus,
            string.Join("\n\n", Enumerable.Range(1, 33).Select(index => $"# Batch {index}\n\nBatch embedding fixture {index}.")));

        await RunCliAsync("kb", "models", "install", "default");
        await RunCliAsync("kb", "--db", database, "--model", "default", "init");
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", corpus, "--pattern", "*.md");
        var batchAdd = await RunCliAsync("kb", "--db", database, "--model", "default", "add", batchCorpus);

        var astronomy = await RunCliAsync("kb", "--db", database, "--model", "default", "search", "Which celestial body has an icy nucleus and tail?", "--top", "1");
        var databaseResult = await RunCliAsync("kb", "--db", database, "--model", "default", "search", "What database uses write-ahead logging for crash recovery?", "--top", "1");

        Assert.Contains("astronomy.md", astronomy.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("storage.md", databaseResult.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("33 chunk(s)", batchAdd.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphSearch_TraversesTertiarySemanticConnectionsAcrossDocuments()
    {
        var database = Path.Combine(_directory, "semantic-chain.db");
        var corpus = Path.Combine(AppContext.BaseDirectory, "Fixtures", "semantic-chain");

        await RunCliAsync("kb", "models", "install", "default");
        await RunCliAsync("kb", "--db", database, "--model", "default", "init");
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "atlas.md"));
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "heliostat.md"));
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "configuration.md"));

        var vectorSearch = await RunCliAsync(
            "kb", "--db", database, "--model", "default", "search",
            "Which setting controls the Atlas array sunward hold mode?", "--top", "1");
        var oneHopGraphSearch = await RunCliAsync(
            "kb", "--db", database, "--model", "default", "graph-search",
            "Which setting controls the Atlas array sunward hold mode?", "--top", "1", "--depth", "1");
        var twoHopGraphSearch = await RunCliAsync(
            "kb", "--db", database, "--model", "default", "graph-search",
            "Which setting controls the Atlas array sunward hold mode?", "--top", "1", "--depth", "2");

        Assert.Contains("atlas.md", vectorSearch.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("configuration.md", vectorSearch.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("configuration.md", oneHopGraphSearch.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("configuration.md", twoHopGraphSearch.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("sunTargetBias", twoHopGraphSearch.StandardOutput, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunCliAsync(params string[] arguments)
    {
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "Chimpiler.dll");
        Assert.True(File.Exists(cliAssembly), $"Compiled CLI assembly was not found at '{cliAssembly}'.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(cliAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the compiled Chimpiler CLI.");
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"chimpiler {string.Join(' ', arguments)} failed:{Environment.NewLine}{standardError}");
        return new ProcessResult(standardOutput, standardError);
    }

    private sealed record ProcessResult(string StandardOutput, string StandardError);
}
