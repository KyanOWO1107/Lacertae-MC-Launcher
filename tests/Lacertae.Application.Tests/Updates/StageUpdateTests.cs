using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lacertae.Application.Archives;
using Lacertae.Application.Downloads;
using Lacertae.Application.Updates;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;
using Lacertae.Domain.Updates;

namespace Lacertae.Application.Tests.Updates;

public sealed class StageUpdateTests
{
    [Fact]
    public async Task ExecuteAsyncDownloadsVerifiesExtractsAndWritesStagedMetadata()
    {
        using TemporaryRoot root = new();
        byte[] packageBytes = Encoding.UTF8.GetBytes("package-bytes");
        string packageManifest = ManifestJson();
        string packageManifestHash = Hash(Encoding.UTF8.GetBytes(packageManifest));
        FakeDownloader downloader = new(packageBytes);
        FakeExtractor extractor = new(packageManifest, "app/Lacertae.Desktop.dll", Encoding.UTF8.GetBytes("desktop"));
        StageUpdate stage = new(downloader, extractor);
        VerifiedUpdateManifest update = new(
            Manifest(packageBytes.Length, Hash(packageBytes), packageManifestHash),
            Encoding.UTF8.GetBytes("canonical-manifest"),
            [1, 2, 3]);

        var result = await stage.ExecuteAsync(
            new StageUpdateRequest(update, "1.0.0", root.Path, true, false, false, "stage-test"),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.True(File.Exists(Path.Combine(root.Path, result.Value.RelativeStagingPath, "signed-manifest.json")));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(
            Path.Combine(root.Path, result.Value.RelativeStagingPath, "signed-manifest.sig"),
            TestContext.Current.CancellationToken));
        Assert.True(File.Exists(result.Value.MetadataPath));
        Assert.Contains("staging/", await File.ReadAllTextAsync(result.Value.MetadataPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(1, downloader.Calls);
        Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public async Task ExecuteAsyncRequiresConfirmationAndRejectsActiveOperations()
    {
        using TemporaryRoot root = new();
        StageUpdate stage = new(new FakeDownloader([1]), new FakeExtractor(ManifestJson(), "app.dll", [1]));
        VerifiedUpdateManifest update = new(
            Manifest(1, Hash([1]), Hash(Encoding.UTF8.GetBytes(ManifestJson()))),
            [1],
            [1]);

        var notConfirmed = await stage.ExecuteAsync(
            new StageUpdateRequest(update, "1.0.0", root.Path, false, false, false, "test"),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);
        var active = await stage.ExecuteAsync(
            new StageUpdateRequest(update, "1.0.0", root.Path, true, true, false, "test"),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(notConfirmed.IsSuccess);
        Assert.Equal("UPDATE_CONFIRMATION_REQUIRED", notConfirmed.Problem?.Code);
        Assert.False(active.IsSuccess);
        Assert.Equal("UPDATE_ACTIVE_OPERATION", active.Problem?.Code);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsIndependentPackageHashMismatch()
    {
        using TemporaryRoot root = new();
        byte[] packageBytes = [1, 2, 3];
        StageUpdate stage = new(new FakeDownloader(packageBytes), new FakeExtractor(ManifestJson(), "app.dll", [1]));
        UpdateManifest manifest = Manifest(packageBytes.Length, new string('f', 64), Hash(Encoding.UTF8.GetBytes(ManifestJson())));

        var result = await stage.ExecuteAsync(
            new StageUpdateRequest(new VerifiedUpdateManifest(manifest, [1], [1]), "1.0.0", root.Path, true, false, false, "test"),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("UPDATE_PACKAGE_HASH_MISMATCH", result.Problem?.Code);
    }

    private static UpdateManifest Manifest(long size, string sha256, string fileManifestSha256) => new(
        1,
        "test-key",
        UpdateChannel.Stable,
        "1.1.0",
        DateTimeOffset.UtcNow.AddMinutes(-1),
        "1.0.0",
        new Dictionary<string, string>(StringComparer.Ordinal) { ["en-US"] = "Notes" },
        new Uri("https://updates.example.test/notes"),
        new UpdatePackage("win-x64", new Uri("https://updates.example.test/package.zip"), size, sha256, fileManifestSha256));

    private static string ManifestJson() => "{\"schemaVersion\":1,\"files\":[{\"path\":\"app/Lacertae.Desktop.dll\",\"size\":7,\"sha256\":\"" + Hash(Encoding.UTF8.GetBytes("desktop")) + "\"}]}";

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FakeDownloader(byte[] packageBytes) : IArtifactDownloader
    {
        public int Calls { get; private set; }

        public async Task<Result<DownloadReceipt>> DownloadAsync(
            DownloadRequest request,
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            Directory.CreateDirectory(request.StagingDirectory);
            string path = Path.Combine(request.StagingDirectory, "launcher-update.zip");
            await File.WriteAllBytesAsync(path, packageBytes, cancellationToken);
            return Result<DownloadReceipt>.Success(new DownloadReceipt(
                path,
                new DownloadSourceId("official"),
                packageBytes.Length,
                false,
                request.Artifact.Hashes[0]));
        }
    }

    private sealed class FakeExtractor(
        string packageManifest,
        string filePath,
        byte[] fileBytes) : IArchiveExtractor
    {
        public int Calls { get; private set; }

        public async Task<Result<Unit>> ExtractAsync(
            ArchiveExtractionRequest request,
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            Directory.CreateDirectory(request.DestinationDirectory);
            string manifestPath = Path.Combine(request.DestinationDirectory, "package-manifest.json");
            await File.WriteAllTextAsync(manifestPath, packageManifest, cancellationToken);
            string filePathOnDisk = Path.Combine(request.DestinationDirectory, filePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(filePathOnDisk)!);
            await File.WriteAllBytesAsync(filePathOnDisk, fileBytes, cancellationToken);
            return Result.Success();
        }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lacertae-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
