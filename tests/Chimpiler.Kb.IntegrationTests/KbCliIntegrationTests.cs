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
    public async Task Prompt_ProvidesAgentReadyKbOperatingGuidance()
    {
        var prompt = await RunCliAsync("kb", "prompt");

        Assert.Contains("graph-search", prompt.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("does not infer entities", prompt.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("subagent", prompt.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Verify the cited source text and trail", prompt.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install --global Chimpiler", prompt.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphSearch_TraversesAgentAuthoredCrossDocumentConnections()
    {
        var database = Path.Combine(_directory, "semantic-chain.db");
        var corpus = Path.Combine(AppContext.BaseDirectory, "Fixtures", "semantic-chain");

        await RunCliAsync("kb", "models", "install", "default");
        await RunCliAsync("kb", "--db", database, "--model", "default", "init");
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "atlas.md"));
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "heliostat.md"));
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "configuration.md"));
        var atlas = Path.Combine(corpus, "atlas.md");
        var heliostat = Path.Combine(corpus, "heliostat.md");
        var configuration = Path.Combine(corpus, "configuration.md");
        await RunCliAsync("kb", "--db", database, "--model", "default", "entity", "system:atlas", "--kind", "system", "--surface", "Atlas array", "--source", atlas, "--evidence", "Atlas array");
        await RunCliAsync("kb", "--db", database, "--model", "default", "entity", "system:heliostat", "--kind", "system", "--surface", "heliostat", "--source", heliostat, "--evidence", "heliostat");
        await RunCliAsync("kb", "--db", database, "--model", "default", "entity", "setting:sun-target-bias", "--kind", "setting", "--surface", "sunTargetBias", "--source", configuration, "--evidence", "sunTargetBias");
        await RunCliAsync("kb", "--db", database, "--model", "default", "relate", "system:atlas", "uses", "system:heliostat", "--source", atlas, "--evidence", "Atlas array");
        await RunCliAsync("kb", "--db", database, "--model", "default", "relate", "system:heliostat", "configured-by", "setting:sun-target-bias", "--source", heliostat, "--evidence", "heliostat");

        var vectorSearch = await RunCliAsync(
            "kb", "--db", database, "--model", "default", "search",
            "Which setting controls the Atlas array sunward hold mode?", "--top", "1");
        var graphSearch = await RunCliAsync(
            "kb", "--db", database, "--model", "default", "graph-search",
            "Which setting controls the Atlas array sunward hold mode?", "--top", "3", "--depth", "2");

        Assert.Contains("atlas.md", vectorSearch.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("configuration.md", vectorSearch.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("configuration.md", graphSearch.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("sunTargetBias", graphSearch.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphSearch_TraversesAgentVerifiedAliasesAcrossDocuments()
    {
        var database = Path.Combine(_directory, "entity-aliases.db");
        var corpus = Path.Combine(AppContext.BaseDirectory, "Fixtures", "entity-aliases");

        await RunCliAsync("kb", "models", "install", "default");
        await RunCliAsync("kb", "--db", database, "--model", "default", "init");
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "bob.md"));
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "robert.md"));
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "electronic-arts.md"));
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "ea.md"));
        var bob = Path.Combine(corpus, "bob.md");
        var robert = Path.Combine(corpus, "robert.md");
        await RunCliAsync("kb", "--db", database, "--model", "default", "entity", "person:bob tagart", "--kind", "person", "--surface", "Bob Tagart", "--source", bob, "--evidence", "Bob Tagart");
        await RunCliAsync("kb", "--db", database, "--model", "default", "entity", "person:robert tagart", "--kind", "person", "--surface", "Robert Tagart", "--source", robert, "--evidence", "Robert Tagart");
        await RunCliAsync("kb", "--db", database, "--model", "default", "relate", "person:bob tagart", "alias-candidate", "person:robert tagart", "--source", bob, "--evidence", "Bob Tagart");

        var bobVectorSearch = await RunCliAsync(
            "kb", "--db", database, "--model", "default", "search",
            "What did Bob Tagart authorize?", "--top", "1");
        var bobGraphSearch = await RunCliAsync(
            "kb", "--db", database, "--model", "default", "graph-search",
            "What did Bob Tagart authorize?", "--top", "1", "--depth", "3");

        Assert.Contains("bob.md", bobVectorSearch.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("robert.md", bobVectorSearch.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("robert.md", bobGraphSearch.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("crimsonLedger", bobGraphSearch.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("trail: person:bob tagart \u2192 alias-candidate \u2192 person:robert tagart", bobGraphSearch.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphSearch_UsesAgentRegisteredEntityRelationships()
    {
        var database = Path.Combine(_directory, "relationships.db");
        var corpus = Path.Combine(AppContext.BaseDirectory, "Fixtures", "relationships");

        await RunCliAsync("kb", "models", "install", "default");
        await RunCliAsync("kb", "--db", database, "--model", "default", "init");
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "authorization.md"));
        await RunCliAsync("kb", "--db", database, "--model", "default", "add", Path.Combine(corpus, "record.md"));
        var authorization = Path.Combine(corpus, "authorization.md");
        var record = Path.Combine(corpus, "record.md");
        await RunCliAsync("kb", "--db", database, "--model", "default", "entity", "person:bob tagart", "--kind", "person", "--surface", "Bob Tagart", "--source", authorization, "--evidence", "Bob Tagart");
        await RunCliAsync("kb", "--db", database, "--model", "default", "entity", "organization:electronic arts", "--kind", "organization", "--surface", "Electronic Arts", "--source", record, "--evidence", "Electronic Arts");
        var entities = await RunCliAsync("kb", "--db", database, "--model", "default", "entities");
        var assertion = await RunCliAsync(
            "kb", "--db", database, "--model", "default", "relate",
            "person:bob tagart", "authorized", "organization:electronic arts",
            "--source", authorization, "--evidence", "Bob Tagart", "--confidence", "0.9");

        var direct = await RunCliAsync(
            "kb", "--db", database, "--model", "default", "search",
            "What did Bob Tagart authorize?", "--top", "1");
        var graph = await RunCliAsync(
            "kb", "--db", database, "--model", "default", "graph-search",
            "What did Bob Tagart authorize?", "--top", "1", "--depth", "4");

        Assert.Contains("person:bob tagart", entities.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("organization:electronic arts", entities.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Added 'authorized' relationship", assertion.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("authorization.md", direct.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("record.md", direct.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("record.md", graph.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("NorthstarReceipt", graph.StandardOutput, StringComparison.Ordinal);
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
