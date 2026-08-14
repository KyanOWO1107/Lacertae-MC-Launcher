using System.Security.Cryptography;
using Lacertae.Application.Downloads;
using Lacertae.Application.Java;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Java;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Storage;
using Xunit;

namespace Lacertae.Application.Tests.Java;

public sealed class InstallManagedJavaTests
{
    [Fact]
    public async Task ExecuteAsyncDownloadsIntoOperationStagingAndCommitsOnlyAfterProbe()
    {
        using TemporaryRoot root = new();
        FakePackage package = FakePackage.Create();
        FakeDownloader downloader = new(package);
        FakeProbe probe = new(package);

        Result<JavaInstallation> result = await CreateInstaller(package, downloader, probe)
            .ExecuteAsync(root.DataRoot, package.Component, package.Architecture, null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(2, downloader.Downloads.Count);
        Assert.All(downloader.Downloads, path =>
            Assert.StartsWith(Path.Combine(root.DataRoot.LocalPath, "runtimes", ".staging"), path, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(result.Value.ExecutablePath));
        Assert.True(File.Exists(Path.Combine(root.DataRoot.LocalPath, "runtimes", package.Component, package.PackageVersion + "-x64", "runtime.json")));
        Assert.True(probe.Called);
        Assert.True(Directory.Exists(Path.Combine(root.DataRoot.LocalPath, "runtimes", package.Component, package.PackageVersion + "-x64")));
        Assert.False(Directory.Exists(Path.Combine(root.DataRoot.LocalPath, "runtimes", ".staging", downloader.OperationId)));
    }

    [Fact]
    public async Task ExecuteAsyncRejectsHashMismatchAndLeavesFinalTargetAbsent()
    {
        using TemporaryRoot root = new();
        FakePackage package = FakePackage.Create();
        FakeDownloader downloader = new(package) { CorruptFirstFile = true };

        Result<JavaInstallation> result = await CreateInstaller(package, downloader, new FakeProbe(package))
            .ExecuteAsync(root.DataRoot, package.Component, package.Architecture, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_HASH_MISMATCH", result.Problem?.Code);
        Assert.False(Directory.Exists(Path.Combine(root.DataRoot.LocalPath, "runtimes", package.Component, package.PackageVersion + "-x64")));
        Assert.False(Directory.EnumerateDirectories(Path.Combine(root.DataRoot.LocalPath, "runtimes", ".staging"), "*", SearchOption.TopDirectoryOnly).Any());
    }

    [Theory]
    [InlineData("/absolute.bin")]
    [InlineData("../escape.bin")]
    [InlineData("bin/../escape.bin")]
    public async Task ExecuteAsyncRejectsUnsafeManifestPathBeforeDownload(string path)
    {
        using TemporaryRoot root = new();
        FakePackage package = FakePackage.Create(path);
        FakeDownloader downloader = new(package);

        Result<JavaInstallation> result = await CreateInstaller(package, downloader, new FakeProbe(package))
            .ExecuteAsync(root.DataRoot, package.Component, package.Architecture, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_RUNTIME_MANIFEST_INVALID", result.Problem?.Code);
        Assert.Empty(downloader.Downloads);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsDuplicateAndCaseCollidingManifestPathsBeforeDownload()
    {
        using TemporaryRoot root = new();
        FakePackage package = FakePackage.CreateWithPaths("bin/java.exe", "BIN/JAVA.EXE");
        FakeDownloader downloader = new(package);

        Result<JavaInstallation> result = await CreateInstaller(package, downloader, new FakeProbe(package))
            .ExecuteAsync(root.DataRoot, package.Component, package.Architecture, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_RUNTIME_MANIFEST_INVALID", result.Problem?.Code);
        Assert.Empty(downloader.Downloads);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsManifestAboveConfiguredLimits()
    {
        using TemporaryRoot root = new();
        FakePackage package = FakePackage.Create();
        package.Files = [
            FakeArtifact.FromPath("bin/java.exe", [1, 2, 3, 4]),
            FakeArtifact.FromPath("lib/0.bin", [1, 2, 3]),
            FakeArtifact.FromPath("lib/1.bin", [1, 2, 3]),
        ];
        FakeDownloader downloader = new(package);
        InstallManagedJavaOptions options = new() { MaximumFileCount = 2, MaximumTotalBytes = 1024 };

        Result<JavaInstallation> result = await new InstallManagedJava(package.CatalogPort, downloader, new FakeProbe(package), options)
            .ExecuteAsync(root.DataRoot, package.Component, package.Architecture, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_RUNTIME_MANIFEST_TOO_LARGE", result.Problem?.Code);
        Assert.Empty(downloader.Downloads);
    }

    [Fact]
    public async Task ExecuteAsyncCancellationCleansStagingAndPreservesExistingInstall()
    {
        using TemporaryRoot root = new();
        FakePackage existingPackage = FakePackage.Create(packageVersion: "89ce85ccb518c62e18b4b58d63399ba2d9611426");
        Result<JavaInstallation> existing = await CreateInstaller(existingPackage, new FakeDownloader(existingPackage), new FakeProbe(existingPackage))
            .ExecuteAsync(root.DataRoot, existingPackage.Component, existingPackage.Architecture, null, TestContext.Current.CancellationToken);
        Assert.True(existing.IsSuccess, existing.Problem?.Code);
        string target = Directory.GetParent(Path.GetDirectoryName(existing.Value.ExecutablePath)!)!.FullName;
        string existingRuntime = File.ReadAllText(Path.Combine(target, "runtime.json"));
        FakePackage package = FakePackage.Create(packageVersion: "cb4394a27089d19f65d5baa6cf0482c27c3c7865");
        using CancellationTokenSource cancellation = new();
        FakeDownloader downloader = new(package) { Cancel = cancellation };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateInstaller(package, downloader, new FakeProbe(package))
            .ExecuteAsync(root.DataRoot, package.Component, package.Architecture, null, cancellation.Token));

        Assert.Equal(existingRuntime, File.ReadAllText(Path.Combine(target, "runtime.json")));
        Assert.False(Directory.EnumerateDirectories(Path.Combine(root.DataRoot.LocalPath, "runtimes", ".staging"), "*", SearchOption.TopDirectoryOnly).Any());
    }

    [Fact]
    public async Task ExecuteAsyncIsIdempotentForAlreadyVerifiedRuntime()
    {
        using TemporaryRoot root = new();
        FakePackage package = FakePackage.Create();
        FakeDownloader firstDownloader = new(package);
        FakeProbe firstProbe = new(package);
        InstallManagedJava installer = CreateInstaller(package, firstDownloader, firstProbe);
        Result<JavaInstallation> first = await installer.ExecuteAsync(root.DataRoot, package.Component, package.Architecture, null, TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess, first.Problem?.Code);

        FakeDownloader secondDownloader = new(package);
        FakeProbe secondProbe = new(package);
        Result<JavaInstallation> second = await CreateInstaller(package, secondDownloader, secondProbe)
            .ExecuteAsync(root.DataRoot, package.Component, package.Architecture, null, TestContext.Current.CancellationToken);

        Assert.True(second.IsSuccess, second.Problem?.Code);
        Assert.Empty(secondDownloader.Downloads);
        Assert.False(secondProbe.Called);
        Assert.Equal(first.Value.ExecutablePath, second.Value.ExecutablePath);
    }

    private static InstallManagedJava CreateInstaller(FakePackage package, FakeDownloader downloader, FakeProbe probe) =>
        new(package.CatalogPort, downloader, probe);

    private sealed class TemporaryRoot : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "lacertae-install-" + Guid.NewGuid().ToString("N"));

        public TemporaryRoot()
        {
            Directory.CreateDirectory(path);
            DataRoot = new(DataRootMode.UserProfile, Path.Combine(path, "roaming"), Path.Combine(path, "local"));
            Directory.CreateDirectory(DataRoot.LocalPath);
        }

        public DataRoot DataRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    private sealed class FakePackage : IManagedJavaCatalog
    {
        private FakePackage(string component, JavaArchitecture architecture, string packageVersion, List<FakeArtifact> files)
        {
            Component = component;
            Architecture = architecture;
            PackageVersion = packageVersion;
            Files = files;
            CatalogPort = new Catalog(this);
        }

        public string Component { get; }
        public JavaArchitecture Architecture { get; }
        public string PackageVersion { get; }
        public List<FakeArtifact> Files { get; set; }
        public IManagedJavaCatalog CatalogPort { get; }

        public Task<Result<ManagedJavaPackage>> GetPackageAsync(string component, JavaArchitecture architecture, CancellationToken cancellationToken)
        {
            ManagedJavaPackage package = new(Component, 17, Architecture, PackageVersion, ["bin", "lib"], Files.Select(static file => file.Artifact).ToList(), "bin/java.exe");
            return Task.FromResult(Result<ManagedJavaPackage>.Success(package));
        }

        public static FakePackage Create(string? firstPath = null, string? packageVersion = null)
        {
            FakePackage package = new("java-runtime-beta", JavaArchitecture.X64, packageVersion ?? "89ce85ccb518c62e18b4b58d63399ba2d9611426", []);
            package.Files = [
                FakeArtifact.FromPath(firstPath ?? "bin/java.exe", [1, 2, 3, 4]),
                FakeArtifact.FromPath("release", [5, 6, 7]),
            ];
            return package;
        }

        public static FakePackage CreateWithPaths(params string[] paths)
        {
            FakePackage package = new("java-runtime-beta", JavaArchitecture.X64, "89ce85ccb518c62e18b4b58d63399ba2d9611426", []);
            package.Files = paths.Select(path => FakeArtifact.FromPath(path, [1, 2, 3])).ToList();
            return package;
        }

        private sealed class Catalog(FakePackage package) : IManagedJavaCatalog
        {
            public Task<Result<ManagedJavaPackage>> GetPackageAsync(string component, JavaArchitecture architecture, CancellationToken cancellationToken) =>
                package.GetPackageAsync(component, architecture, cancellationToken);
        }
    }

    private sealed class FakeArtifact
    {
        public FakeArtifact(DownloadArtifact artifact, byte[] content)
        {
            Artifact = artifact;
            Content = content;
        }

        public DownloadArtifact Artifact { get; }
        public byte[] Content { get; }

        public static FakeArtifact FromPath(string path, byte[] content)
        {
            string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            DownloadArtifact artifact = !path.StartsWith('/') && !path.Contains("..", StringComparison.Ordinal)
                ? DownloadArtifact.Create(ArtifactKind.JavaRuntime, new Uri("https://example.test/" + Guid.NewGuid().ToString("N")), path, content.Length, [new("sha256", sha256)])
                : new(Guid.NewGuid().ToString("N"), ArtifactKind.JavaRuntime, new Uri("https://example.test/"), path, content.Length, [new("sha256", sha256)]);
            return new FakeArtifact(artifact, content);
        }
    }

    private sealed class FakeDownloader(FakePackage package) : IArtifactDownloader
    {
        public List<string> Downloads { get; } = [];
        public string OperationId { get; } = Guid.NewGuid().ToString("N");
        public bool CorruptFirstFile { get; init; }
        public CancellationTokenSource? Cancel { get; init; }

        public Task<Result<DownloadReceipt>> DownloadAsync(DownloadRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadArtifact artifact = request.Artifact;
            string stagingDirectory = request.StagingDirectory;
            string path = Path.Combine(stagingDirectory, artifact.RelativeDestinationPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FakeArtifact source = package.Files.Single(file => file.Artifact.ArtifactId == artifact.ArtifactId);
            byte[] content = source.Content;
            if (CorruptFirstFile && Downloads.Count == 0)
            {
                content = [9, 9, 9];
            }

            File.WriteAllBytes(path, content);
            Downloads.Add(path);
            Cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result<DownloadReceipt>.Success(new DownloadReceipt(
                path,
                new DownloadSourceId("fixture"),
                content.Length,
                WasResumed: false,
                artifact.Hashes.Single())));
        }

    }

    private sealed class FakeProbe(FakePackage package) : IJavaProbe
    {
        public bool Called { get; private set; }

        public Task<Result<JavaInstallation>> ProbeAsync(string executablePath, JavaSource source, bool isManaged, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(Result<JavaInstallation>.Success(new JavaInstallation(
                "managed-fixture",
                executablePath,
                17,
                "17.0.1",
                "Fixture Vendor",
                package.Architecture,
                source,
                isManaged)));
        }
    }
}
