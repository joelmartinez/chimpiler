using System.CommandLine;
using Chimpiler.Kb;
using Chimpiler.Kb.Abstractions;
using Chimpiler.Kb.Embeddings;
using Chimpiler.Kb.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Chimpiler;

/// <summary>Builds the `chimpiler kb` command tree.</summary>
internal static class KbCommandFactory
{
    public static Command Create()
    {
        var kbCommand = new Command("kb", "Agent-ready, local GraphRAG knowledge base backed by SQLite");

        var dbOption = new Option<string>(
            name: "--db",
            description: "Path to the knowledge base SQLite database",
            getDefaultValue: () => KnowledgeBaseOptions.DefaultDatabaseFile);
        dbOption.AddAlias("-d");

        var modelOption = new Option<string?>(
            name: "--model",
            description: "Embedding model id to use (e.g. 'default', 'bge-small-en-v1.5'). Omit to use the built-in offline hash provider.",
            getDefaultValue: () => null);

        kbCommand.AddGlobalOption(dbOption);
        kbCommand.AddGlobalOption(modelOption);

        kbCommand.AddCommand(CreateInit(dbOption, modelOption));
        kbCommand.AddCommand(CreateAdd(dbOption, modelOption));
        kbCommand.AddCommand(CreateRemove(dbOption, modelOption));
        kbCommand.AddCommand(CreateList(dbOption, modelOption));
        kbCommand.AddCommand(CreateEntities(dbOption, modelOption));
        kbCommand.AddCommand(CreateEntity(dbOption, modelOption));
        kbCommand.AddCommand(CreateRelate(dbOption, modelOption));
        kbCommand.AddCommand(CreateSearch(dbOption, modelOption, graph: false));
        kbCommand.AddCommand(CreateSearch(dbOption, modelOption, graph: true));
        kbCommand.AddCommand(CreateRebuild(dbOption, modelOption));
        kbCommand.AddCommand(CreateOptimize(dbOption, modelOption));
        kbCommand.AddCommand(CreateModels());
        kbCommand.AddCommand(CreateAgentPrompt());

        return kbCommand;
    }

    private static Command CreateInit(Option<string> dbOption, Option<string?> modelOption)
    {
        var command = new Command("init", "Create the knowledge base SQLite database");
        command.SetHandler(async (string db, string? model) =>
        {
            await RunAsync(db, model, async kb =>
            {
                await kb.InitializeAsync();
                Console.WriteLine($"Initialized knowledge base at {Path.GetFullPath(db)}");
            });
        }, dbOption, modelOption);

        return command;
    }

    private static Command CreateAdd(Option<string> dbOption, Option<string?> modelOption)
    {
        var command = new Command("add", "Add or update a file or directory in the knowledge base");
        var pathArg = new Argument<string>("path", "File or directory to index");
        var patternOption = new Option<string>(
            name: "--pattern",
            description: "Glob pattern used when indexing a directory",
            getDefaultValue: () => "*.*");
        var maxTokensOption = new Option<int>(
            name: "--max-tokens",
            description: "Maximum tokens per chunk",
            getDefaultValue: () => ChunkingOptions.Default.MaxTokens);
        var overlapOption = new Option<int>(
            name: "--overlap",
            description: "Token overlap between adjacent chunks",
            getDefaultValue: () => ChunkingOptions.Default.OverlapTokens);

        command.AddArgument(pathArg);
        command.AddOption(patternOption);
        command.AddOption(maxTokensOption);
        command.AddOption(overlapOption);

        command.SetHandler(async (context) =>
        {
            var db = context.ParseResult.GetValueForOption(dbOption)!;
            var model = context.ParseResult.GetValueForOption(modelOption);
            var path = context.ParseResult.GetValueForArgument(pathArg);
            var pattern = context.ParseResult.GetValueForOption(patternOption)!;
            var chunking = new ChunkingOptions
            {
                MaxTokens = context.ParseResult.GetValueForOption(maxTokensOption),
                OverlapTokens = context.ParseResult.GetValueForOption(overlapOption)
            };

            await RunAsync(db, model, async kb =>
            {
                await kb.InitializeAsync();

                var files = Directory.Exists(path)
                    ? Directory.GetFiles(path, pattern, SearchOption.AllDirectories)
                    : new[] { path };

                var totalChunks = 0;
                foreach (var file in files)
                {
                    totalChunks += await kb.AddDocumentAsync(file, chunking);
                }

                Console.WriteLine($"Indexed {files.Length} document(s) into {totalChunks} chunk(s).");
            });
        });

        return command;
    }

