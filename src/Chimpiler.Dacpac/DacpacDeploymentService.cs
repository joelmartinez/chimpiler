using System.Text.RegularExpressions;
using Npgsql;

namespace Chimpiler.Dacpac;

public interface IDacpacDeploymentProvider
{
    string Name { get; }
    Task<DeploymentResult> ApplyAsync(
        DacpacSchema source,
        DacpacApplyOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class DacpacDeploymentService
{
    private readonly DacpacModelReader _reader;
    private readonly IReadOnlyDictionary<string, IDacpacDeploymentProvider> _providers;

    public DacpacDeploymentService(
        DacpacModelReader? reader = null,
        IEnumerable<IDacpacDeploymentProvider>? providers = null)
    {
        _reader = reader ?? new DacpacModelReader();
        _providers = (providers ?? [new PostgreSqlDeploymentProvider()])
            .ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<DeploymentResult> ApplyAsync(
        DacpacApplyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_providers.TryGetValue(options.Provider, out var provider))
        {
            throw new DacpacCompatibilityException(
                $"Provider '{options.Provider}' is not supported. Available providers: {string.Join(", ", _providers.Keys.Order())}. MySQL is a future extension point.");
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("A connection string is required.", nameof(options));
        }

        // Reading and translating happen before a network connection is opened so unsupported
        // objects and expressions fail closed during preflight.
        var source = _reader.Read(options.DacpacPath);
        return await provider.ApplyAsync(source, options, cancellationToken);
    }
}

public sealed class PostgreSqlDeploymentProvider : IDacpacDeploymentProvider
{
    private readonly PostgreSqlModelTranslator _translator = new();
    private readonly PostgreSqlCatalogReader _catalog = new();
    private readonly SchemaDiffer _differ = new();
    private readonly PostgreSqlSqlGenerator _generator = new();

    public string Name => "postgresql";

    public async Task<DeploymentResult> ApplyAsync(
        DacpacSchema source,
        DacpacApplyOptions options,
        CancellationToken cancellationToken = default)
    {
        var desired = _translator.Translate(source);

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('chimpiler:dacpac:' || current_database(), 0))",
            connection,
            transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var current = await _catalog.ReadAsync(connection, transaction, cancellationToken);
        var plan = _differ.Diff(desired, current);
        if (plan.HasDestructiveOperations && !options.AllowDestructive)
        {
            throw new DestructiveChangesException(plan.Operations);
        }

        var script = _generator.GenerateScript(plan);
        if (!string.IsNullOrWhiteSpace(options.ScriptPath))
        {
            var fullScriptPath = Path.GetFullPath(options.ScriptPath);
            var directory = Path.GetDirectoryName(fullScriptPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullScriptPath, script, cancellationToken);
        }

        if (options.DryRun || !string.IsNullOrWhiteSpace(options.ScriptPath))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DeploymentResult(plan, script, false);
        }

        foreach (var operation in plan.Operations)
        {
            await using var command = new NpgsqlCommand(operation.Sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new DeploymentResult(plan, script, true);
    }
}

public static partial class SecretRedactor
{
    public static string Redact(string message, string? connectionString = null)
    {
        var redacted = message ?? string.Empty;
        if (!string.IsNullOrEmpty(connectionString))
        {
            redacted = redacted.Replace(connectionString, "[REDACTED CONNECTION STRING]", StringComparison.Ordinal);
        }

        return SecretValue().Replace(redacted, match => $"{match.Groups["key"].Value}=[REDACTED]");
    }

    [GeneratedRegex(
        @"(?i)(?<key>password|pwd|passfile|sslpassword|access\s*token|token)\s*=\s*(?:'[^']*'|""[^""]*""|[^;\s]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretValue();
}
