using System.Globalization;
using System.Reflection;
using Lacertae.Application.Storage;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Microsoft.Data.Sqlite;

namespace Lacertae.Infrastructure.Storage;

public sealed record SqliteMigration(int Version, string Sql);

public sealed class SqliteMigrator : IDatabaseMigrator
{
    private static readonly SqliteMigration[] DefaultMigrations = LoadEmbeddedMigrations();

    private readonly SqliteConnectionFactory factory;
    private readonly IReadOnlyList<SqliteMigration> migrations;

    public SqliteMigrator(SqliteConnectionFactory factory, IReadOnlyList<SqliteMigration>? migrations = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.migrations = migrations ?? DefaultMigrations;
        if (this.migrations.Count == 0 || this.migrations.Any(static migration => migration.Version < 1))
        {
            throw new ArgumentException("At least one positive migration is required.", nameof(migrations));
        }
    }

    public async Task<Result<Unit>> MigrateAsync(CancellationToken cancellationToken)
    {
        string? databasePath = factory.DatabasePath;
        if (File.Exists(databasePath))
        {
            string backupPath = databasePath + ".pre-migration-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".bak";
            File.Copy(databasePath, backupPath, overwrite: false);
        }

        await using SqliteConnection connection = factory.Create();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);

            int currentVersion = await ReadCurrentVersionAsync(connection, cancellationToken);
            int latestVersion = migrations.Max(static migration => migration.Version);
            if (currentVersion > latestVersion)
            {
                return Result.Failure(Problem("DATABASE_SCHEMA_NEWER"));
            }

            foreach (SqliteMigration migration in migrations.OrderBy(static migration => migration.Version))
            {
                if (migration.Version <= currentVersion)
                {
                    continue;
                }

                await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    await using SqliteCommand command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = migration.Sql;
                    await command.ExecuteNonQueryAsync(cancellationToken);

                    await using SqliteCommand versionCommand = connection.CreateCommand();
                    versionCommand.Transaction = transaction;
                    versionCommand.CommandText = "INSERT INTO schema_migrations(version, applied_utc) VALUES ($version, $appliedUtc);";
                    versionCommand.Parameters.AddWithValue("$version", migration.Version);
                    versionCommand.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    await versionCommand.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }

            return Result.Success();
        }
        catch (SqliteException)
        {
            return Result.Failure(Problem("DATABASE_MIGRATION_FAILED"));
        }
        catch (IOException)
        {
            return Result.Failure(Problem("DATABASE_UNAVAILABLE"));
        }
    }

    private static async Task<int> ReadCurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';";
        long tableExists = (long)(await exists.ExecuteScalarAsync(cancellationToken))!;
        if (tableExists == 0)
        {
            return 0;
        }

        await using SqliteCommand current = connection.CreateCommand();
        current.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(await current.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.Storage,
        "problem.database.migration_failed",
        code == "DATABASE_UNAVAILABLE",
        Guid.NewGuid().ToString("N"),
        ["action.database.retry"]);

    private static SqliteMigration[] LoadEmbeddedMigrations()
    {
        Assembly assembly = typeof(SqliteMigrator).Assembly;
        List<SqliteMigration> migrations = [];
        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!TryGetMigrationVersion(resourceName, out int version))
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration resource '{resourceName}' is missing.");
            using StreamReader reader = new(stream);
            migrations.Add(new SqliteMigration(version, reader.ReadToEnd()));
        }

        if (migrations.Count == 0)
        {
            throw new InvalidOperationException("No embedded database migrations were found.");
        }

        if (migrations.GroupBy(static migration => migration.Version).Any(static group => group.Count() > 1))
        {
            throw new InvalidOperationException("Embedded database migration versions must be unique.");
        }

        return migrations.OrderBy(static migration => migration.Version).ToArray();
    }

    private static bool TryGetMigrationVersion(string resourceName, out int version)
    {
        const string marker = ".Storage.Migrations.";
        version = 0;
        int markerStart = resourceName.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerStart < 0 || !resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int fileNameStart = markerStart + marker.Length;
        int sqlSuffixStart = resourceName.Length - ".sql".Length;
        string fileName = resourceName[fileNameStart..sqlSuffixStart];
        int separator = fileName.IndexOf('_');
        return separator > 0
            && int.TryParse(
                fileName.AsSpan(0, separator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out version)
            && version > 0;
    }
}
