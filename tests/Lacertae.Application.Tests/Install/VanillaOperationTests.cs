using System.Security.Cryptography;
using Lacertae.Application.Downloads;
using Lacertae.Application.Install;
using Lacertae.Application.Operations;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Install;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Install;

public sealed class VanillaOperationTests
{
    [Fact]
    public async Task InstallOperationReportsDeterministicStagesAndMonotonicProgress()
    {
        using TestRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/1.21.8.jar", [1, 2, 3]);
        FakeTaskStore store = new();
        InstallVanillaOperation operation = CreateOperation(root, artifact, InstallAction.Install, store);
        List<OperationProgress> progress = [];

        Result<Unit> result = await operation.ExecuteAsync(new InlineProgress<OperationProgress>(progress.Add), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(["metadata", "preflight", "download", "verify", "commit"], progress.Select(static value => value.Stage).Distinct().ToArray());
        Assert.True(progress.Zip(progress.Skip(1), static (left, right) =>
            right.TotalBytes >= left.TotalBytes && right.CompletedBytes >= 0).All(static value => value));
        Assert.NotEmpty(store.Records);
        Assert.Equal(operation.Id, store.Records[0].Id);
        Assert.Contains("1.21.8", store.Records[0].FrozenPlanJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairOperationPreservesFallbackConsentFailureAndDoesNotMutatePlan()
    {
        using TestRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/1.21.8.jar", [1, 2, 3]);
        FakeTaskStore store = new();
        RepairVanillaOperation operation = CreateRepairOperation(root, artifact, store);
        List<OperationProgress> progress = [];

        Result<Unit> result = await operation.ExecuteAsync(new InlineProgress<OperationProgress>(progress.Add), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_FALLBACK_CONSENT_REQUIRED", result.Problem?.Code);
        Assert.Equal(InstallAction.Repair, operation.Action);
        Assert.DoesNotContain(progress, value => value.Stage == "commit" && value.CompletedItems > 0);
        Assert.Equal(operation.Id, store.Records[^1].Id);
    }

    private static InstallVanillaOperation CreateOperation(TestRoot root, DownloadArtifact artifact, InstallAction action, FakeTaskStore store)
    {
        PlanVanillaInstall planner = new(new FakeMetadataSource(artifact));
        ExecuteVanillaInstall executor = new(
            new FakeDownloader(artifact),
            new FakeVerifier(),
            new FakeJournal(),
            new FakeEnvironment());
        return new InstallVanillaOperation(
            planner,
            executor,
            new GameRoot("root-1", root.Path, "Fixture", GameRootAvailability.Available, null),
            "1.21.8",
            VanillaPlatform.WindowsX64,
            action,
            store);
    }

    private static RepairVanillaOperation CreateRepairOperation(TestRoot root, DownloadArtifact artifact, FakeTaskStore store) =>
        new(
            new PlanVanillaInstall(new FakeMetadataSource(artifact)),
            new ExecuteVanillaInstall(
            new ConsentDownloader(),
                new FakeVerifier(),
                new FakeJournal(),
                new FakeEnvironment()),
            new GameRoot("root-1", root.Path, "Fixture", GameRootAvailability.Available, null),
            "1.21.8",
            VanillaPlatform.WindowsX64,
            store);

    private static DownloadArtifact Artifact(string path, byte[] bytes) => DownloadArtifact.Create(
        ArtifactKind.ClientJar,
        new Uri("https://official.example.test/" + path.Replace('/', '_')),
        path,
        bytes.Length,
        [new ArtifactHash("sha256", Convert.ToHexString(SHA256.HashData(bytes)))]);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lacertae-op-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private sealed class FakeTaskStore : IBackgroundTaskStore
    {
        public List<BackgroundTaskRecord> Records { get; } = [];
        public Task<Result<Unit>> SaveAsync(BackgroundTaskRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FakeMetadataSource(DownloadArtifact artifact) : IVanillaMetadataSource
    {
        public Task<Result<VanillaMetadataSnapshot>> GetAsync(string versionId, VanillaPlatform platform, CancellationToken cancellationToken) =>
            Task.FromResult(Result<VanillaMetadataSnapshot>.Success(new VanillaMetadataSnapshot(
                versionId,
                "release",
                DateTimeOffset.UtcNow,
                new Lacertae.Domain.Versions.JavaRequirement("java", 17),
                artifact,
                artifact,
                null,
                [],
                artifact,
                [])));
    }

    private sealed class FakeDownloader(DownloadArtifact artifact) : IArtifactDownloader
    {
        public async Task<Result<DownloadReceipt>> DownloadAsync(DownloadRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            string path = System.IO.Path.Combine(request.StagingDirectory, request.Artifact.RelativeDestinationPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3], cancellationToken);
            progress.Report(new OperationProgress("download", 1, 1, artifact.ExpectedSize, artifact.ExpectedSize));
            return Result<DownloadReceipt>.Success(new DownloadReceipt(path, new DownloadSourceId("official"), artifact.ExpectedSize, false, artifact.Hashes[0]));
        }
    }

    private sealed class ConsentDownloader : IArtifactDownloader
    {
        public Task<Result<DownloadReceipt>> DownloadAsync(DownloadRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
            Task.FromResult(Result<DownloadReceipt>.Failure(new Problem(
                "DOWNLOAD_FALLBACK_CONSENT_REQUIRED",
                ProblemStage.Download,
                "problem.download.fallback_consent_required",
                false,
                "consent",
                ["action.download.approve_fallback"])));
    }

    private sealed class FakeVerifier : IGameFileVerifier
    {
        public async Task<Result<bool>> VerifyAsync(DownloadArtifact artifact, string filePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath)) return Result<bool>.Success(false);
            byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            return Result<bool>.Success(bytes.SequenceEqual(new byte[] { 1, 2, 3 }));
        }
    }

    private sealed class FakeJournal : IInstallJournalRepository
    {
        public Task<Result<Unit>> SaveAsync(VanillaInstallPlan plan, InstallJournal journal, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
        public Task<Result<IReadOnlyList<InstallJournalRecord>>> GetRecoverableAsync(CancellationToken cancellationToken) => Task.FromResult(Result<IReadOnlyList<InstallJournalRecord>>.Success([]));
        public Task<Result<Unit>> RemoveAsync(string operationId, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }

    private sealed class FakeEnvironment : IInstallEnvironment
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool IsDirectoryWritable(string path) => true;
        public long GetAvailableFreeBytes(string path) => long.MaxValue;
    }
}
