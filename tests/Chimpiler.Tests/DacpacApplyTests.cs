using Chimpiler.Dacpac;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

namespace Chimpiler.Tests;

public sealed class DacpacModelReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        "dacpac-tests",
        Guid.NewGuid().ToString("N"));

    public DacpacModelReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public void Read_extracts_supported_schema_objects()
    {
        var path = BuildDacpac("""
            CREATE SCHEMA [sales];
            GO
            CREATE TABLE [sales].[Customers] (
                [Id] INT IDENTITY(5, 2) NOT NULL,
                [Email] NVARCHAR(200) NOT NULL CONSTRAINT [DF_Customers_Email] DEFAULT (N'unknown'),
                CONSTRAINT [PK_Customers] PRIMARY KEY ([Id]),
                CONSTRAINT [UQ_Customers_Email] UNIQUE ([Email])
            );
            GO
            CREATE TABLE [sales].[Orders] (
                [Id] BIGINT NOT NULL,
                [CustomerId] INT NOT NULL,
                CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId])
                    REFERENCES [sales].[Customers]([Id]) ON DELETE CASCADE
            );
            GO
            CREATE INDEX [IX_Orders_CustomerId] ON [sales].[Orders]([CustomerId]);
            """);

        var schema = new DacpacModelReader().Read(path);

        var customers = Assert.Single(schema.Tables, table => table.Name == "Customers");
        var id = Assert.Single(customers.Columns, column => column.Name == "Id");
        Assert.Equal(new IdentityDefinition(5, 2), id.Identity);
        Assert.Equal("(N'unknown')", Assert.Single(customers.Columns, column => column.Name == "Email").DefaultExpression);
        Assert.Equal("PK_Customers", customers.PrimaryKey!.Name);
        Assert.Single(customers.UniqueConstraints);

        var orders = Assert.Single(schema.Tables, table => table.Name == "Orders");
        Assert.Equal(ReferentialAction.Cascade, Assert.Single(orders.ForeignKeys).OnDelete);
        Assert.Equal("IX_Orders_CustomerId", Assert.Single(orders.Indexes).Name);
    }

    [Fact]
    public void Read_rejects_unsupported_objects()
    {
        var path = BuildDacpac("""
            CREATE TABLE [dbo].[Items] ([Id] INT NOT NULL);
            GO
            CREATE VIEW [dbo].[ItemView] AS SELECT [Id] FROM [dbo].[Items];
            """);

        var exception = Assert.Throws<DacpacCompatibilityException>(() => new DacpacModelReader().Read(path));

        Assert.Contains("View", exception.Message);
    }

    [Fact]
    public void Read_rejects_explicit_column_collation()
    {
        var path = BuildDacpac("""
            CREATE TABLE [dbo].[Items] (
                [Name] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL
            );
            """);

        var exception = Assert.Throws<DacpacCompatibilityException>(() => new DacpacModelReader().Read(path));

        Assert.Contains("explicit collation", exception.Message);
        Assert.Contains("Latin1_General_100_BIN2", exception.Message);
    }

    [Fact]
    public void Read_rejects_non_default_database_collation()
    {
        var path = BuildDacpac(
            "CREATE TABLE [dbo].[Items] ([Name] NVARCHAR(100) NULL);",
            new TSqlModelOptions { Collation = "Latin1_General_100_BIN2" });

        var exception = Assert.Throws<DacpacCompatibilityException>(() => new DacpacModelReader().Read(path));

        Assert.Contains("Database collation", exception.Message);
        Assert.Contains("Latin1_General_100_BIN2", exception.Message);
    }

    [Fact]
    public async Task Duplicate_postgresql_index_names_fail_before_connecting()
    {
        var path = BuildDacpac("""
            CREATE TABLE [dbo].[Customers] ([Id] INT NOT NULL);
            GO
            CREATE INDEX [IX_Shared] ON [dbo].[Customers] ([Id]);
            GO
            CREATE TABLE [dbo].[Orders] ([Id] INT NOT NULL);
            GO
            CREATE INDEX [IX_Shared] ON [dbo].[Orders] ([Id]);
            """);

        var exception = await Assert.ThrowsAsync<DacpacCompatibilityException>(() =>
            new DacpacDeploymentService().ApplyAsync(new DacpacApplyOptions
            {
                DacpacPath = path,
                Provider = "postgresql",
                ConnectionString = "Host=does-not-exist.invalid;Timeout=1"
            }));

        Assert.Contains("PostgreSQL index name 'IX_Shared' collides", exception.Message);
    }

    private string BuildDacpac(string script, TSqlModelOptions? options = null)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.dacpac");
        using var model = new TSqlModel(SqlServerVersion.Sql160, options ?? new TSqlModelOptions());
        model.AddObjects(script);
        DacPackageExtensions.BuildPackage(path, model, new PackageMetadata
        {
            Name = "ChimpilerTest",
            Version = "1.0.0.0"
        });
        return path;
    }
}

