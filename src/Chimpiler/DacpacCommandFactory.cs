using System.CommandLine;
using Chimpiler.Dacpac;

namespace Chimpiler;

internal static class DacpacCommandFactory
{
    internal const string ConnectionStringEnvironmentVariable = "CHIMPILER_DACPAC_CONNECTION_STRING";

    public static Command Create()
    {
        var command = new Command("dacpac", "Read and deploy a supported DACPAC schema subset");
        command.AddCommand(CreateApply());
        return command;
    }

    private static Command CreateApply()
    {
        var command = new Command("apply", "Plan or apply a DACPAC schema to a target database");
        var dacpacArgument = new Argument<string>("dacpac", "Path to the SQL Server Database Project DACPAC");
        var providerOption = new Option<string>(
            "--provider",
            () => "postgresql",
            "Target provider. PostgreSQL is supported; MySQL is reserved for future support.");
        var connectionStringOption = new Option<string?>(
            "--connection-string",
            "Target connection string. Defaults to CHIMPILER_DACPAC_CONNECTION_STRING.");
        var dryRunOption = new Option<bool>(
            "--dry-run",
            "Validate, introspect, and print the deterministic SQL plan without changing the database.");
        var scriptOption = new Option<string?>(
            "--script",
            "Write the deterministic SQL plan to this file without changing the database.");
        var allowDestructiveOption = new Option<bool>(
            "--allow-destructive",
            "Allow operations that drop tables, columns, constraints, indexes, defaults, identities, or change column types.");

        command.AddArgument(dacpacArgument);
        command.AddOption(providerOption);
        command.AddOption(connectionStringOption);
        command.AddOption(dryRunOption);
        command.AddOption(scriptOption);
        command.AddOption(allowDestructiveOption);

        command.SetHandler(async context =>
        {
            var dacpac = context.ParseResult.GetValueForArgument(dacpacArgument);
            var provider = NormalizeProvider(context.ParseResult.GetValueForOption(providerOption)!);
            var connectionString = context.ParseResult.GetValueForOption(connectionStringOption)
                ?? Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var scriptPath = context.ParseResult.GetValueForOption(scriptOption);
            var allowDestructive = context.ParseResult.GetValueForOption(allowDestructiveOption);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.Error.WriteLine(
                    $"Error: A connection string is required via --connection-string or {ConnectionStringEnvironmentVariable}.");
                context.ExitCode = 1;
                return;
            }

            try
            {
                var result = await new DacpacDeploymentService().ApplyAsync(new DacpacApplyOptions
                {
                    DacpacPath = dacpac,
                    Provider = provider,
                    ConnectionString = connectionString,
                    DryRun = dryRun,
                    ScriptPath = scriptPath,
                    AllowDestructive = allowDestructive
                });

                if (dryRun)
                {
                    Console.Write(result.Script);
                }

                if (!string.IsNullOrWhiteSpace(scriptPath))
                {
                    Console.WriteLine($"Wrote deployment script to {Path.GetFullPath(scriptPath)}.");
                }
                else if (result.Applied)
                {
                    Console.WriteLine(result.Plan.Operations.Count == 0
                        ? "Database schema is already up to date."
                        : $"Applied {result.Plan.Operations.Count} schema operation(s).");
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Error: {SecretRedactor.Redact(exception.Message, connectionString)}");
                context.ExitCode = 1;
            }
        });

        return command;
    }

    private static string NormalizeProvider(string provider) =>
        provider.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("pgsql", StringComparison.OrdinalIgnoreCase)
            ? "postgresql"
            : provider;
}
