using System.Security.Cryptography;
using Lacertae.Application.Install;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Install;
using Lacertae.Infrastructure.Install;
using Lacertae.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Lacertae.Infrastructure.Tests.Install;

public sealed class SqliteInstallJournalRepositoryTests
{
    [Fact]
    public async Task SaveLoadAndRemoveRoundTripStrictPlanAndJournal()
    {
        using TestRoot root = new();
        SqliteConnectionFactory factory = new(Path.Combine(root.Path, "launcher.db"));
        Assert.True((await new SqliteMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken)).IsSuccess);
        SqliteInstallJournalRepository repository = new(factory);
        VanillaInstallPlan plan = Plan(root.Path);
        InstallJournal journal = new(
            plan.OperationId,
            plan.GameRootId,
            plan.VersionId,
            InstallJournalState.Staging,
            [new InstallMove(".lacertae/staging/op/file.bin", "file.bin", null, false)],
            DateTimeOffset.UtcNow);

        Assert.True((await repository.SaveAsync(plan, journal, TestContext.Current.CancellationToken)).IsSuccess);
        IReadOnlyList<InstallJournalRecord> records = (await repository.GetRecoverableAsync(TestContext.Current.CancellationToken)).Value;
        InstallJournalRecord actual = Assert.Single(records);
        Assert.Equal(plan.OperationId, actual.Plan.OperationId);
        Assert.Equal(plan.GameRootPath, actual.Plan.GameRootPath);
        Assert.Equal(plan.Artifacts[0].ArtifactId, actual.Plan.Artifacts[0].ArtifactId);
        Assert.Equal(plan.Artifacts[0].OfficialUri, actual.Plan.Artifacts[0].OfficialUri);
        Assert.Equal(plan.Artifacts[0].Hashes[0], actual.Plan.Artifacts[0].Hashes[0]);
        Assert.Equal(journal.State, actual.Journal.State);
        Assert.Equal(journal.Moves[0], actual.Journal.Moves[0]);

        await using (SqliteConnection connection = factory.Create())
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT frozen_plan_json, journal_json FROM background_tasks WHERE id = $id;";
            command.Parameters.AddWithValue("$id", plan.OperationId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.DoesNotContain("?token=", reader.GetString(0), StringComparison.Ordinal);
            Assert.DoesNotContain("?token=", reader.GetString(1), StringComparison.Ordinal);
        }

        Assert.True((await repository.RemoveAsync(plan.OperationId, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Empty((await repository.GetRecoverableAsync(TestContext.Current.CancellationToken)).Value);
    }

    private static VanillaInstallPlan Plan(string root)
    {
        byte[] content = [1, 2, 3];
        DownloadArtifact artifact = DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            new Uri("https://official.example.test/file.bin"),
            "file.bin",
            content.Length,
            [new ArtifactHash("sha256", Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant())]);
        return new VanillaInstallPlan(
            "op",
            InstallAction.Install,
            "root",
            root,
            "1.21.8",
            Path.Combine(root, "versions", "1.21.8"),
            artifact.ExpectedSize,
            artifact.ExpectedSize,
            [artifact],
            DateTimeOffset.UtcNow);
    }

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lacertae-journal-" + Guid.NewGuid().ToString("N"));
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