    private static Command CreateRemove(Option<string> dbOption, Option<string?> modelOption)
    {
        var command = new Command("remove", "Remove a document from the knowledge base");
        var pathArg = new Argument<string>("path", "Path of the document to remove");
        command.AddArgument(pathArg);

        command.SetHandler(async (string db, string? model, string path) =>
        {
            await RunAsync(db, model, async kb =>
            {
                await kb.InitializeAsync();
                await kb.RemoveDocumentAsync(path);
                Console.WriteLine($"Removed {Path.GetFullPath(path)}");
            });
        }, dbOption, modelOption, pathArg);

        return command;
    }

    private static Command CreateList(Option<string> dbOption, Option<string?> modelOption)
    {
        var command = new Command("list", "List documents in the knowledge base");
        command.SetHandler(async (string db, string? model) =>
        {
            await RunAsync(db, model, async kb =>
            {
                await kb.InitializeAsync();
                var documents = await kb.ListDocumentsAsync();
                if (documents.Count == 0)
                {
                    Console.WriteLine("No documents indexed.");
                    return;
                }

                foreach (var document in documents)
                {
                    Console.WriteLine($"{document.SourcePath}  [{document.ContentType}]  updated {document.UpdatedUtc:u}");
                }
            });
        }, dbOption, modelOption);

        return command;
    }

    private static Command CreateSearch(Option<string> dbOption, Option<string?> modelOption, bool graph)
    {
        var command = graph
            ? new Command("graph-search", "Semantic search followed by knowledge-graph expansion")
            : new Command("search", "Semantic vector search over indexed chunks");

        var queryArg = new Argument<string>("query", "Query text");
        var topOption = new Option<int>(name: "--top", description: "Number of results", getDefaultValue: () => 5);
        var depthOption = new Option<int>(name: "--depth", description: "Maximum agent-authored relationship hops to traverse", getDefaultValue: () => 2);

        command.AddArgument(queryArg);
        command.AddOption(topOption);
        if (graph)
        {
            command.AddOption(depthOption);
        }

        command.SetHandler(async (context) =>
        {
            var db = context.ParseResult.GetValueForOption(dbOption)!;
            var model = context.ParseResult.GetValueForOption(modelOption);
            var query = context.ParseResult.GetValueForArgument(queryArg);
            var top = context.ParseResult.GetValueForOption(topOption);
            var depth = graph ? context.ParseResult.GetValueForOption(depthOption) : 0;

            await RunAsync(db, model, async kb =>
            {
                await kb.InitializeAsync();
                var results = graph
                    ? await kb.GraphSearchAsync(query, top, depth)
                    : await kb.SearchAsync(query, top);

                if (results.Count == 0)
                {
                    Console.WriteLine("No results.");
                    return;
                }

                foreach (var result in results)
                {
                    var tag = result.FromGraphExpansion ? " (graph)" : string.Empty;
                    Console.WriteLine($"[{result.Score:F4}]{tag} {result.SourcePath}#{result.ChunkId}");
                    if (!string.IsNullOrWhiteSpace(result.Heading))
                    {
                        Console.WriteLine($"  ## {result.Heading}");
                    }

                    Console.WriteLine($"  {Preview(result.Text)}");
                    if (!string.IsNullOrWhiteSpace(result.GraphTrail))
                    {
                        Console.WriteLine($"  trail: {result.GraphTrail}");
                    }
                    Console.WriteLine();
                }
            });
        });

        return command;
    }

    private static Command CreateEntities(Option<string> dbOption, Option<string?> modelOption)
    {
        var command = new Command("entities", "List entity keys available for graph retrieval and enrichment");
        command.SetHandler(async (string db, string? model) =>
        {
            await RunAsync(db, model, async kb =>
            {
                await kb.InitializeAsync();
                var entities = await kb.ListEntitiesAsync();
                if (entities.Count == 0)
                {
                    Console.WriteLine("No entities indexed.");
                    return;
                }

                foreach (var entity in entities)
                {
                    Console.WriteLine($"{entity.Key}  [{entity.Kind}]  {entity.Surface}");
                }
            });
        }, dbOption, modelOption);
        return command;
    }