public sealed class PostgreSqlModelTranslatorTests
{
    private readonly PostgreSqlModelTranslator _translator = new();

    [Theory]
    [InlineData("Int", 0, 0, 0, false, "integer")]
    [InlineData("BigInt", 0, 0, 0, false, "bigint")]
    [InlineData("NVarChar", 400, 0, 0, false, "character varying(400)")]
    [InlineData("NVarChar", 0, 0, 0, true, "text")]
    [InlineData("Decimal", 0, 18, 2, false, "numeric(18,2)")]
    [InlineData("UniqueIdentifier", 0, 0, 0, false, "uuid")]
    [InlineData("DateTime2", 0, 0, 3, false, "timestamp(3) without time zone")]
    public void MapType_maps_supported_scalar_types(
        string name,
        int length,
        int precision,
        int scale,
        bool isMax,
        string expected)
    {
        Assert.Equal(expected, _translator.MapType(new SqlServerType(name, length, precision, scale, isMax)));
    }

    [Theory]
    [InlineData("((1))", "number:1", "1")]
    [InlineData("(N'it''s ready')", "string:it's ready", "'it''s ready'")]
    [InlineData("(GETDATE())", "timestamp:current", "CURRENT_TIMESTAMP")]
    [InlineData("(NEWID())", "uuid:random", "gen_random_uuid()")]
    public void TranslateDefault_allows_only_known_safe_expressions(string source, string canonical, string sql)
    {
        var result = _translator.TranslateDefault(source, new SqlServerType("Int"));
        Assert.Equal(canonical, result.Canonical);
        Assert.Equal(sql, result.Sql);
    }

    [Fact]
    public void TranslateDefault_rejects_arbitrary_sql() =>
        Assert.Throws<DacpacCompatibilityException>(() =>
            _translator.TranslateDefault("(NEXT VALUE FOR dbo.Sequence)", new SqlServerType("Int")));

    [Fact]
    public void MapType_rejects_unsupported_types() =>
        Assert.Throws<DacpacCompatibilityException>(() =>
            _translator.MapType(new SqlServerType("SqlVariant")));

    [Fact]
    public void Translate_rejects_identity_on_non_integer_type()
    {
        var source = new DacpacSchema(
        [
            new DacpacTable(
                "dbo",
                "Items",
                [new DacpacColumn("Id", new SqlServerType("Decimal", Precision: 18, Scale: 0), false, null, new IdentityDefinition(1, 1))],
                null,
                [],
                [],
                [])
        ]);

        Assert.Throws<DacpacCompatibilityException>(() => _translator.Translate(source));
    }

    [Fact]
    public void Translate_rejects_nullable_unique_constraint()
    {
        var source = SchemaWithNullableUnique(
            uniqueConstraints: [new DacpacKey("UQ_Items_Code", ["Code"])],
            indexes: []);

        var exception = Assert.Throws<DacpacCompatibilityException>(() => _translator.Translate(source));

        Assert.Contains("nullable column(s) Code", exception.Message);
    }

    [Fact]
    public void Translate_rejects_nullable_unique_index()
    {
        var source = SchemaWithNullableUnique(
            uniqueConstraints: [],
            indexes: [new DacpacIndex("IX_Items_Code", true, [new IndexColumn("Code", true)], [])]);

        var exception = Assert.Throws<DacpacCompatibilityException>(() => _translator.Translate(source));

        Assert.Contains("nullable column(s) Code", exception.Message);
    }

    [Fact]
    public void Translate_rejects_schema_wide_index_name_collisions_deterministically()
    {
        var source = new DacpacSchema(
        [
            TableWithIndex("Zebra", "IX_Shared"),
            TableWithIndex("Alpha", "IX_Shared")
        ]);

        var exception = Assert.Throws<DacpacCompatibilityException>(() => _translator.Translate(source));

        Assert.Equal(
            "PostgreSQL index name 'IX_Shared' collides within schema 'public': public.Alpha index, public.Zebra index. PostgreSQL index names must be unique within a schema.",
            exception.Message);
    }

