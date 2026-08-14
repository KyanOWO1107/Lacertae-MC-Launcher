using System.Collections.Concurrent;
using System.Security.Cryptography;
using Lacertae.Application.Downloads;
using Lacertae.Application.Install;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Install;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Install;

public sealed class ExecuteVanillaInstallTests
{
    [Fact]
    public async Task PreflightRejectsInsufficientSpaceBeforeDownload()
    {
        using InstallTestRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/client.jar", [1, 2, 3]);
        FakeDownloader downloader = new();
        FakeJournalRepository journal = new();
        ExecuteVanillaInstall install = CreateInstall(downloader, journal, new FakeEnvironment(availableBytes: 0));

        Result<Unit> result = await install.ExecuteAsync(
            Plan(root.Path, [artifact]),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("INSTALL_DISK_SPACE_INSUFFICIENT", result.Problem?.Code);
        Assert.Empty(downloader.Requests);
        Assert.Empty(journal.Saves);
    }

    [Fact]
    public async Task PreflightRejectsUnavailableOrUnwritableRootBeforeDownload()
    {
        using InstallTestRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/client.jar", [1, 2, 3]);
        FakeDownloader downloader = new();
        FakeJournalRepository journal = new();

        Result<Unit> unavailable = await CreateInstall(
            downloader,
            journal,
            new FakeEnvironment { IsAvailable = false }).ExecuteAsync(
                Plan(root.Path, [artifact]),
                new Progress<OperationProgress>(),
                TestContext.Current.CancellationToken);
        Assert.False(unavailable.IsSuccess);
        Assert.Equal("INSTALL_ROOT_UNAVAILABLE", unavailable.Problem?.Code);

        Result<Unit> unwritable = await CreateInstall(
            downloader,
            journal,
            new FakeEnvironment { IsWritable = false }).ExecuteAsync(
                Plan(root.Path, [artifact], operationId: "unwritable"),
                new Progress<OperationProgress>(),
                TestContext.Current.CancellationToken);
        Assert.False(unwritable.IsSuccess);
        Assert.Equal("INSTALL_ROOT_UNWRITABLE", unwritable.Problem?.Code);
        Assert.Empty(downloader.Requests);
    }

    [Fact]
    public async Task RejectsArtifactPathEscapeBeforeDownload()
    {
        using InstallTestRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/client.jar", [1, 2, 3]) with
        {
            RelativeDestinationPath = "versions/../outside.jar",
        };
        FakeDownloader downloader = new();

        Result<Unit> result = await CreateInstall(
            downloader,
            new FakeJournalRepository(),
            new FakeEnvironment()).ExecuteAsync(
                Plan(root.Path, [artifact], operationId: "escape"),
                new Progress<OperationProgress>(),
                TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("INSTALL_PATH_INVALID", result.Problem?.Code);
        Assert.Empty(downloader.Requests);
    }

    [Fact]
    public async Task SkipsValidFilesDownloadsMissingFilesAndCommitsAtomically()
    {
        using InstallTestRoot root = new();
        DownloadArtifact valid = Artifact("versions/1.21.8/1.21.8.json", [1, 2]);
        DownloadArtifact missing = Artifact("versions/1.21.8/1.21.8.jar", [3, 4, 5]);
        string validPath = root.WriteFinal(valid, [1, 2]);
        FakeDownloader downloader = new();
        FakeJournalRepository journal = new();
        ExecuteVanillaInstall install = CreateInstall(downloader, journal, new FakeEnvironment());

        Result<Unit> result = await install.ExecuteAsync(
            Plan(root.Path, [valid, missing]),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal([1, 2], await File.ReadAllBytesAsync(validPath, TestContext.Current.CancellationToken));
        Assert.Equal([3, 4, 5], await File.ReadAllBytesAsync(root.FinalPath(missing), TestContext.Current.CancellationToken));
        Assert.Single(downloader.Requests);
        Assert.True(journal.Removed);
        Assert.Contains(journal.Saves.SelectMany(static save => save.Journal.Moves), move =>
            move.FinalRelativePath == missing.RelativeDestinationPath && move.Applied);
    }

    [Fact]
    public async Task DamagedFileMovesToOperationQuarantineBeforeReplacement()
    {
        using InstallTestRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/1.21.8.jar", [3, 4, 5]);
        string finalPath = root.WriteFinal(artifact, [0, 0, 0]);
        FakeJournalRepository journal = new();
        Result<Unit> result = await CreateInstall(new FakeDownloader(), journal, new FakeEnvironment()).ExecuteAsync(
            Plan(root.Path, [artifact], InstallAction.Repair),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal([3, 4, 5], await File.ReadAllBytesAsync(finalPath, TestContext.Current.CancellationToken));
        Assert.Contains(journal.Saves.SelectMany(static save => save.Journal.Moves), move =>
            move.FinalRelativePath == artifact.RelativeDestinationPath && move.QuarantineRelativePath is not null);
    }

    [Fact]
    public async Task StagingFailureLeavesInstalledFilesUnchanged()
    {
        using InstallTestRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/1.21.8.jar", [3, 4, 5]);
        string finalPath = root.WriteFinal(artifact, [0, 0, 0]);
        FakeDownloader downloader = new()
        {
            Failure = new Problem(
                "DOWNLOAD_UNAVAILABLE",
                ProblemStage.Download,
                "problem.download.unavailable",
                true,
                "download",
                ["action.download.retry"]),
        };

        Result<Unit> result = await CreateInstall(downloader, new FakeJournalRepository(), new FakeEnvironment()).ExecuteAsync(
            Plan(root.Path, [artifact]),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal([0, 0, 0], await File.ReadAllBytesAsync(finalPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CommitConflictNeverOverwritesUnexpectedUserFile()
    {
        using InstallTestRoot root = new();
        DownloadArtifact artifact = Artifact("versions/1.21.8/1.21.8.jar", [3, 4, 5]);
        FakeJournalRepository journal = new();
        journal.OnSave = (plan, saved) =>
        {
            if (saved.State == InstallJournalState.Verified)
            {
                string path = root.FinalPath(artifact);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [99, 99, 99]);
            }
        };

        Result<Unit> result = await CreateInstall(new FakeDownloader(), journal, new FakeEnvironment()).ExecuteAsync(
            Plan(root.Path, [artifact]),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("INSTALL_COMMIT_CONFLICT", result.Problem?.Code);
        Assert.Equal([99, 99, 99], await File.ReadAllBytesAsync(root.FinalPath(artifact), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentPlansForTheSameRootAreSerialized()
    {
        using InstallTestRoot root = new();
        DownloadArtifact first = Artifact("versions/1.21.8/first.jar", [1]);
        DownloadArtifact second = Artifact("versions/1.21.8/second.jar", [2]);
        TrackingDownloader downloader = new();
        ExecuteVanillaInstall install = CreateInstall(downloader, new FakeJournalRepository(), new FakeEnvironment());

        Task<Result<Unit>> firstTask = install.ExecuteAsync(
            Plan(root.Path, [first], operationId: "operation-one"),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);
        Task<Result<Unit>> secondTask = install.ExecuteAsync(
            Plan(root.Path, [second], operationId: "operation-two"),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Result<Unit>[] results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Problem?.Code));
        Assert.Equal(1, downloader.MaximumConcurrent);
    }

    private static ExecuteVanillaInstall CreateInstall(
        IArtifactDownloader downloader,
        IInstallJournalRepository journal,
        IInstallEnvironment environment) =>
        new(downloader, new FixtureVerifier(), journal, environment);

    private static VanillaInstallPlan Plan(
        string root,
        IReadOnlyList<DownloadArtifact> artifacts,
        InstallAction action = InstallAction.Install,
        string operationId = "operation-test") =>
        new(
            operationId,
            action,
            "root-test",
            root,
            "1.21.8",
            Path.Combine(root, "versions", "1.21.8"),
            artifacts.Sum(static artifact => artifact.ExpectedSize),
            artifacts.Sum(static artifact => artifact.ExpectedSize) + 256 * 1024 * 1024,
            artifacts,
            DateTimeOffset.UtcNow);

    private static DownloadArtifact Artifact(string relativePath, byte[] content) =>
        DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            new Uri("https://official.example.test/" + relativePath.Replace('/', '_')),
            relativePath,
            content.Length,
            [new ArtifactHash("sha256", Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant())]);

    private sealed class InstallTestRoot : IDisposable
    {
        public InstallTestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lacertae-install-" + Guid.NewGuid().ToString("N"));
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

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FakeEnvironment(long? availableBytes = null) : IInstallEnvironment
    {
        public bool IsAvailable { get; init; } = true;
        public bool IsWritable { get; init; } = true;
        public bool DirectoryExists(string path) => IsAvailable && Directory.Exists(path);
        public bool IsDirectoryWritable(string path) => IsWritable && Directory.Exists(path);
        public long GetAvailableFreeBytes(string path) => availableBytes ?? long.MaxValue;
    }

    private sealed class FakeDownloader : IArtifactDownloader
    {
        public List<DownloadRequest> Requests { get; } = [];
        public Problem? Failure { get; init; }

        public async Task<Result<DownloadReceipt>> DownloadAsync(
            DownloadRequest request,
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Failure is not null)
            {
                return Result<DownloadReceipt>.Failure(Failure);
            }

            string path = System.IO.Path.Combine(
                request.StagingDirectory,
                request.Artifact.RelativeDestinationPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            byte[] content = System.IO.Path.GetFileName(request.Artifact.RelativeDestinationPath) switch
            {
                "1.21.8.json" => [1, 2],
                "1.21.8.jar" => [3, 4, 5],
                "first.jar" => [1],
                "second.jar" => [2],
                _ => Enumerable.Range(0, checked((int)request.Artifact.ExpectedSize)).Select(static value => (byte)value).ToArray(),
            };

            await File.WriteAllBytesAsync(path, content, cancellationToken);
            return Result<DownloadReceipt>.Success(new DownloadReceipt(
                path,
                new DownloadSourceId("fixture"),
                content.Length,
                WasResumed: false,
                request.Artifact.Hashes[0]));
        }
    }

    private sealed class TrackingDownloader : IArtifactDownloader
    {
        private int active;
        public int MaximumConcurrent { get; private set; }

        public async Task<Result<DownloadReceipt>> DownloadAsync(
            DownloadRequest request,
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref active);
            MaximumConcurrent = Math.Max(MaximumConcurrent, current);
            await Task.Delay(25, cancellationToken);
            string path = System.IO.Path.Combine(request.StagingDirectory, request.Artifact.RelativeDestinationPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            byte[] content = request.Artifact.ExpectedSize == 1 && request.Artifact.RelativeDestinationPath.EndsWith("first.jar", StringComparison.Ordinal)
                ? [1]
                : [2];
            await File.WriteAllBytesAsync(path, content, cancellationToken);
            Interlocked.Decrement(ref active);
            return Result<DownloadReceipt>.Success(new DownloadReceipt(path, new DownloadSourceId("fixture"), content.Length, false, request.Artifact.Hashes[0]));
        }
    }

    private sealed class FakeJournalRepository : IInstallJournalRepository
    {
        public List<(VanillaInstallPlan Plan, InstallJournal Journal)> Saves { get; } = [];
        public bool Removed { get; private set; }
        public Action<VanillaInstallPlan, InstallJournal>? OnSave { get; set; }

        public Task<Result<Unit>> SaveAsync(VanillaInstallPlan plan, InstallJournal journal, CancellationToken cancellationToken)
        {
            Saves.Add((plan, journal));
            OnSave?.Invoke(plan, journal);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<InstallJournalRecord>>> GetRecoverableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<InstallJournalRecord>>.Success([]));

        public Task<Result<Unit>> RemoveAsync(string operationId, CancellationToken cancellationToken)
        {
            Removed = true;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FixtureVerifier : IGameFileVerifier
    {
        public async Task<Result<bool>> VerifyAsync(
            DownloadArtifact artifact,
            string filePath,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length != artifact.ExpectedSize)
            {
                return Result<bool>.Success(false);
            }

            byte[] content = await File.ReadAllBytesAsync(filePath, cancellationToken);
            string actual = Convert.ToHexString(SHA256.HashData(content));
            return Result<bool>.Success(artifact.Hashes.Any(hash =>
                hash.NormalizedAlgorithm == "sha256" &&
                string.Equals(hash.NormalizedHexDigest, actual, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
