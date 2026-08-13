using Lacertae.Application.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Tests.Versions;

public sealed class RenameVersionFolderTests
{
    [Fact]
    public async Task PrepareRejectsInvalidNamesMissingSourceAndExistingTarget()
    {
        string root = CreateRoot();
        try
        {
            CreateVersion(root, "source");
            Directory.CreateDirectory(Path.Combine(root, "versions", "target"));
            Assert.Equal("VERSION_RENAME_INVALID_NAME", (await RenameVersionFolder.PrepareAsync("root", root, "source", "../bad", false, CancellationToken.None)).Problem?.Code);
            Assert.Equal("VERSION_RENAME_SOURCE_MISSING", (await RenameVersionFolder.PrepareAsync("root", root, "missing", "new", false, CancellationToken.None)).Problem?.Code);
            Assert.Equal("VERSION_RENAME_TARGET_EXISTS", (await RenameVersionFolder.PrepareAsync("root", root, "source", "target", false, CancellationToken.None)).Problem?.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareRejectsActiveTaskAndJsonIdMismatch()
    {
        string root = CreateRoot();
        try
        {
            CreateVersion(root, "source", jsonId: "other-id");
            Assert.Equal("VERSION_RENAME_ACTIVE_TASK", (await RenameVersionFolder.PrepareAsync("root", root, "source", "new", true, CancellationToken.None)).Problem?.Code);
            Assert.Equal("VERSION_RENAME_JSON_BASENAME_MISMATCH", (await RenameVersionFolder.PrepareAsync("root", root, "source", "new", false, CancellationToken.None)).Problem?.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareRejectsMismatchedJarAndExternalInheritanceReference()
    {
        string root = CreateRoot();
        try
        {
            CreateVersion(root, "source", includeJar: true);
            File.WriteAllBytes(Path.Combine(root, "versions", "source", "different.jar"), [1]);
            Assert.Equal(
                "VERSION_RENAME_JAR_BASENAME_MISMATCH",
                (await RenameVersionFolder.PrepareAsync("root", root, "source", "new", false, CancellationToken.None)).Problem?.Code);

            File.Delete(Path.Combine(root, "versions", "source", "different.jar"));
            string child = Path.Combine(root, "versions", "child");
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "child.json"), "{\"id\":\"child\",\"inheritsFrom\":\"source\"}");
            var result = await RenameVersionFolder.PrepareAsync("root", root, "source", "new", false, CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal("VERSION_RENAME_REFERENCED", result.Problem?.Code);
            Assert.Equal("child", result.Problem?.SafeContext["referringFolders"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteMovesDirectoryRenamesFilesAndMigratesOverride()
    {
        string root = CreateRoot();
        try
        {
            CreateVersion(root, "source", includeJar: true, includeIsolatedData: true);
            FakeOverrideRepository overrides = new([
                new VersionOverride("root", "source", "Source", IsolationOverride.Inherit, null, null, null, null, null, [], [])]);
            FakeJournal journal = new();
            var result = await new RenameVersionFolder(overrides, journal).ExecuteAsync(
                "root", root, "source", "new-name", false, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Problem?.Code);
            Assert.False(Directory.Exists(Path.Combine(root, "versions", "source")));
            string target = Path.Combine(root, "versions", "new-name");
            Assert.True(File.Exists(Path.Combine(target, "new-name.json")));
            Assert.True(File.Exists(Path.Combine(target, "new-name.jar")));
            Assert.True(Directory.Exists(Path.Combine(target, "mods")));
            Assert.Equal(VersionRenameJournalState.Completed, journal.Entries[^1].State);
            Assert.Equal("new-name", Assert.Single(overrides.Stored).VersionFolder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoverRollsBackWhenDatabaseUpdateFailedAfterDirectoryMove()
    {
        string root = CreateRoot();
        try
        {
            CreateVersion(root, "source", includeJar: true);
            FakeOverrideRepository overrides = new([
                new VersionOverride("root", "source", "Source", IsolationOverride.Inherit, null, null, null, null, null, [], [])])
            {
                FailRename = true,
            };
            FakeJournal journal = new();
            RenameVersionFolder useCase = new(overrides, journal);

            var result = await useCase.ExecuteAsync("root", root, "source", "new-name", false, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("VERSION_OVERRIDE_RENAME_FAILED", result.Problem?.Code);
            Assert.False(Directory.Exists(Path.Combine(root, "versions", "source")));
            Assert.True(Directory.Exists(Path.Combine(root, "versions", "new-name")));
            Assert.Equal(VersionRenameJournalState.RollbackRequired, journal.Entries[^1].State);

            overrides.FailRename = false;
            var recovered = await useCase.RecoverAsync(CancellationToken.None);

            Assert.True(recovered.IsSuccess, recovered.Problem?.Code);
            Assert.True(Directory.Exists(Path.Combine(root, "versions", "source")));
            Assert.False(Directory.Exists(Path.Combine(root, "versions", "new-name")));
            Assert.Equal("source", Assert.Single(overrides.Stored).VersionFolder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "lacertae-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "versions"));
        return root;
    }

    private static void CreateVersion(string root, string folder, string? jsonId = null, bool includeJar = false, bool includeIsolatedData = false)
    {
        string path = Path.Combine(root, "versions", folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, folder + ".json"), $"{{\"id\":\"{jsonId ?? folder}\",\"type\":\"release\"}}");
        if (includeJar)
        {
            File.WriteAllBytes(Path.Combine(path, folder + ".jar"), [1, 2, 3]);
        }

        if (includeIsolatedData)
        {
            Directory.CreateDirectory(Path.Combine(path, "mods"));
            File.WriteAllText(Path.Combine(path, "mods", "example.jar"), "fixture");
        }
    }

    private sealed class FakeJournal : IVersionRenameJournal
    {
        public List<VersionRenameJournalEntry> Entries { get; } = [];

        public Task<Result<Unit>> WriteAsync(VersionRenameJournalEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<VersionRenameJournalEntry?>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<VersionRenameJournalEntry?>.Success(Entries.Count == 0 ? null : Entries[^1]));

        public Task<Result<Unit>> DeleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeOverrideRepository(IReadOnlyList<VersionOverride>? initial = null) : IVersionOverrideRepository
    {
        public List<VersionOverride> Stored { get; } = initial?.ToList() ?? [];
        public bool FailRename { get; set; }

        public Task<IReadOnlyList<VersionOverride>> GetForGameRootAsync(string gameRootId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VersionOverride>>(Stored.ToArray());

        public Task<Result<Unit>> UpsertAsync(VersionOverride versionOverride, CancellationToken cancellationToken)
        {
            Stored.RemoveAll(value => value.GameRootId == versionOverride.GameRootId && value.VersionFolder == versionOverride.VersionFolder);
            Stored.Add(versionOverride);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<Unit>> RemoveAsync(string gameRootId, string versionFolder, CancellationToken cancellationToken)
        {
            Stored.RemoveAll(value => value.GameRootId == gameRootId && value.VersionFolder == versionFolder);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<Unit>> RenameAsync(string gameRootId, string sourceFolder, string targetFolder, CancellationToken cancellationToken)
        {
            if (FailRename)
            {
                return Task.FromResult(Result.Failure(new Lacertae.Domain.Problems.Problem(
                    "VERSION_OVERRIDE_RENAME_FAILED",
                    Lacertae.Domain.Problems.ProblemStage.Storage,
                    "problem.version.override_rename_failed",
                    false,
                    "test",
                    [])));
            }

            int index = Stored.FindIndex(value => value.GameRootId == gameRootId && value.VersionFolder == sourceFolder);
            if (index >= 0)
            {
                Stored[index] = Stored[index] with { VersionFolder = targetFolder };
            }

            return Task.FromResult(Result.Success());
        }
    }
}