    private static Command CreateEntity(Option<string> dbOption, Option<string?> modelOption)
    {
        var command = new Command("entity", "Register an agent-verified entity mention from an indexed source");
        var keyArg = new Argument<string>("key", "Stable entity key, such as 'person:bob tagart' or 'concept:root cause culture'");
        var kindOption = new Option<string>("--kind", "Entity type, such as person, organization, or concept") { IsRequired = true };
        var surfaceOption = new Option<string>("--surface", "Exact entity text in the source") { IsRequired = true };
        var sourceOption = new Option<string>("--source", "Indexed source path containing the evidence") { IsRequired = true };
        var evidenceOption = new Option<string>("--evidence", "Exact source text supporting the entity") { IsRequired = true };
        command.AddArgument(keyArg);
        command.AddOption(kindOption);
        command.AddOption(surfaceOption);
        command.AddOption(sourceOption);
        command.AddOption(evidenceOption);

        command.SetHandler(async (string db, string? model, string key, string kind, string surface, string source, string evidence) =>
        {
            await RunAsync(db, model, async kb =>
            {
                await kb.InitializeAsync();
                await kb.RegisterEntityAsync(new KbEntityMention(kind, surface, key), evidence, source);
                Console.WriteLine($"Registered '{key}' from {Path.GetFullPath(source)}.");
            });
        }, dbOption, modelOption, keyArg, kindOption, surfaceOption, sourceOption, evidenceOption);
        return command;
    }

    private static Command CreateRelate(Option<string> dbOption, Option<string?> modelOption)
    {
        var command = new Command("relate", "Add an agent-confirmed relationship between registered entity keys");
        var subjectArg = new Argument<string>("subject", "Subject entity key from `kb entities`");
        var predicateArg = new Argument<string>("predicate", "Normalized relationship verb, such as 'authorized'");
        var objectArg = new Argument<string>("object", "Object entity key from `kb entities`");
        var evidenceOption = new Option<string>("--evidence", "Source evidence supporting the relationship") { IsRequired = true };
        var sourceOption = new Option<string>("--source", "Indexed source path containing the evidence") { IsRequired = true };
        var confidenceOption = new Option<double>("--confidence", () => 0.9, "Confidence from 0 through 1");
        command.AddArgument(subjectArg);
        command.AddArgument(predicateArg);
        command.AddArgument(objectArg);
        command.AddOption(evidenceOption);
        command.AddOption(sourceOption);
        command.AddOption(confidenceOption);

        command.SetHandler(async (string db, string? model, string subject, string predicate, string target, string evidence, string source, double confidence) =>
        {
            await RunAsync(db, model, async kb =>
            {
                await kb.InitializeAsync();
                await kb.AddEntityRelationshipAsync(new KbEntityRelationship(
                    subject,
                    predicate,
                    target,
                    evidence,
                    source,
                    confidence,
                    "agent-asserted"));
                Console.WriteLine($"Added '{predicate}' relationship from {subject} to {target}.");
            });
        }, dbOption, modelOption, subjectArg, predicateArg, objectArg, evidenceOption, sourceOption, confidenceOption);
        return command;
    }

    private static Command CreateRebuild(Option<string> dbOption, Option<string?> modelOption)
    {
        var command = new Command("rebuild", "Re-chunk and re-embed every indexed document");
        command.SetHandler(async (string db, string? model) =>
        {
            await RunAsync(db, model, async kb =>
            {
                await kb.InitializeAsync();
                var chunks = await kb.RebuildAsync();
                Console.WriteLine($"Rebuilt {chunks} chunk(s).");
            }, allowEmbeddingMismatch: true);
        }, dbOption, modelOption);

        return command;
    }

    private static Command CreateOptimize(Option<string> dbOption, Option<string?> modelOption)
    {
        var command = new Command("optimize", "Compact the database and refresh statistics");
        command.SetHandler(async (string db, string? model) =>
        {
            await RunAsync(db, model, async kb =>
            {
                await kb.InitializeAsync();
                await kb.OptimizeAsync();
                Console.WriteLine("Optimized.");
            });
        }, dbOption, modelOption);

        return command;
    }

