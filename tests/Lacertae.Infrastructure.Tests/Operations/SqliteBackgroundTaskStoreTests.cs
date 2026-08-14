using Lacertae.Domain.Operations;
using Lacertae.Infrastructure.Operations;
using Lacertae.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Lacertae.Infrastructure.Tests.Operations;

public sealed class SqliteBackgroundTaskStoreTests
{
    [Fact]
    public async Task FinalStateDoesNotEraseAnInstallJournal()
    {
        using TestRoot root = new();
        SqliteConnectionFactory factory = new(Path.Combine(root.Path, "launcher.db"));
        Assert.True((await new SqliteMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken)).IsSuccess);
        SqliteBackgroundTaskStore store = new(factory);

        Assert.True((await store.SaveAsync(
            Record("{\"operationId\":\"op\"}", null, OperationState.Running),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.SaveAsync(
            Record("{\"schemaVersion\":1,\"plan\":{}}", "{\"schemaVersion\":1,\"journal\":{}}", OperationState.Running),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.SaveAsync(
            Record("{\"operationId\":\"op\"}", null, OperationState.Failed),
            TestContext.Current.CancellationToken)).IsSuccess);

        await using SqliteConnection connection = factory.Create();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT frozen_plan_json, journal_json, state FROM background_tasks WHERE id = 'op';";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Contains("schemaVersion", reader.GetString(0), StringComparison.Ordinal);
        Assert.NotNull(reader.GetString(1));
        Assert.Equal((int)OperationState.Failed, reader.GetInt32(2));
    }

    private static BackgroundTaskRecord Record(
        string frozenPlanJson,
        string? journalJson,
        OperationState state) => new(
            "op",
            "vanilla-install",
            state,
            frozenPlanJson,
            journalJson,
            state == OperationState.Failed ? "DOWNLOAD_UNAVAILABLE" : null,
            DateTimeOffset.UtcNow);

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lacertae-task-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
