using System.Globalization;
using Lacertae.Application.GameRoots;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Lacertae.Infrastructure.GameRoots;

public sealed class SqliteGameRootRepository(SqliteConnectionFactory factory) : IGameRootRepository
{
    public async Task<IReadOnlyList<GameRoot>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, normalized_path, display_name, availability, last_scanned_utc FROM game_roots ORDER BY display_name;";
        List<GameRoot> roots = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roots.Add(Read(reader));
        }

        return roots;
    }

    public async Task<GameRoot?> FindByNormalizedPathAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, normalized_path, display_name, availability, last_scanned_utc FROM game_roots WHERE normalized_path = $path LIMIT 1;";
        command.Parameters.AddWithValue("$path", normalizedPath);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<Result<Unit>> UpsertAsync(GameRoot gameRoot, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO game_roots(id, normalized_path, display_name, availability, last_scanned_utc)
            VALUES ($id, $path, $name, $availability, $scanned)
            ON CONFLICT(id) DO UPDATE SET normalized_path = excluded.normalized_path, display_name = excluded.display_name,
                availability = excluded.availability, last_scanned_utc = excluded.last_scanned_utc;
            """;
        command.Parameters.AddWithValue("$id", gameRoot.Id);
        command.Parameters.AddWithValue("$path", gameRoot.NormalizedPath);
        command.Parameters.AddWithValue("$name", gameRoot.DisplayName);
        command.Parameters.AddWithValue("$availability", (int)gameRoot.Availability);
        command.Parameters.AddWithValue("$scanned", gameRoot.LastScannedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<Unit>> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM game_roots WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return Result.Success();
    }

    private static GameRoot Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        (GameRootAvailability)reader.GetInt32(3),
        reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
}
