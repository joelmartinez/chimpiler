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

        // Create the clawcker command
        var clawckerCommand = CreateClawckerCommand();
        rootCommand.AddCommand(clawckerCommand);

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
        Console.WriteLine("  clawcker      Manage local OpenClaw instances using Docker");
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
        else if (subcommand.Equals("clawcker", StringComparison.OrdinalIgnoreCase))
        {
            var helpText = LoadEmbeddedMarkdown("clawcker.md");
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

    static Command CreateClawckerCommand()
    {
        var clawckerCommand = new Command("clawcker", "Manage local OpenClaw instances using Docker");

        // Create the 'new' subcommand
        var newCommand = new Command("new", "Create a new OpenClaw instance");
        var nameArg = new Argument<string>(
            name: "name",
            description: "Name of the instance to create");
        newCommand.AddArgument(nameArg);

        newCommand.SetHandler((string name) =>
        {
            try
            {
                var service = new ClawckerService(Console.WriteLine);
                service.CreateInstance(name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, nameArg);

        clawckerCommand.AddCommand(newCommand);

        // Create the 'up' subcommand
        var upCommand = new Command("up", "Start an OpenClaw instance");
        var upNameArg = new Argument<string>(
            name: "name",
            description: "Name of the instance to start");
        upCommand.AddArgument(upNameArg);

        upCommand.SetHandler((string name) =>
        {
            try
            {
                var service = new ClawckerService(Console.WriteLine);
                service.StartInstance(name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, upNameArg);

        clawckerCommand.AddCommand(upCommand);

        // Create the 'talk' subcommand
        var talkCommand = new Command("talk", "Open the web UI for an instance in your browser");
        var talkNameArg = new Argument<string>(
            name: "name",
            description: "Name of the instance to access");
        talkCommand.AddArgument(talkNameArg);

        talkCommand.SetHandler((string name) =>
        {
            try
            {
                var service = new ClawckerService(Console.WriteLine);
                service.OpenWebUI(name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, talkNameArg);

        clawckerCommand.AddCommand(talkCommand);

        // Create the 'list' subcommand
        var listCommand = new Command("list", "List all Clawcker instances");

        listCommand.SetHandler(() =>
        {
            try
            {
                var service = new ClawckerService(Console.WriteLine);
                var instances = service.ListInstances();
                
                if (instances.Count == 0)
                {
                    Console.WriteLine("No Clawcker instances found.");
                    Console.WriteLine("");
                    Console.WriteLine("Create a new instance with:");
                    Console.WriteLine("  chimpiler clawcker new <name>");
                }
                else
                {
                    Console.WriteLine("Clawcker Instances:");
                    Console.WriteLine("");
                    foreach (var instance in instances)
                    {
                        var status = service.GetInstanceStatus(instance.Name);
                        Console.WriteLine($"  {instance.Name}");
                        Console.WriteLine($"    Status: {status}");
                        Console.WriteLine($"    Port: {instance.Port}");
                        Console.WriteLine($"    Created: {instance.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
                        Console.WriteLine("");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        clawckerCommand.AddCommand(listCommand);

        // Create the 'down' subcommand
        var downCommand = new Command("down", "Stop a running OpenClaw instance");
        var downNameArg = new Argument<string>(
            name: "name",
            description: "Name of the instance to stop");
        downCommand.AddArgument(downNameArg);

        downCommand.SetHandler((string name) =>
        {
            try
            {
                var service = new ClawckerService(Console.WriteLine);
                service.StopInstance(name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, downNameArg);

        clawckerCommand.AddCommand(downCommand);

        return clawckerCommand;
    }
}
