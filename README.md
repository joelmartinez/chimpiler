# Chimpiler

A multi-purpose CLI tooling ecosystem for .NET.

## Overview

Chimpiler is an extensible CLI framework designed to provide pragmatic tooling for modern .NET applications and development workflows.

## Features

### `chimpiler clawcker` — OpenClaw Instance Manager

Clawcker makes it trivially easy to create, run, and access local OpenClaw instances using Docker. Get started with just three commands:

```bash
chimpiler clawcker new myagent
chimpiler clawcker start myagent
chimpiler clawcker talk myagent
```

**Key Features:**
- ✅ One-command instance creation
- ✅ Automatic Docker image pulling
- ✅ Secure random token generation
- ✅ Persistent configuration and workspace
- ✅ Easy web UI access
- ✅ Multiple instance management

[Learn more about Clawcker →](docs/cli/clawcker.md)

### `chimpiler ef-migrate` — EF Core Model → DACPAC Generator

The `ef-migrate` command generates one or more DACPAC files from EF Core DbContext models defined in a compiled .NET assembly. Each DbContext represents a distinct database, and the tool emits **one DACPAC per DbContext** without requiring:

- A live database connection
- Existing EF Core migrations
- Manual SQL scripting
- Database import/export workflows

**Key Benefits:**
- ✅ Fully automated, CI-friendly workflow
- ✅ Model-driven schema generation
- ✅ DACPAC artifacts compatible with SqlPackage and Azure DevOps
- ✅ Deterministic and repeatable output
- ✅ No dependency on SQL Server during build

[Learn more about ef-migrate →](docs/cli/ef-migrate.md)

## Installation

### As a .NET Global Tool

Install Chimpiler globally using the .NET CLI:

```bash
dotnet tool install -g Chimpiler
```

To update to the latest version:

```bash
dotnet tool update -g Chimpiler
```

### From Source

```bash
git clone https://github.com/joelmartinez/chimpiler.git
cd chimpiler
dotnet build
dotnet pack src/Chimpiler/Chimpiler.csproj -c Release
dotnet tool install -g --add-source ./src/Chimpiler/bin/Release Chimpiler
```

## Usage

### Basic Usage

Generate DACPACs for all DbContexts in an assembly:

```bash
chimpiler ef-migrate --assembly path/to/YourApp.dll
```

This will discover all DbContext types in the assembly and generate a DACPAC for each in the `./output` directory.

### Specify a Single DbContext

Generate a DACPAC for a specific DbContext:

```bash
chimpiler ef-migrate --assembly path/to/YourApp.dll --context YourNamespace.OrdersDbContext
```

### Custom Output Directory

```bash
chimpiler ef-migrate --assembly path/to/YourApp.dll --output ./dacpacs
```

### Enable Verbose Logging

```bash
chimpiler ef-migrate --assembly path/to/YourApp.dll --verbose
```

## Command Reference

### `ef-migrate`

Generate DACPACs from EF Core DbContext models.

**Options:**

| Option | Alias | Required | Description | Default |
|--------|-------|----------|-------------|---------|
| `--assembly` | `-a` | ✅ | Path to compiled .NET assembly containing DbContext types | - |
| `--context` | `-c` | ❌ | Fully qualified type name of a specific DbContext | All DbContexts |
| `--output` | `-o` | ❌ | Output directory for generated DACPACs | `./output` |
| `--framework` | `-f` | ❌ | Target framework hint for multi-targeted assemblies | - |
| `--verbose` | `-v` | ❌ | Enable detailed logging | `false` |

## DACPAC Naming

DACPAC files are named based on the DbContext type name with the following rules:

1. Strip the `Context` suffix if present
2. Strip the `DbContext` suffix if present  
3. Append `.dacpac`

**Examples:**

| DbContext Type | DACPAC Filename |
|----------------|-----------------|
| `TheDatabaseContext` | `TheDatabase.dacpac` |
| `OrdersDbContext` | `Orders.dacpac` |
| `ReportingContext` | `Reporting.dacpac` |
| `InventoryContext` | `Inventory.dacpac` |

## How It Works

