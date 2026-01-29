using System.CommandLine;
using Chimpiler.Core;

namespace Chimpiler;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Chimpiler - A multi-purpose database and schema tooling ecosystem");

        // Create the ef-migrate command
        var efMigrateCommand = CreateEfMigrateCommand();
        rootCommand.AddCommand(efMigrateCommand);

        return await rootCommand.InvokeAsync(args);
    }

    static Command CreateEfMigrateCommand()
    {
        var efMigrateCommand = new Command("ef-migrate", "Generate DACPACs from EF Core DbContext models");

        // Required options
        var assemblyOption = new Option<string>(
            name: "--assembly",
            description: "Path to a compiled .NET assembly containing one or more EF Core DbContext types")
        {
            IsRequired = true
        };
        assemblyOption.AddAlias("-a");

        // Optional options
        var contextOption = new Option<string?>(
            name: "--context",
            description: "Fully qualified type name of a specific DbContext. If omitted, all DbContexts will be processed")
        {
            IsRequired = false
        };
        contextOption.AddAlias("-c");

        var outputOption = new Option<string>(
            name: "--output",
            description: "Output directory for generated DACPACs",
            getDefaultValue: () => "./output")
        {
            IsRequired = false
        };
        outputOption.AddAlias("-o");

        var frameworkOption = new Option<string?>(
            name: "--framework",
            description: "Target framework hint for multi-targeted assemblies")
        {
            IsRequired = false
        };
        frameworkOption.AddAlias("-f");

        var verboseOption = new Option<bool>(
            name: "--verbose",
            description: "Enable detailed logging",
            getDefaultValue: () => false)
        {
            IsRequired = false
        };
        verboseOption.AddAlias("-v");

        efMigrateCommand.AddOption(assemblyOption);
        efMigrateCommand.AddOption(contextOption);
        efMigrateCommand.AddOption(outputOption);
        efMigrateCommand.AddOption(frameworkOption);
        efMigrateCommand.AddOption(verboseOption);

        efMigrateCommand.SetHandler(
            (string assembly, string? context, string output, string? framework, bool verbose) =>
            {
                try
                {
                    var options = new EfMigrateOptions
                    {
                        AssemblyPath = assembly,
                        ContextTypeName = context,
                        OutputDirectory = output,
                        Framework = framework,
                        Verbose = verbose
                    };

                    var service = new EfMigrateService(Console.WriteLine);
                    service.Execute(options);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    if (verbose)
                    {
                        Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
                    }
                    Environment.ExitCode = 1;
                }
            },
            assemblyOption,
            contextOption,
            outputOption,
            frameworkOption,
            verboseOption);

        return efMigrateCommand;
    }
}
