using System.Security.Cryptography;
using Lacertae.Application.Downloads;
using Lacertae.Application.Install;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Install;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Install;

public sealed class RecoverVanillaInstallsTests
{
    [Fact]
    public async Task RecoveryFinishesWhenStagedArtifactIsPresentAndFinalIsMissing()
    {
        using RecoveryRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/1.21.8.jar", [3, 4, 5]);
        string staged = root.WriteStaged("operation", artifact, [3, 4, 5]);
        VanillaInstallPlan plan = Plan(root.Path, artifact, "operation");
        FakeJournalRepository repository = new(new InstallJournalRecord(
            plan,
            Journal(plan, staged, Applied: false, InstallJournalState.Committing)));

        Result<Unit> result = await new RecoverVanillaInstalls(repository, new FixtureVerifier()).ExecuteAsync(
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal([3, 4, 5], await File.ReadAllBytesAsync(root.FinalPath(artifact), TestContext.Current.CancellationToken));
        Assert.True(repository.Removed);
    }

    [Fact]
    public async Task RecoveryRollsBackAnAppliedMoveWhenOnlyQuarantineEvidenceRemains()
    {
        using RecoveryRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/1.21.8.jar", [3, 4, 5]);
        string quarantine = root.WriteQuarantine("operation", artifact, [0, 0, 0]);
        VanillaInstallPlan plan = Plan(root.Path, artifact, "operation");
        FakeJournalRepository repository = new(new InstallJournalRecord(
            plan,
            Journal(plan, quarantine, Applied: true, InstallJournalState.RollbackRequired)));

        Result<Unit> result = await new RecoverVanillaInstalls(repository, new FixtureVerifier()).ExecuteAsync(
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal([0, 0, 0], await File.ReadAllBytesAsync(root.FinalPath(artifact), TestContext.Current.CancellationToken));
        Assert.True(repository.Removed);
    }

    [Fact]
    public async Task RecoveryPreservesAmbiguousEvidenceAndReturnsConflict()
    {
        using RecoveryRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/1.21.8.jar", [3, 4, 5]);
        string final = root.WriteFinal(artifact, [9, 9, 9]);
        string staged = root.WriteStaged("operation", artifact, [3, 4, 5]);
        VanillaInstallPlan plan = Plan(root.Path, artifact, "operation");
        FakeJournalRepository repository = new(new InstallJournalRecord(
            plan,
            Journal(plan, staged, Applied: false, InstallJournalState.Committing)));

        Result<Unit> result = await new RecoverVanillaInstalls(repository, new FixtureVerifier()).ExecuteAsync(
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("INSTALL_RECOVERY_CONFLICT", result.Problem?.Code);
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(final, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(staged));
        Assert.False(repository.Removed);
    }

    private static InstallJournal Journal(
        VanillaInstallPlan plan,
        string evidencePath,
        bool Applied,
        InstallJournalState state)
    {
        string relative = Path.GetRelativePath(plan.GameRootPath, evidencePath).Replace(Path.DirectorySeparatorChar, '/');
        string staged = relative.Contains("/staging/", StringComparison.Ordinal)
            ? relative
            : $".lacertae/staging/{plan.OperationId}/{plan.Artifacts[0].RelativeDestinationPath}";
        string? quarantine = relative.Contains("/quarantine/", StringComparison.Ordinal) ? relative :
            Applied ? $".lacertae/quarantine/{plan.OperationId}/{plan.Artifacts[0].RelativeDestinationPath}" : null;
        return new InstallJournal(
            plan.OperationId,
            plan.GameRootId,
            plan.VersionId,
            state,
            [new InstallMove(staged, plan.Artifacts[0].RelativeDestinationPath, quarantine, Applied)],
            DateTimeOffset.UtcNow);
    }

    private static VanillaInstallPlan Plan(string root, DownloadArtifact artifact, string operationId) =>
        new(
            operationId,
            InstallAction.Repair,
            "root-test",
            root,
            "1.21.8",
            Path.Combine(root, "versions", "1.21.8"),
            artifact.ExpectedSize,
            artifact.ExpectedSize,
            [artifact],
            DateTimeOffset.UtcNow);

    private static DownloadArtifact Artifact(string path, byte[] content) =>
        DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            new Uri("https://official.example.test/" + path.Replace('/', '_')),
            path,
            content.Length,
            [new ArtifactHash("sha256", Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant())]);

    private sealed class RecoveryRoot : IDisposable
    {
        public RecoveryRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lacertae-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string FinalPath(DownloadArtifact artifact) =>
            System.IO.Path.Combine(Path, artifact.RelativeDestinationPath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string WriteFinal(DownloadArtifact artifact, byte[] content)
        {
            string path = FinalPath(artifact);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        public string WriteStaged(string operationId, DownloadArtifact artifact, byte[] content) =>
            WriteEvidence(System.IO.Path.Combine(Path, ".lacertae", "staging", operationId, artifact.RelativeDestinationPath.Replace('/', System.IO.Path.DirectorySeparatorChar)), content);

        public string WriteQuarantine(string operationId, DownloadArtifact artifact, byte[] content) =>
            WriteEvidence(System.IO.Path.Combine(Path, ".lacertae", "quarantine", operationId, artifact.RelativeDestinationPath.Replace('/', System.IO.Path.DirectorySeparatorChar)), content);

        private static string WriteEvidence(string path, byte[] content)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    private sealed class FakeJournalRepository(InstallJournalRecord record) : IInstallJournalRepository
    {
        private readonly List<InstallJournalRecord> records = [record];
        public bool Removed { get; private set; }

        public Task<Result<Unit>> SaveAsync(VanillaInstallPlan plan, InstallJournal journal, CancellationToken cancellationToken)
        {
            records.Clear();
            records.Add(new InstallJournalRecord(plan, journal));
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<InstallJournalRecord>>> GetRecoverableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<InstallJournalRecord>>.Success(records));

        public Task<Result<Unit>> RemoveAsync(string operationId, CancellationToken cancellationToken)
        {
            Removed = true;
            records.Clear();
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FixtureVerifier : IGameFileVerifier
    {
        public async Task<Result<bool>> VerifyAsync(DownloadArtifact artifact, string filePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length != artifact.ExpectedSize)
            {
                return Result<bool>.Success(false);
            }

            byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            return Result<bool>.Success(Convert.ToHexString(SHA256.HashData(bytes)).Equals(
                artifact.Hashes[0].NormalizedHexDigest,
                StringComparison.OrdinalIgnoreCase));
        }
    }
}
