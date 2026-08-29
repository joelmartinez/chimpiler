using Chimpiler.Dacpac;
using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Chimpiler.Dacpac.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL DACPAC";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("chimpiler")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DROP SCHEMA IF EXISTS sales CASCADE; DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public AUTHORIZATION postgres;",
            connection);
        await command.ExecuteNonQueryAsync();
    }
}

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlDacpacApplyTests : IDisposable
{
    private readonly PostgreSqlFixture _postgres;
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        "dacpac-integration",
        Guid.NewGuid().ToString("N"));

    public PostgreSqlDacpacApplyTests(PostgreSqlFixture postgres)
    {
        _postgres = postgres;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task Empty_deployment_and_second_apply_are_idempotent()
    {
        await _postgres.ResetAsync();
        var dacpac = BuildDacpac(BaselineSql);

        var first = await ApplyAsync(dacpac);
        var second = await ApplyAsync(dacpac);

        Assert.True(first.Applied);
        Assert.NotEmpty(first.Plan.Operations);
        Assert.True(second.Applied);
        Assert.Empty(second.Plan.Operations);
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*)::int FROM information_schema.tables WHERE table_schema = 'sales'"));
    }

    [Fact]
    public async Task Additive_upgrade_preserves_data_and_deploys_identity_defaults_constraints_and_indexes()
    {
        await _postgres.ResetAsync();
        var initial = BuildDacpac("""
            CREATE TABLE [dbo].[Customers] (
                [Id] INT IDENTITY(1,1) NOT NULL,
                [Email] NVARCHAR(320) NOT NULL,
                CONSTRAINT [PK_Customers] PRIMARY KEY ([Id]),
                CONSTRAINT [UQ_Customers_Email] UNIQUE ([Email])
            );
            """);
        await ApplyAsync(initial);
        await ExecuteAsync("""INSERT INTO "public"."Customers" ("Email") VALUES ('first@example.test')""");

        var upgraded = BuildDacpac("""
            CREATE TABLE [dbo].[Customers] (
                [Id] INT IDENTITY(1,1) NOT NULL,
                [Email] NVARCHAR(320) NOT NULL,
                [Enabled] BIT NOT NULL CONSTRAINT [DF_Customers_Enabled] DEFAULT (1),
                CONSTRAINT [PK_Customers] PRIMARY KEY ([Id]),
                CONSTRAINT [UQ_Customers_Email] UNIQUE ([Email])
            );
            GO
            CREATE INDEX [IX_Customers_Email] ON [dbo].[Customers] ([Email]);
            """);

        await ApplyAsync(upgraded);

        Assert.Equal("first@example.test", await ScalarAsync<string>("SELECT \"Email\" FROM \"public\".\"Customers\" WHERE \"Id\" = 1"));
        Assert.True(await ScalarAsync<bool>("SELECT \"Enabled\" FROM \"public\".\"Customers\" WHERE \"Id\" = 1"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*)::int FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_Customers_Email'"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT count(*)::int FROM pg_constraint WHERE conname IN ('PK_Customers', 'UQ_Customers_Email')"));
        await ExecuteAsync("INSERT INTO \"public\".\"Customers\" (\"Email\") VALUES ('second@example.test')");
        Assert.Equal(2, await ScalarAsync<int>("SELECT max(\"Id\") FROM \"public\".\"Customers\""));
    }

    [Fact]
    public async Task Identity_increment_change_preserves_sequence_position_and_is_idempotent()
    {
        await _postgres.ResetAsync();
        var initial = BuildDacpac("""
            CREATE TABLE [dbo].[Items] (
                [Id] INT IDENTITY(1,1) NOT NULL,
                CONSTRAINT [PK_Items] PRIMARY KEY ([Id])
            );
            """);
        await ApplyAsync(initial);
        await ExecuteAsync("""INSERT INTO "public"."Items" DEFAULT VALUES; INSERT INTO "public"."Items" DEFAULT VALUES;""");

        var upgraded = BuildDacpac("""
            CREATE TABLE [dbo].[Items] (
                [Id] INT IDENTITY(1,5) NOT NULL,
                CONSTRAINT [PK_Items] PRIMARY KEY ([Id])
            );
            """);

        var first = await ApplyAsync(upgraded);
        var second = await ApplyAsync(upgraded);
        await ExecuteAsync("""INSERT INTO "public"."Items" DEFAULT VALUES;""");

        var operation = Assert.Single(first.Plan.Operations);
        Assert.Contains("SET INCREMENT BY 5", operation.Sql);
        Assert.DoesNotContain("RESTART", operation.Sql);
        Assert.Empty(second.Plan.Operations);
        Assert.Equal(7, await ScalarAsync<int>("SELECT max(\"Id\") FROM \"public\".\"Items\""));
    }

    [Fact]
    public async Task Adding_identity_to_populated_column_is_rejected()
    {
        await _postgres.ResetAsync();
        var initial = BuildDacpac("""
            CREATE TABLE [dbo].[Items] (
                [Id] INT NOT NULL,
                CONSTRAINT [PK_Items] PRIMARY KEY ([Id])
            );
            """);
        await ApplyAsync(initial);
        await ExecuteAsync("""INSERT INTO "public"."Items" ("Id") VALUES (42);""");

        var upgraded = BuildDacpac("""
            CREATE TABLE [dbo].[Items] (
                [Id] INT IDENTITY(1,1) NOT NULL,
                CONSTRAINT [PK_Items] PRIMARY KEY ([Id])
            );
            """);

        var exception = await Assert.ThrowsAsync<DacpacCompatibilityException>(() => ApplyAsync(upgraded));

        Assert.Contains("Cannot add identity to populated column public.Items.Id", exception.Message);
        Assert.Equal(42, await ScalarAsync<int>("SELECT \"Id\" FROM \"public\".\"Items\""));
        Assert.Equal(
            string.Empty,
            await ScalarAsync<string>("SELECT attidentity::text FROM pg_attribute WHERE attrelid = 'public.\"Items\"'::regclass AND attname = 'Id'"));
    }

    [Fact]
    public async Task Explicit_null_default_is_idempotent()
    {
        await _postgres.ResetAsync();
        var dacpac = BuildDacpac("""
            CREATE TABLE [dbo].[Items] (
                [Id] INT NOT NULL,
                [Value] NVARCHAR(100) NULL CONSTRAINT [DF_Items_Value] DEFAULT (NULL),
                CONSTRAINT [PK_Items] PRIMARY KEY ([Id])
            );
            """);

        var first = await ApplyAsync(dacpac);
        var second = await ApplyAsync(dacpac);

        Assert.NotEmpty(first.Plan.Operations);
        Assert.Empty(second.Plan.Operations);
    }

    [Fact]
    public async Task Destructive_change_is_rejected_and_data_is_preserved()
    {
        await _postgres.ResetAsync();
        var initial = BuildDacpac("""
            CREATE TABLE [dbo].[Items] (
                [Id] INT NOT NULL,
                [Value] NVARCHAR(100) NULL,
                CONSTRAINT [PK_Items] PRIMARY KEY ([Id])
            );
            """);
        await ApplyAsync(initial);
        await ExecuteAsync("""INSERT INTO "public"."Items" ("Id", "Value") VALUES (1, 'keep')""");

        var destructive = BuildDacpac("""
            CREATE TABLE [dbo].[Items] (
                [Id] INT NOT NULL,
                CONSTRAINT [PK_Items] PRIMARY KEY ([Id])
            );
            """);

        await Assert.ThrowsAsync<DestructiveChangesException>(() => ApplyAsync(destructive));
        Assert.Equal("keep", await ScalarAsync<string>("SELECT \"Value\" FROM \"public\".\"Items\" WHERE \"Id\" = 1"));
    }

    [Fact]
    public async Task Dry_run_and_script_do_not_modify_database()
    {
        await _postgres.ResetAsync();
        var dacpac = BuildDacpac(BaselineSql);
        var scriptPath = Path.Combine(_directory, "deployment.sql");

        var result = await ApplyAsync(dacpac, dryRun: true, scriptPath: scriptPath);

        Assert.False(result.Applied);
        Assert.Contains("pg_advisory_xact_lock", result.Script);
        Assert.Equal(result.Script, await File.ReadAllTextAsync(scriptPath));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*)::int FROM information_schema.tables WHERE table_schema = 'sales'"));
    }

    [Fact]
    public async Task Failed_late_operation_rolls_back_earlier_operations()
    {
        await _postgres.ResetAsync();
        await ExecuteAsync("""
            CREATE TABLE "public"."Existing" ("Id" integer NOT NULL, "Value" text NULL);
            INSERT INTO "public"."Existing" ("Id", "Value") VALUES (1, NULL);
            """);
        var dacpac = BuildDacpac("""
            CREATE TABLE [dbo].[Added] ([Id] INT NOT NULL);
            GO
            CREATE TABLE [dbo].[Existing] ([Id] INT NOT NULL, [Value] NVARCHAR(MAX) NOT NULL);
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(dacpac));

        Assert.False(await ScalarAsync<bool>("SELECT to_regclass('public.\"Added\"') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>("SELECT to_regclass('public.\"Existing\"') IS NOT NULL"));
    }

    [Fact]
    public async Task Unsupported_dacpac_fails_preflight_before_connecting()
    {
        var dacpac = BuildDacpac("""
            CREATE TABLE [dbo].[Items] ([Id] INT NOT NULL);
            GO
            CREATE VIEW [dbo].[ItemsView] AS SELECT [Id] FROM [dbo].[Items];
            """);

        var exception = await Assert.ThrowsAsync<DacpacCompatibilityException>(() =>
            new DacpacDeploymentService().ApplyAsync(new DacpacApplyOptions
            {
                DacpacPath = dacpac,
                Provider = "postgresql",
                ConnectionString = "Host=does-not-exist.invalid;Timeout=1"
            }));

        Assert.Contains("unsupported objects", exception.Message);
    }

    [Fact]
    public async Task Concurrent_applies_are_serialized_by_advisory_lock()
    {
        await _postgres.ResetAsync();
        var dacpac = BuildDacpac(BaselineSql);

        var results = await Task.WhenAll(ApplyAsync(dacpac), ApplyAsync(dacpac));

        Assert.Equal(1, results.Count(result => result.Plan.Operations.Count > 0));
        Assert.Equal(1, results.Count(result => result.Plan.Operations.Count == 0));
    }

    private Task<DeploymentResult> ApplyAsync(
        string dacpac,
        bool dryRun = false,
        string? scriptPath = null) =>
        new DacpacDeploymentService().ApplyAsync(new DacpacApplyOptions
        {
            DacpacPath = dacpac,
            Provider = "postgresql",
            ConnectionString = _postgres.ConnectionString,
            DryRun = dryRun,
            ScriptPath = scriptPath
        });

    private string BuildDacpac(string sql)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.dacpac");
        using var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions());
        model.AddObjects(sql);
        DacPackageExtensions.BuildPackage(path, model, new PackageMetadata
        {
            Name = "ChimpilerIntegration",
            Version = "1.0.0.0"
        });
        return path;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private const string BaselineSql = """
        CREATE SCHEMA [sales];
        GO
        CREATE TABLE [sales].[Customers] (
            [Id] INT IDENTITY(1,1) NOT NULL,
            [Email] NVARCHAR(320) NOT NULL,
            [CreatedAt] DATETIME2(3) NOT NULL CONSTRAINT [DF_Customers_CreatedAt] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_Customers] PRIMARY KEY ([Id]),
            CONSTRAINT [UQ_Customers_Email] UNIQUE ([Email])
        );
        GO
        CREATE TABLE [sales].[Orders] (
            [Id] BIGINT IDENTITY(1000,1) NOT NULL,
            [CustomerId] INT NOT NULL,
            [Total] DECIMAL(18,2) NOT NULL CONSTRAINT [DF_Orders_Total] DEFAULT (0),
            CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
            CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId])
                REFERENCES [sales].[Customers]([Id]) ON DELETE CASCADE
        );
        GO
        CREATE INDEX [IX_Orders_CustomerId] ON [sales].[Orders]([CustomerId]);
        """;
}
