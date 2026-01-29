using System.CommandLine;
using System.Reflection;
using Chimpiler.Core;

namespace Chimpiler;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Chimpiler - A multi-purpose CLI tooling ecosystem");

        // Create the ef-migrate command
        var efMigrateCommand = CreateEfMigrateCommand();
        rootCommand.AddCommand(efMigrateCommand);

        // Create the help command
        var helpCommand = CreateHelpCommand(rootCommand);
        rootCommand.AddCommand(helpCommand);

        return await rootCommand.InvokeAsync(args);
    }

    static Command CreateHelpCommand(RootCommand rootCommand)
    {
        var helpCommand = new Command("help", "Display help information for Chimpiler or a specific subcommand");
        
        var subcommandArg = new Argument<string?>(
            name: "subcommand",
            description: "The subcommand to get help for",
            getDefaultValue: () => null);
        
        helpCommand.AddArgument(subcommandArg);

        helpCommand.SetHandler((string? subcommand) =>
        {
            if (string.IsNullOrEmpty(subcommand))
            {
                // Show general help
                ShowGeneralHelp();
            }
            else
            {
                // Show subcommand-specific help
                ShowSubcommandHelp(subcommand);
            }
        }, subcommandArg);

        return helpCommand;
    }

    static void ShowGeneralHelp()
    {
        Console.WriteLine("Chimpiler - A multi-purpose CLI tooling ecosystem");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  chimpiler [command] [options]");
        Console.WriteLine();
        Console.WriteLine("Available Commands:");
        Console.WriteLine("  ef-migrate    Generate DACPACs from EF Core DbContext models");
        Console.WriteLine("  help          Display help information for Chimpiler or a specific subcommand");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -?, -h, --help    Show help and usage information");
        Console.WriteLine("  --version         Show version information");
        Console.WriteLine();
        Console.WriteLine("Use 'chimpiler help [command]' for more information about a command.");
    }

    static void ShowSubcommandHelp(string subcommand)
    {
        if (subcommand.Equals("ef-migrate", StringComparison.OrdinalIgnoreCase))
        {
            var helpText = LoadEmbeddedMarkdown("ef-migrate.md");
            Console.WriteLine(helpText);
        }
        else
        {
            Console.WriteLine($"Unknown subcommand: {subcommand}");
            Console.WriteLine();
            ShowGeneralHelp();
        }
    }

    static string LoadEmbeddedMarkdown(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourcePath = $"Chimpiler.Resources.{resourceName}";
        
        using var stream = assembly.GetManifestResourceStream(resourcePath);
        if (stream == null)
        {
            return $"Help documentation not found for {resourceName}";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
        efMigrateCommand.AddOption(verboseOption);

        efMigrateCommand.SetHandler(
            (string assembly, string? context, string output, bool verbose) =>
            {
                try
                {
                    var options = new EfMigrateOptions
                    {
                        AssemblyPath = assembly,
                        ContextTypeName = context,
                        OutputDirectory = output,
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
            verboseOption);

        return efMigrateCommand;
    }
}
