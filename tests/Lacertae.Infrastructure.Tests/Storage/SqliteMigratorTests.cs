using Lacertae.Infrastructure.Storage;

namespace Lacertae.Infrastructure.Tests.Storage;

public sealed class SqliteMigratorTests
{
    [Fact]
    public async Task MigrateAsyncCreatesSchemaAndRecordsVersion()
    {
        string directory = CreateDirectory();
        try
        {
            SqliteConnectionFactory factory = new(Path.Combine(directory, "lacertae.db"));
            var result = await new SqliteMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess, result.Problem?.Code);
            await using (var connection = factory.Create())
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations"));
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'accounts'"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsyncAppliesAccountConstraintsAsVersionTwo()
    {
        string directory = CreateDirectory();
        try
        {
            SqliteConnectionFactory factory = new(Path.Combine(directory, "lacertae.db"));
            var result = await new SqliteMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess, result.Problem?.Code);
            await using var connection = factory.Create();
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2L, await ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations"));
            Assert.Equal(
                1L,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_accounts_status'"));
            string accountDefinition = await TextAsync(
                connection,
                "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'accounts'");
            Assert.Contains("CHECK(length(id) = 32)", accountDefinition, StringComparison.Ordinal);
            Assert.Contains("CHECK(account_type IN (0, 1))", accountDefinition, StringComparison.Ordinal);
            Assert.Contains("CHECK(status IN (0, 1, 2))", accountDefinition, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsyncRollsBackVersionTwoWhenExistingAccountViolatesConstraints()
    {
        string directory = CreateDirectory();
        try
        {
            SqliteConnectionFactory factory = new(Path.Combine(directory, "lacertae.db"));
            SqliteMigration legacyMigration = new(1, """
                CREATE TABLE schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );
                CREATE TABLE accounts (
                    id TEXT PRIMARY KEY,
                    provider_id TEXT NOT NULL,
                    profile_uuid TEXT NOT NULL,
                    account_type INTEGER NOT NULL,
                    player_name TEXT NOT NULL,
                    avatar_cache_key TEXT NULL,
                    secret_ref TEXT NULL,
                    status INTEGER NOT NULL,
                    last_successful_login_utc TEXT NULL,
                    UNIQUE (provider_id, profile_uuid)
                );
                """);
            Assert.True((await new SqliteMigrator(factory, [legacyMigration]).MigrateAsync(TestContext.Current.CancellationToken)).IsSuccess);

            await using (var connection = factory.Create())
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO accounts(
                        id, provider_id, profile_uuid, account_type, player_name, avatar_cache_key,
                        secret_ref, status, last_successful_login_utc)
                    VALUES ('too-short', 'offline', 'profile', 0, 'Alex', NULL, NULL, 0, NULL);
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var result = await new SqliteMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal("DATABASE_MIGRATION_FAILED", result.Problem?.Code);
            await using var verify = factory.Create();
            await verify.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1L, await ScalarAsync(verify, "SELECT MAX(version) FROM schema_migrations"));
            Assert.Equal(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM accounts WHERE id = 'too-short'"));
            Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_accounts_status'"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsyncIsIdempotent()
    {
        string directory = CreateDirectory();
        try
        {
            SqliteConnectionFactory factory = new(Path.Combine(directory, "lacertae.db"));
            SqliteMigrator migrator = new(factory);

            Assert.True((await migrator.MigrateAsync(TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await migrator.MigrateAsync(TestContext.Current.CancellationToken)).IsSuccess);

            await using (var connection = factory.Create())
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsyncRollsBackEntireMigrationOnSqlError()
    {
        string directory = CreateDirectory();
        try
        {
            SqliteConnectionFactory factory = new(Path.Combine(directory, "lacertae.db"));
            SqliteMigrator migrator = new(factory, [
                new SqliteMigration(1, "CREATE TABLE should_rollback (id INTEGER); THIS IS INVALID SQL;")
            ]);

            var result = await migrator.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal("DATABASE_MIGRATION_FAILED", result.Problem?.Code);
            await using (var connection = factory.Create())
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'should_rollback'"));
                Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations'"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsyncRejectsDatabaseNewerThanApplication()
    {
        string directory = CreateDirectory();
        try
        {
            SqliteConnectionFactory factory = new(Path.Combine(directory, "lacertae.db"));
            await using (var connection = factory.Create())
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL); INSERT INTO schema_migrations(version, applied_utc) VALUES (99, 'now');";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var result = await new SqliteMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal("DATABASE_SCHEMA_NEWER", result.Problem?.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<long> ScalarAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<string> TextAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lacertae-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