1. **Assembly Loading** — The tool loads the target assembly via reflection
2. **DbContext Discovery** — Discovers all types inheriting from `DbContext`
3. **Model Extraction** — For each DbContext:
   - Instantiates the context
   - Builds the EF Core runtime model
   - Extracts relational metadata (tables, columns, keys, indexes, schemas)
4. **DACPAC Generation** — Translates the EF Core model into SQL Server schema objects using DacFx APIs
5. **File Output** — Writes each DACPAC to the output directory

## Comparison to Alternatives

### vs. EF Core Migrations

| Feature | Chimpiler `ef-migrate` | EF Core Migrations |
|---------|------------------------|-------------------|
| Approach | State-based (DACPAC) | Migration-based |
| Output | `.dacpac` files | C# migration files |
| Database Required | ❌ No | ❌ No |
| Deployment | SqlPackage / Azure DevOps | `dotnet ef database update` |
| Change Tracking | Handled by SqlPackage | Handled by EF Core |
| Reversibility | Via DACPAC snapshots | Via down migrations |

**When to use Chimpiler:**
- You prefer state-based deployments
- You want DACPAC artifacts for CI/CD
- You're prototyping and need fast iteration

**When to use EF Migrations:**
- You need custom migration logic
- You want version-controlled migration history
- You need seed data or manual SQL customization

### vs. SQL Server Database Projects (SSDT)

| Feature | Chimpiler `ef-migrate` | SSDT |
|---------|------------------------|------|
| Schema Source | EF Core models | Hand-written SQL |
| Tooling | CLI | Visual Studio |
| Automation | ✅ Full | ⚠️ Limited |
| Learning Curve | Low (if you know EF) | Medium-High |
| Advanced SQL Features | ⚠️ Limited | ✅ Full |

**When to use Chimpiler:**
- Your source of truth is EF Core models
- You want automation in CI/CD
- You're building new applications

**When to use SSDT:**
- You need advanced SQL Server features
- Your DBAs prefer SQL-first workflows
- You have existing database projects

### vs. EF Core Power Tools

| Feature | Chimpiler `ef-migrate` | EF Core Power Tools |
|---------|------------------------|-------------------|
| Execution | CLI / Automated | UI / Manual |
| Output | DACPACs | SQL scripts (via UI) |
| CI/CD Friendly | ✅ Yes | ❌ No |
| Visual Studio Required | ❌ No | ✅ Yes |

## Supported EF Core Features

✅ **Supported:**
- Tables, columns, data types
- Primary keys (simple and composite)
- Foreign keys and relationships
- Indexes (unique and non-unique)
- Schemas (including custom schemas)
- Column nullability and max length
- Identity columns
- Decimal precision
- Delete behaviors (Cascade, SetNull, Restrict)

⚠️ **Not Yet Supported:**
- Temporal tables
- Memory-optimized tables
- Computed columns
- Check constraints
- Default constraints
- Filtered indexes
- Stored procedures
- Functions
- Views
- Triggers
- Full-text indexes
- Service Broker objects
- CLR types
- Row-level security

These limitations are documented and may be addressed in future releases.

## Requirements

- .NET 10 or later
- SQL Server DacFx libraries (automatically included via NuGet)
- Entity Framework Core 10.x (in your target assembly)

## Development

### Build

```bash
dotnet build
```

### Run Tests

```bash
dotnet test
```

All tests are located in `tests/Chimpiler.Tests` with test fixtures in `tests/Chimpiler.TestFixtures`.

### Run Locally

```bash
dotnet run --project src/Chimpiler/Chimpiler.csproj -- ef-migrate --assembly <path> --output <path>
```

## Contributing

Contributions are welcome! Please feel free to submit issues or pull requests.

## License

MIT License - See [LICENSE](LICENSE) for details.

## Roadmap

Future subcommands and features may include:
- Additional database providers (PostgreSQL, MySQL)
- Schema comparison tools
- Migration generation from model diffs
- Data seeding utilities
- And more...

Chimpiler is designed to grow into a comprehensive database tooling ecosystem. The `ef-migrate` command is just the beginning.

---

**Built with ❤️ using .NET 10**