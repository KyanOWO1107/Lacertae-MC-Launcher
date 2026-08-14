using System.Globalization;
using System.Text.Json;
using Lacertae.Application.Operations;
using Lacertae.Domain.Common;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Lacertae.Infrastructure.Operations;

public sealed class SqliteBackgroundTaskStore(SqliteConnectionFactory factory) : IBackgroundTaskStore
{
    private readonly SqliteConnectionFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<Result<IReadOnlyList<OperationSnapshot>>> GetActiveAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = factory.Create();
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, kind, state, problem_code
                FROM background_tasks
                WHERE state IN ($pending, $running)
                ORDER BY updated_utc ASC, id ASC;
                """;
            command.Parameters.AddWithValue("$pending", (int)OperationState.Pending);
            command.Parameters.AddWithValue("$running", (int)OperationState.Running);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            List<OperationSnapshot> snapshots = [];
            while (await reader.ReadAsync(cancellationToken))
            {
                int stateValue = reader.GetInt32(2);
                if (!Enum.IsDefined((OperationState)stateValue))
                {
                    return Result<IReadOnlyList<OperationSnapshot>>.Failure(
                        Problem("BACKGROUND_TASK_INVALID", reader.GetString(0)));
                }

                snapshots.Add(new OperationSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    (OperationState)stateValue,
                    null,
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            return Result<IReadOnlyList<OperationSnapshot>>.Success(snapshots);
        }
        catch (SqliteException)
        {
            return Result<IReadOnlyList<OperationSnapshot>>.Failure(Problem("BACKGROUND_TASK_UNAVAILABLE", null));
        }
        catch (IOException)
        {
            return Result<IReadOnlyList<OperationSnapshot>>.Failure(Problem("BACKGROUND_TASK_UNAVAILABLE", null));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<OperationSnapshot>>.Failure(Problem("BACKGROUND_TASK_UNAVAILABLE", null));
        }
    }

    public async Task<Result<Unit>> SaveAsync(
        BackgroundTaskRecord record,
        CancellationToken cancellationToken)
    {
        if (!IsValid(record))
        {
            return Result.Failure(Problem("BACKGROUND_TASK_INVALID", record?.Id));
        }

        try
        {
            await using SqliteConnection connection = factory.Create();
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO background_tasks(id, kind, state, frozen_plan_json, journal_json, problem_code, updated_utc)
                VALUES ($id, $kind, $state, $plan, $journal, $problem, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    kind = excluded.kind,
                    state = excluded.state,
                    frozen_plan_json = CASE
                        WHEN excluded.journal_json IS NULL AND background_tasks.journal_json IS NOT NULL
                            THEN background_tasks.frozen_plan_json
                        ELSE excluded.frozen_plan_json
                    END,
                    journal_json = CASE
                        WHEN excluded.journal_json IS NULL THEN background_tasks.journal_json
                        ELSE excluded.journal_json
                    END,
                    problem_code = excluded.problem_code,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$kind", record.Kind);
            command.Parameters.AddWithValue("$state", (int)record.State);
            command.Parameters.AddWithValue("$plan", record.FrozenPlanJson);
            command.Parameters.AddWithValue("$journal", (object?)record.JournalJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$problem", (object?)record.ProblemCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", record.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return Result.Success();
        }
        catch (SqliteException)
        {
            return Result.Failure(Problem("BACKGROUND_TASK_UNAVAILABLE", record.Id));
        }
        catch (IOException)
        {
            return Result.Failure(Problem("BACKGROUND_TASK_UNAVAILABLE", record.Id));
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure(Problem("BACKGROUND_TASK_UNAVAILABLE", record.Id));
        }
    }

    private static bool IsValid(BackgroundTaskRecord? record)
    {
        if (record is null ||
            string.IsNullOrWhiteSpace(record.Id) ||
            string.IsNullOrWhiteSpace(record.Kind) ||
            string.IsNullOrWhiteSpace(record.FrozenPlanJson) ||
            !Enum.IsDefined(record.State))
        {
            return false;
        }

        try
        {
            using JsonDocument frozenPlan = JsonDocument.Parse(record.FrozenPlanJson);
            if (frozenPlan.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (record.JournalJson is not null)
            {
                using JsonDocument journal = JsonDocument.Parse(record.JournalJson);
                if (journal.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Problem Problem(string code, string? correlationId) => new(
        code,
        ProblemStage.Storage,
        code == "BACKGROUND_TASK_INVALID"
            ? "problem.background_task.invalid"
            : "problem.background_task.unavailable",
        code == "BACKGROUND_TASK_UNAVAILABLE",
        string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
        ["action.background_task.retry"]);
}
