using System.Text.Json;
using Lacertae.Application.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;
using Lacertae.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Lacertae.Infrastructure.Versions;

public sealed class SqliteVersionOverrideRepository(SqliteConnectionFactory factory) : IVersionOverrideRepository
{
    private static readonly JsonSerializerOptions ArgumentSerializerOptions = new()
    {
        WriteIndented = false,
    };

    public async Task<IReadOnlyList<VersionOverride>> GetForGameRootAsync(
        string gameRootId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootId);
        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_root_id, version_folder, display_name, isolation_override, account_id, java_path,
                   minimum_memory_mb, maximum_memory_mb, gc_profile, jvm_arguments_json, game_arguments_json
            FROM version_overrides
            WHERE game_root_id = $gameRootId
            ORDER BY version_folder;
            """;
        command.Parameters.AddWithValue("$gameRootId", gameRootId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<VersionOverride> overrides = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            overrides.Add(Read(reader));
        }

        return overrides;
    }

    public async Task<Result<Unit>> UpsertAsync(
        VersionOverride versionOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versionOverride);
        if (!TrySerializeArguments(versionOverride.JvmArguments, out string jvmArguments) ||
            !TrySerializeArguments(versionOverride.GameArguments, out string gameArguments))
        {
            return Result.Failure(InvalidProblem());
        }

        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO version_overrides(
                game_root_id, version_folder, display_name, isolation_override, account_id, java_path,
                minimum_memory_mb, maximum_memory_mb, gc_profile, jvm_arguments_json, game_arguments_json)
            VALUES ($gameRootId, $versionFolder, $displayName, $isolation, $accountId, $javaPath,
                    $minimumMemoryMb, $maximumMemoryMb, $gcProfile, $jvmArguments, $gameArguments)
            ON CONFLICT(game_root_id, version_folder) DO UPDATE SET
                display_name = excluded.display_name,
                isolation_override = excluded.isolation_override,
                account_id = excluded.account_id,
                java_path = excluded.java_path,
                minimum_memory_mb = excluded.minimum_memory_mb,
                maximum_memory_mb = excluded.maximum_memory_mb,
                gc_profile = excluded.gc_profile,
                jvm_arguments_json = excluded.jvm_arguments_json,
                game_arguments_json = excluded.game_arguments_json;
            """;
        command.Parameters.AddWithValue("$gameRootId", versionOverride.GameRootId);
        command.Parameters.AddWithValue("$versionFolder", versionOverride.VersionFolder);
        command.Parameters.AddWithValue("$displayName", (object?)versionOverride.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$isolation", (int)versionOverride.Isolation);
        command.Parameters.AddWithValue("$accountId", (object?)versionOverride.AccountId ?? DBNull.Value);
        command.Parameters.AddWithValue("$javaPath", (object?)versionOverride.JavaPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$minimumMemoryMb", (object?)versionOverride.MinimumMemoryMb ?? DBNull.Value);
        command.Parameters.AddWithValue("$maximumMemoryMb", (object?)versionOverride.MaximumMemoryMb ?? DBNull.Value);
        command.Parameters.AddWithValue("$gcProfile", versionOverride.GcProfile is null ? DBNull.Value : (object)(int)versionOverride.GcProfile.Value);
        command.Parameters.AddWithValue("$jvmArguments", jvmArguments);
        command.Parameters.AddWithValue("$gameArguments", gameArguments);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return Result.Success();
        }
        catch (SqliteException)
        {
            return Result.Failure(InvalidProblem());
        }
    }

    public async Task<Result<Unit>> RemoveAsync(
        string gameRootId,
        string versionFolder,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionFolder);
        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM version_overrides WHERE game_root_id = $gameRootId AND version_folder = $versionFolder;";
        command.Parameters.AddWithValue("$gameRootId", gameRootId);
        command.Parameters.AddWithValue("$versionFolder", versionFolder);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return Result.Success();
        }
        catch (SqliteException)
        {
            return Result.Failure(InvalidProblem());
        }
    }

    public async Task<Result<Unit>> RenameAsync(
        string gameRootId,
        string sourceFolder,
        string targetFolder,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE version_overrides
            SET version_folder = $targetFolder
            WHERE game_root_id = $gameRootId AND version_folder = $sourceFolder;
            """;
        command.Parameters.AddWithValue("$gameRootId", gameRootId);
        command.Parameters.AddWithValue("$sourceFolder", sourceFolder);
        command.Parameters.AddWithValue("$targetFolder", targetFolder);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (SqliteException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result.Failure(RenameProblem());
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static VersionOverride Read(SqliteDataReader reader)
    {
        int isolation = reader.GetInt32(3);
        GcProfile? gcProfile = reader.IsDBNull(8) ? null : (GcProfile)reader.GetInt32(8);
        return new VersionOverride(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            (IsolationOverride)isolation,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            gcProfile,
            DeserializeArguments(reader.GetString(9)),
            DeserializeArguments(reader.GetString(10)));
    }

    private static string[] DeserializeArguments(string json)
    {
        string[]? arguments = JsonSerializer.Deserialize<string[]>(json, ArgumentSerializerOptions);
        if (arguments is null || arguments.Any(static argument => string.IsNullOrWhiteSpace(argument) || argument.Contains('\0')))
        {
            throw new InvalidDataException("Version argument JSON is invalid.");
        }

        return arguments;
    }

    private static bool TrySerializeArguments(IReadOnlyList<string> arguments, out string json)
    {
        json = string.Empty;
        if (arguments is null || arguments.Any(static argument => string.IsNullOrWhiteSpace(argument) || argument.Contains('\0')))
        {
            return false;
        }

        json = JsonSerializer.Serialize(arguments, ArgumentSerializerOptions);
        return true;
    }

    private static Problem InvalidProblem() => new(
        "VERSION_OVERRIDE_INVALID",
        ProblemStage.Storage,
        "problem.version.override_invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.version.review_settings"]);

    private static Problem RenameProblem() => new(
        "VERSION_OVERRIDE_RENAME_FAILED",
        ProblemStage.Storage,
        "problem.version.override_rename_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.version.review_rename"]);
}
