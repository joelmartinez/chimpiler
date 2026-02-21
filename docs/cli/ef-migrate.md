EF-MIGRATE - Generate DACPACs from EF Core DbContext
================================================================================

Generate DACPAC files from Entity Framework Core DbContext models without
requiring a live database connection, existing migrations, or manual SQL.

USAGE
  $ chimpiler ef-migrate --assembly <path> [options]

OPTIONS
--------------------------------------------------------------------------------

Required:
  -a, --assembly <path>     Path to compiled .NET assembly with DbContext types

Optional:
  -c, --context <name>      Fully qualified DbContext type name (default: all)
  -o, --output <folder>     Output directory (default: ./output)
  -v, --verbose             Enable detailed logging
  -h, --help                Show help

EXAMPLES
--------------------------------------------------------------------------------

  Generate DACPACs for all DbContexts:
    $ chimpiler ef-migrate -a path/to/YourApp.dll

  Generate for a specific DbContext:
    $ chimpiler ef-migrate -a path/to/YourApp.dll -c YourNamespace.OrdersDbContext

  Custom output with verbose logging:
    $ chimpiler ef-migrate -a path/to/YourApp.dll -o ./dacpacs -v

DACPAC NAMING
--------------------------------------------------------------------------------

Files are named based on the DbContext type:

  1. Strip 'DbContext' suffix if present
  2. Strip 'Context' suffix if present
  3. Append '.dacpac'

  TheDatabaseContext  ->  TheDatabase.dacpac
  OrdersDbContext     ->  Orders.dacpac
  ReportingContext    ->  Reporting.dacpac

SUPPORTED EF CORE FEATURES
--------------------------------------------------------------------------------

Supported:
  [x] Tables, columns, data types
  [x] Primary keys (simple and composite)
  [x] Foreign keys and relationships
  [x] Indexes (unique and non-unique)
  [x] Schemas (including custom schemas)
  [x] Column nullability and max length
  [x] Identity columns
  [x] Decimal precision
  [x] Delete behaviors (Cascade, SetNull, Restrict)

Not yet supported:
  [ ] Temporal tables
  [ ] Memory-optimized tables
  [ ] Computed columns
  [ ] Check constraints
  [ ] Default constraints
  [ ] Filtered indexes
  [ ] Stored procedures, functions, views, triggers
  [ ] Full-text indexes
  [ ] Service Broker objects
  [ ] CLR types
  [ ] Row-level security

HOW IT WORKS
--------------------------------------------------------------------------------

  1. Assembly Loading
     Loads the target assembly via reflection

  2. DbContext Discovery
     Finds all types inheriting from DbContext

  3. Model Extraction
     For each DbContext:
       - Instantiates the context
       - Builds the EF Core runtime model
       - Extracts relational metadata (tables, columns, keys, indexes)

  4. DACPAC Generation
     Translates EF Core model to SQL Server schema using DacFx APIs

  5. File Output
     Writes each DACPAC to the output directory

NOTES
--------------------------------------------------------------------------------

  - No live SQL Server connection required
  - DbContext must have parameterless constructor or configure OnConfiguring
  - Target assemblies can use a different EF Core patch/minor version than Chimpiler within the same major version
  - Generated DACPACs work with SqlPackage and Azure DevOps pipelines
  - One DACPAC per DbContext
