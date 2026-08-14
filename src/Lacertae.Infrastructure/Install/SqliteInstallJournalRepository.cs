using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lacertae.Application.Install;
using Lacertae.Domain.Common;
using Lacertae.Domain.Install;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Lacertae.Infrastructure.Install;

public sealed class SqliteInstallJournalRepository(SqliteConnectionFactory factory) : IInstallJournalRepository
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<Result<Unit>> SaveAsync(
        VanillaInstallPlan plan,
        InstallJournal journal,
        CancellationToken cancellationToken)
    {
        if (!IsValid(plan, journal))
        {
            return Result.Failure(Problem("INSTALL_JOURNAL_INVALID"));
        }

        try
        {
            string frozenPlan = JsonSerializer.Serialize(new PlanDocument(SchemaVersion, plan), JsonOptions);
            string journalJson = JsonSerializer.Serialize(new JournalDocument(SchemaVersion, journal), JsonOptions);
            await using SqliteConnection connection = factory.Create();
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO background_tasks(id, kind, state, frozen_plan_json, journal_json, problem_code, updated_utc)
                VALUES ($id, $kind, $state, $plan, $journal, NULL, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    kind = excluded.kind,
                    state = excluded.state,
                    frozen_plan_json = excluded.frozen_plan_json,
                    journal_json = excluded.journal_json,
                    problem_code = NULL,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$id", journal.OperationId);
            command.Parameters.AddWithValue("$kind", "vanilla-install");
            command.Parameters.AddWithValue("$state", (int)MapOperationState(journal.State));
            command.Parameters.AddWithValue("$plan", frozenPlan);
            command.Parameters.AddWithValue("$journal", journalJson);
            command.Parameters.AddWithValue("$updated", journal.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return Result.Success();
        }
        catch (JsonException)
        {
            return Result.Failure(Problem("INSTALL_JOURNAL_INVALID"));
        }
        catch (SqliteException)
        {
            return Result.Failure(Problem("INSTALL_JOURNAL_UNAVAILABLE", retryable: true));
        }
        catch (IOException)
        {
            return Result.Failure(Problem("INSTALL_JOURNAL_UNAVAILABLE", retryable: true));
        }
    }

    public async Task<Result<IReadOnlyList<InstallJournalRecord>>> GetRecoverableAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = factory.Create();
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT frozen_plan_json, journal_json
                FROM background_tasks
                WHERE journal_json IS NOT NULL
                ORDER BY updated_utc, id;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            List<InstallJournalRecord> records = [];
            while (await reader.ReadAsync(cancellationToken))
            {
                PlanDocument? plan = JsonSerializer.Deserialize<PlanDocument>(reader.GetString(0), JsonOptions);
                JournalDocument? journal = JsonSerializer.Deserialize<JournalDocument>(reader.GetString(1), JsonOptions);
                if (plan is null || journal is null || plan.SchemaVersion != SchemaVersion || journal.SchemaVersion != SchemaVersion ||
                    !IsValid(plan.Plan, journal.Journal) || journal.Journal.State == InstallJournalState.Completed)
                {
                    return Result<IReadOnlyList<InstallJournalRecord>>.Failure(Problem("INSTALL_JOURNAL_INVALID"));
                }

                records.Add(new InstallJournalRecord(plan.Plan, journal.Journal));
            }

            return Result<IReadOnlyList<InstallJournalRecord>>.Success(records);
        }
        catch (JsonException)
        {
            return Result<IReadOnlyList<InstallJournalRecord>>.Failure(Problem("INSTALL_JOURNAL_INVALID"));
        }
        catch (SqliteException)
        {
            return Result<IReadOnlyList<InstallJournalRecord>>.Failure(Problem("INSTALL_JOURNAL_UNAVAILABLE", retryable: true));
        }
        catch (IOException)
        {
            return Result<IReadOnlyList<InstallJournalRecord>>.Failure(Problem("INSTALL_JOURNAL_UNAVAILABLE", retryable: true));
        }
    }

    public async Task<Result<Unit>> RemoveAsync(string operationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return Result.Failure(Problem("INSTALL_JOURNAL_INVALID"));
        }

        try
        {
            await using SqliteConnection connection = factory.Create();
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM background_tasks WHERE id = $id;";
            command.Parameters.AddWithValue("$id", operationId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return Result.Success();
        }
        catch (SqliteException)
        {
            return Result.Failure(Problem("INSTALL_JOURNAL_UNAVAILABLE", retryable: true));
        }
    }

    private static bool IsValid(VanillaInstallPlan? plan, InstallJournal? journal) =>
        plan is not null && journal is not null &&
        !string.IsNullOrWhiteSpace(plan.OperationId) &&
        string.Equals(plan.OperationId, journal.OperationId, StringComparison.Ordinal) &&
        string.Equals(plan.GameRootId, journal.GameRootId, StringComparison.Ordinal) &&
        string.Equals(plan.VersionId, journal.VersionId, StringComparison.Ordinal) &&
        journal.Moves is not null;

    private static OperationState MapOperationState(InstallJournalState state) => state switch
    {
        InstallJournalState.Completed => OperationState.Succeeded,
        InstallJournalState.RollbackRequired => OperationState.Failed,
        _ => OperationState.Running,
    };

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static Problem Problem(string code, bool retryable = false) => new(
        code,
        ProblemStage.Storage,
        code == "INSTALL_JOURNAL_INVALID"
            ? "problem.install.journal_invalid"
            : "problem.install.journal_unavailable",
        retryable,
        Guid.NewGuid().ToString("N"),
        ["action.install.review_recovery"]);

    private sealed record PlanDocument(int SchemaVersion, VanillaInstallPlan Plan);

    private sealed record JournalDocument(int SchemaVersion, InstallJournal Journal);
}
