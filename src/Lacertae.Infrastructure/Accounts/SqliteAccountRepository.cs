using System.Globalization;
using Lacertae.Application.Accounts;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Lacertae.Infrastructure.Accounts;

public sealed class SqliteAccountRepository(SqliteConnectionFactory factory) : IAccountRepository
{
    public async Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, provider_id, profile_uuid, account_type, player_name, avatar_cache_key,
                   secret_ref, status, last_successful_login_utc
            FROM accounts
            ORDER BY player_name, id;
            """;
        List<Account> accounts = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            accounts.Add(Read(reader));
        }

        return accounts;
    }

    public async Task<Account?> GetAsync(string accountId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return null;
        }

        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, provider_id, profile_uuid, account_type, player_name, avatar_cache_key,
                   secret_ref, status, last_successful_login_utc
            FROM accounts
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", accountId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<Account?> FindByIdentityAsync(
        AccountIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, provider_id, profile_uuid, account_type, player_name, avatar_cache_key,
                   secret_ref, status, last_successful_login_utc
            FROM accounts
            WHERE provider_id = $provider AND profile_uuid = $profile
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$provider", identity.ProviderId);
        command.Parameters.AddWithValue("$profile", identity.ProfileUuid);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<Result<Unit>> UpsertAsync(Account account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts(
                id, provider_id, profile_uuid, account_type, player_name, avatar_cache_key,
                secret_ref, status, last_successful_login_utc)
            VALUES ($id, $provider, $profile, $type, $name, $avatar, $secret, $status, $lastLogin)
            ON CONFLICT(id) DO UPDATE SET
                provider_id = excluded.provider_id,
                profile_uuid = excluded.profile_uuid,
                account_type = excluded.account_type,
                player_name = excluded.player_name,
                avatar_cache_key = excluded.avatar_cache_key,
                secret_ref = excluded.secret_ref,
                status = excluded.status,
                last_successful_login_utc = excluded.last_successful_login_utc;
            """;
        AddParameters(command, account);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<Unit>> SetStatusAsync(
        string accountId,
        AccountStatus status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId) || !Enum.IsDefined(status))
        {
            return Result<Unit>.Failure(Problem("ACCOUNT_STATUS_INVALID"));
        }

        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE accounts SET status = $status WHERE id = $id;";
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$id", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<Unit>> DeleteAndClearVersionReferencesAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Result<Unit>.Failure(Problem("ACCOUNT_ID_INVALID"));
        }

        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (SqliteCommand clearReferences = connection.CreateCommand())
        {
            clearReferences.Transaction = transaction;
            clearReferences.CommandText = "UPDATE version_overrides SET account_id = NULL WHERE account_id = $id;";
            clearReferences.Parameters.AddWithValue("$id", accountId);
            await clearReferences.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM accounts WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", accountId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    private static void AddParameters(SqliteCommand command, Account account)
    {
        command.Parameters.AddWithValue("$id", account.Id);
        command.Parameters.AddWithValue("$provider", account.Identity.ProviderId);
        command.Parameters.AddWithValue("$profile", account.Identity.ProfileUuid);
        command.Parameters.AddWithValue("$type", (int)account.Type);
        command.Parameters.AddWithValue("$name", account.PlayerName);
        command.Parameters.AddWithValue("$avatar", account.AvatarCacheKey ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$secret", account.SecretRef ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)account.Status);
        command.Parameters.AddWithValue(
            "$lastLogin",
            account.LastSuccessfulLoginUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
    }

    private static Account Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        new AccountIdentity(reader.GetString(1), reader.GetString(2)),
        (AccountType)reader.GetInt32(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        (AccountStatus)reader.GetInt32(7),
        reader.IsDBNull(8)
            ? null
            : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture));

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.Authentication,
        "problem.auth.repository_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.auth.review"]);
}
