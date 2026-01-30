# ef-migrate

Generate DACPACs from EF Core DbContext models.

## Description

The `ef-migrate` command generates one or more DACPAC files from Entity Framework Core DbContext models defined in a compiled .NET assembly. Each DbContext represents a distinct database, and the tool emits **one DACPAC per DbContext** without requiring:

- A live database connection
- Existing EF Core migrations
- Manual SQL scripting
- Database import/export workflows

## Usage

```bash
chimpiler ef-migrate --assembly <path> [options]
```

## Options

### Required

- `-a, --assembly <path>` - Path to a compiled .NET assembly containing one or more EF Core DbContext types

### Optional

- `-c, --context <Fully.Qualified.TypeName>` - Fully qualified type name of a specific DbContext. If omitted, all DbContexts will be processed
- `-o, --output <folder>` - Output directory for generated DACPACs (default: `./output`)
- `-v, --verbose` - Enable detailed logging
- `-h, --help` - Show help information

## Examples

Generate DACPACs for all DbContexts in an assembly:

```bash
chimpiler ef-migrate --assembly path/to/YourApp.dll
```

Generate a DACPAC for a specific DbContext:

```bash
chimpiler ef-migrate --assembly path/to/YourApp.dll --context YourNamespace.OrdersDbContext
```

Custom output directory with verbose logging:

```bash
chimpiler ef-migrate --assembly path/to/YourApp.dll --output ./dacpacs --verbose
```

## DACPAC Naming

DACPAC files are named based on the DbContext type name:

1. Strip the `DbContext` suffix if present
2. Strip the `Context` suffix if present
3. Append `.dacpac`

Examples:
- `TheDatabaseContext` → `TheDatabase.dacpac`
- `OrdersDbContext` → `Orders.dacpac`
- `ReportingContext` → `Reporting.dacpac`

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
- Stored procedures, functions, views, triggers
- Full-text indexes
- Service Broker objects
- CLR types
- Row-level security

## How It Works

1. **Assembly Loading** - Loads the target assembly via reflection
2. **DbContext Discovery** - Discovers all types inheriting from `DbContext`
3. **Model Extraction** - For each DbContext:
   - Instantiates the context
   - Builds the EF Core runtime model
   - Extracts relational metadata (tables, columns, keys, indexes, schemas)
4. **DACPAC Generation** - Translates the EF Core model into SQL Server schema objects using DacFx APIs
5. **File Output** - Writes each DACPAC to the output directory

## Notes

- No live SQL Server connection is required
- The DbContext must have a parameterless constructor or configure `OnConfiguring`
- Generated DACPACs are compatible with SqlPackage and Azure DevOps deployment pipelines