    private static Command CreateModels()
    {
        var models = new Command("models", "Manage local ONNX embedding models");
        var catalog = new HuggingFaceModelCatalog();

        var list = new Command("list", "List available and installed embedding models");
        list.SetHandler(() =>
        {
            foreach (var descriptor in catalog.Available)
            {
                var state = catalog.IsInstalled(descriptor.Id) ? "installed" : "not installed";
                var isDefault = descriptor.Id == catalog.DefaultModelId ? " (default)" : string.Empty;
                Console.WriteLine($"{descriptor.Id}{isDefault} [{descriptor.Dimension}d] - {state}");
                Console.WriteLine($"  {descriptor.Description}");
            }
        });
        models.AddCommand(list);

        var install = new Command("install", "Download an embedding model for offline use");
        var installArg = new Argument<string>("model", () => "default", "Model id, or 'default'");
        install.AddArgument(installArg);
        install.SetHandler(async (string model) =>
        {
            try
            {
                var directory = await catalog.EnsureInstalledAsync(model);
                Console.WriteLine($"Model '{catalog.Resolve(model).Id}' installed at {directory}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, installArg);
        models.AddCommand(install);

        var remove = new Command("remove", "Delete a downloaded embedding model");
        var removeArg = new Argument<string>("model", "Model id to remove");
        remove.AddArgument(removeArg);
        remove.SetHandler((string model) =>
        {
            try
            {
                catalog.Remove(model);
                Console.WriteLine($"Model '{model}' removed.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, removeArg);
        models.AddCommand(remove);

        return models;
    }

    private static Command CreateAgentPrompt()
    {
        var command = new Command("prompt", "Print install and usage instructions for a local agent harness");
        command.SetHandler(() => Console.WriteLine("""
            You can use `chimpiler kb` as a local, agent-ready knowledge-retrieval tool.

            Bootstrap:
            - If `chimpiler` is unavailable, install it with `dotnet tool install --global Chimpiler`.
            - If it is already installed, update it with `dotnet tool update --global Chimpiler`.
            - If a package mirror provides an older CLI without `kb`, add `--add-source https://api.nuget.org/v3/index.json --ignore-failed-sources` to the install or update command.
            - Run `chimpiler kb prompt` again after installation to get this guidance.

            Setup:
            - `chimpiler kb init`
            - `chimpiler kb add <file-or-directory> --pattern "*.md"`
            - `chimpiler kb list`

            Retrieval:
            - Use `chimpiler kb search "<question>" --top 5` for direct semantic retrieval.
            - Read the cited source chunks before making connections. For broad questions, delegate one focused source/theme to each subagent, then return only evidence, entity candidates, and relationships to the orchestrator.
            - Use `chimpiler kb graph-search "<question>" --top 3 --depth 2` only after you have added relevant evidence-backed graph facts. Depth means relationship hops: one hop reaches one connected source; two hops bridges a shared concept. It traverses agent-authored evidence edges, not document structure.
            - Results marked `(graph)` include a `trail:` of entities and predicates. Verify the cited source text and trail before making a claim.

            Entity graph:
            - KB indexes chunks and local embeddings; it does not infer entities, aliases, or relationships from prose. Use your reasoning over cited chunks to decide what is worth adding.
            - Register every entity with exact source evidence: `chimpiler kb entity <key> --kind <type> --surface "<exact text>" --source <path> --evidence "<quoted source>"`. Evidence may omit Markdown link URLs; KB stores the matching canonical source excerpt and prints candidate excerpts if it cannot match.
            - Model ambiguous aliases explicitly as evidence-backed relationships instead of assuming identity.
            - Use `chimpiler kb entities` to inspect registered keys. Add verified relationships with `chimpiler kb relate <subject-key> <predicate> <object-key> --source <path> --evidence "<quoted source>"`.
            - Do not add speculative or uncited facts. The graph is an agent-maintained evidence index, not an automatic truth extractor.

            Models:
            - `chimpiler kb models install default` installs the local embedding model.
            - Add `--model default` to `init`, `add`, `search`, and `graph-search` for semantic ONNX embeddings; omit it for the offline hash fallback.
            """));
        return command;
    }

    private static async Task RunAsync(
        string databasePath,
        string? modelId,
        Func<IKnowledgeBase, Task> action,
        bool allowEmbeddingMismatch = false)
    {
        try
        {
            var options = new KnowledgeBaseOptions
            {
                DatabasePath = databasePath,
                ModelId = modelId,
                AllowEmbeddingMismatch = allowEmbeddingMismatch
            };

            await using var provider = KnowledgeBaseFactory.Build(options);
            await action(provider.GetRequiredService<IKnowledgeBase>());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static string Preview(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 160 ? single : single[..160] + "…";
    }
}