    private static DacpacSchema SchemaWithNullableUnique(
        IReadOnlyList<DacpacKey> uniqueConstraints,
        IReadOnlyList<DacpacIndex> indexes) =>
        new(
        [
            new DacpacTable(
                "dbo",
                "Items",
                [new DacpacColumn("Code", new SqlServerType("NVarChar", Length: 50), true, null, null)],
                null,
                uniqueConstraints,
                [],
                indexes)
        ]);

    private static DacpacTable TableWithIndex(string name, string indexName) =>
        new(
            "dbo",
            name,
            [new DacpacColumn("Id", new SqlServerType("Int"), false, null, null)],
            null,
            [],
            [],
            [new DacpacIndex(indexName, false, [new IndexColumn("Id", true)], [])]);
}

public sealed class SchemaDifferTests
{
    [Fact]
    public void Diff_is_empty_for_equal_schemas()
    {
        var schema = Schema();
        Assert.Empty(new SchemaDiffer().Diff(schema, schema).Operations);
    }

    [Fact]
    public void Diff_orders_additive_operations_deterministically()
    {
        var plan = new SchemaDiffer().Diff(Schema(), DatabaseSchema.Empty);

        Assert.Collection(
            plan.Operations,
            operation => Assert.Equal("Create table public.Items", operation.Description),
            operation => Assert.Equal("Add primary key PK_Items to public.Items", operation.Description),
            operation => Assert.Equal("Create index IX_Items_Name on public.Items", operation.Description));
        Assert.DoesNotContain(plan.Operations, operation => operation.IsDestructive);
    }

    [Fact]
    public void Diff_marks_column_removal_destructive()
    {
        var desired = Schema();
        var currentTable = desired.Tables[0] with
        {
            Columns = [.. desired.Tables[0].Columns, new DatabaseColumn("Legacy", "text", true, null, null)]
        };

        var operation = Assert.Single(new SchemaDiffer().Diff(desired, new DatabaseSchema([currentTable])).Operations);
        Assert.True(operation.IsDestructive);
        Assert.Contains("DROP COLUMN", operation.Sql);
    }

    [Fact]
    public void Diff_rebuilds_dependent_indexes_around_type_changes()
    {
        var desired = Schema();
        var currentTable = desired.Tables[0] with
        {
            Columns =
            [
                desired.Tables[0].Columns[0],
                desired.Tables[0].Columns[1] with { StoreType = "character varying(20)" }
            ]
        };

        var operations = new SchemaDiffer().Diff(desired, new DatabaseSchema([currentTable])).Operations;

        Assert.Equal(3, operations.Count);
        Assert.StartsWith("DROP INDEX", operations[0].Sql);
        Assert.Contains("ALTER COLUMN \"Name\" TYPE text", operations[1].Sql);
        Assert.StartsWith("CREATE INDEX", operations[2].Sql);
    }

    [Fact]
    public void Diff_changes_identity_increment_without_restarting_sequence()
    {
        var desired = Schema();
        var currentTable = desired.Tables[0] with
        {
            Columns =
            [
                desired.Tables[0].Columns[0] with { Identity = new IdentityDefinition(1, 5) },
                desired.Tables[0].Columns[1]
            ],
            HasRows = true
        };

        var operation = Assert.Single(new SchemaDiffer().Diff(desired, new DatabaseSchema([currentTable])).Operations);

        Assert.Contains("SET INCREMENT BY 1", operation.Sql);
        Assert.DoesNotContain("RESTART", operation.Sql);
    }

    [Fact]
    public void Diff_rejects_identity_seed_changes()
    {
        var desired = Schema();
        var currentTable = desired.Tables[0] with
        {
            Columns =
            [
                desired.Tables[0].Columns[0] with { Identity = new IdentityDefinition(10, 1) },
                desired.Tables[0].Columns[1]
            ]
        };

        var exception = Assert.Throws<DacpacCompatibilityException>(
            () => new SchemaDiffer().Diff(desired, new DatabaseSchema([currentTable])));

        Assert.Contains("Cannot change identity seed", exception.Message);
    }

