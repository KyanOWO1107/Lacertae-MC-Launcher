using Lacertae.Application.Install;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Install;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Tests.Install;

public sealed class PlanVanillaInstallTests
{
    [Fact]
    public async Task ExecuteAsyncFreezesArtifactsAndWorkingSpaceWithoutWritingFiles()
    {
        string gameRootPath = Path.Combine(Path.GetTempPath(), "lacertae-plan-" + Guid.NewGuid().ToString("N"));
        var metadata = Snapshot();
        var result = await new PlanVanillaInstall(new FakeSource(metadata)).ExecuteAsync(
            new GameRoot("root-1", gameRootPath, "Fixture", GameRootAvailability.Available, null),
            "1.21.8",
            InstallAction.Install,
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("root-1", result.Value.GameRootId);
        Assert.Equal(Path.GetFullPath(gameRootPath), result.Value.GameRootPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(gameRootPath), "versions", "1.21.8"), result.Value.VersionDirectory);
        Assert.Equal(4, result.Value.Artifacts.Count);
        Assert.Equal(result.Value.Artifacts.Sum(static artifact => artifact.ExpectedSize), result.Value.RequiredDownloadBytes);
        Assert.True(result.Value.RequiredWorkingBytes > result.Value.RequiredDownloadBytes);
        Assert.DoesNotContain(result.Value.Artifacts, artifact =>
            Path.IsPathRooted(artifact.RelativeDestinationPath) || artifact.RelativeDestinationPath.Contains("..", StringComparison.Ordinal));
        Assert.False(Directory.Exists(gameRootPath));
    }

    [Fact]
    public async Task ExecuteAsyncRejectsDuplicateDestinationWithDifferentHash()
    {
        DownloadArtifact first = Artifact(ArtifactKind.Library, "libraries/a.jar", 10, 'a');
        DownloadArtifact second = Artifact(ArtifactKind.Library, "libraries/a.jar", 10, 'b');
        VanillaMetadataSnapshot metadata = Snapshot([first, second]);

        var result = await new PlanVanillaInstall(new FakeSource(metadata)).ExecuteAsync(
            new GameRoot("root-1", @"C:\Games\.minecraft", "Fixture", GameRootAvailability.Available, null),
            "1.21.8",
            InstallAction.Repair,
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", result.Problem?.Code);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsParentSegmentsEvenWhenTheyRemainUnderGameRoot()
    {
        DownloadArtifact unsafeArtifact = Artifact(ArtifactKind.Library, "libraries/a.jar", 10, 'a') with
        {
            RelativeDestinationPath = "libraries/../a.jar",
        };

        var result = await new PlanVanillaInstall(new FakeSource(Snapshot([unsafeArtifact]))).ExecuteAsync(
            new GameRoot("root-1", Path.Combine(Path.GetTempPath(), "lacertae-plan-" + Guid.NewGuid().ToString("N")), "Fixture", GameRootAvailability.Available, null),
            "1.21.8",
            InstallAction.Install,
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", result.Problem?.Code);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsUnsafeVersionIdBeforeCallingMetadataSource()
    {
        var source = new FakeSource(Snapshot());
        var result = await new PlanVanillaInstall(source).ExecuteAsync(
            new GameRoot("root-1", Path.Combine(Path.GetTempPath(), "lacertae-plan-" + Guid.NewGuid().ToString("N")), "Fixture", GameRootAvailability.Available, null),
            "../1.21.8",
            InstallAction.Install,
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", result.Problem?.Code);
        Assert.False(source.WasCalled);
    }

    private static VanillaMetadataSnapshot Snapshot(IReadOnlyList<DownloadArtifact>? libraries = null) =>
        new(
            "1.21.8",
            "release",
            new DateTimeOffset(2025, 7, 17, 12, 4, 2, TimeSpan.Zero),
            new JavaRequirement("java-runtime-delta", 21),
            Artifact(ArtifactKind.VersionMetadata, "versions/1.21.8/1.21.8.json", 100, 'a'),
            Artifact(ArtifactKind.ClientJar, "versions/1.21.8/1.21.8.jar", 200, 'b'),
            null,
            libraries ?? [Artifact(ArtifactKind.Library, "libraries/a.jar", 300, 'c')],
            Artifact(ArtifactKind.AssetIndex, "assets/indexes/26.json", 400, 'd'),
            []);

    private static DownloadArtifact Artifact(ArtifactKind kind, string path, long size, char hashCharacter) =>
        DownloadArtifact.Create(
            kind,
            new Uri("https://official.example.test/" + path.Replace('/', '_')),
            path,
            size,
            [new ArtifactHash("sha256", new string(hashCharacter, 64))]);

    private sealed class FakeSource(VanillaMetadataSnapshot metadata) : IVanillaMetadataSource
    {
        public bool WasCalled { get; private set; }

        public Task<Result<VanillaMetadataSnapshot>> GetAsync(
            string versionId,
            VanillaPlatform platform,
            CancellationToken cancellationToken) =>
            Task.FromResult(Called());

        private Result<VanillaMetadataSnapshot> Called()
        {
            WasCalled = true;
            return Result<VanillaMetadataSnapshot>.Success(metadata);
        }
    }
}
