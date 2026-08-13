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
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations"));
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'accounts'"));
            }
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
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations"));
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

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lacertae-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