    [Fact]
    public void Diff_rejects_adding_identity_to_populated_column()
    {
        var desired = Schema();
        var currentTable = desired.Tables[0] with
        {
            Columns =
            [
                desired.Tables[0].Columns[0] with { Identity = null },
                desired.Tables[0].Columns[1]
            ],
            HasRows = true
        };

        var exception = Assert.Throws<DacpacCompatibilityException>(
            () => new SchemaDiffer().Diff(desired, new DatabaseSchema([currentTable])));

        Assert.Contains("Cannot add identity to populated column public.Items.Id", exception.Message);
    }

    [Fact]
    public void Diff_allows_adding_identity_to_empty_existing_table()
    {
        var desired = Schema();
        var currentTable = desired.Tables[0] with
        {
            Columns =
            [
                desired.Tables[0].Columns[0] with { Identity = null },
                desired.Tables[0].Columns[1]
            ]
        };

        var operation = Assert.Single(new SchemaDiffer().Diff(desired, new DatabaseSchema([currentTable])).Operations);

        Assert.Contains("ADD GENERATED BY DEFAULT AS IDENTITY", operation.Sql);
    }

    [Fact]
    public void DestructiveChangesException_lists_rejected_operations()
    {
        var operation = new DeploymentOperation(1, "x", "Drop table public.Legacy", "DROP TABLE x", true);
        var exception = new DestructiveChangesException([operation]);
        Assert.Contains("Drop table public.Legacy", exception.Message);
    }

    private static DatabaseSchema Schema()
    {
        var table = new DatabaseTable(
            "public",
            "Items",
            [
                new DatabaseColumn("Id", "integer", false, null, new IdentityDefinition(1, 1)),
                new DatabaseColumn("Name", "text", false, new DatabaseDefault("'unknown'", "string:unknown"), null)
            ],
            new DatabaseKey("PK_Items", ["Id"]),
            [],
            [],
            [new DatabaseIndex("IX_Items_Name", false, [new IndexColumn("Name", true)], [])]);
        return new DatabaseSchema([table]);
    }
}

public sealed class PostgreSqlSqlGeneratorTests
{
    [Fact]
    public void GenerateScript_wraps_operations_in_transaction_and_advisory_lock()
    {
        var plan = new DeploymentPlan(
            [new DeploymentOperation(1, "a", "Create schema app", "CREATE SCHEMA \"app\"", false)]);

        var script = new PostgreSqlSqlGenerator().GenerateScript(plan);

        Assert.StartsWith("-- Generated by Chimpiler", script);
        Assert.Contains("BEGIN;", script);
        Assert.Contains("pg_advisory_xact_lock", script);
        Assert.Contains("CREATE SCHEMA \"app\";", script);
        Assert.EndsWith("COMMIT;\n", script);
    }

    [Fact]
    public void CreateTable_quotes_identifiers_and_emits_identity_default_and_nullability()
    {
        var table = new DatabaseTable(
            "public",
            "Order",
            [new DatabaseColumn("Id", "integer", false, new DatabaseDefault("7", "number:7"), new IdentityDefinition(5, 2))],
            null,
            [],
            [],
            []);

        var sql = new PostgreSqlSqlGenerator().CreateTable(table);

        Assert.Contains("\"public\".\"Order\"", sql);
        Assert.Contains("\"Id\" integer GENERATED BY DEFAULT AS IDENTITY (START WITH 5 INCREMENT BY 2) DEFAULT 7 NOT NULL", sql);
    }
}

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_removes_connection_string_and_individual_secret_values()
    {
        const string connection = "Host=db;Username=app;Password=super-secret";
        var result = SecretRedactor.Redact(
            $"Failed with {connection}; Password=another-secret;Token=abc123",
            connection);

        Assert.DoesNotContain("super-secret", result);
        Assert.DoesNotContain("another-secret", result);
        Assert.DoesNotContain("abc123", result);
        Assert.Contains("[REDACTED", result);
    }

    public sealed class PostgreSqlCatalogReaderTests
    {
        [Theory]
        [InlineData("'hello'::character varying", "string:hello")]
        [InlineData("true", "boolean:true")]
        [InlineData("now()", "timestamp:current")]
        [InlineData("gen_random_uuid()", "uuid:random")]
        [InlineData("NULL::integer", "null")]
        public void ParseDefault_normalizes_postgresql_catalog_expressions(string expression, string expected) =>
            Assert.Equal(expected, PostgreSqlCatalogReader.ParseDefault(expression)!.Canonical);
    }
}
