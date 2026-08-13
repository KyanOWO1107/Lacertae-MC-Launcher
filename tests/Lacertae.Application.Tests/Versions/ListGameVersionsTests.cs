using Lacertae.Application.Games;
using Lacertae.Application.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Tests.Versions;

public sealed class ListGameVersionsTests
{
    [Fact]
    public async Task ExecuteAsyncMergesOverrideWithoutReplacingPhysicalDescriptor()
    {
        const string rootId = "root-1";
        const string rootPath = @"C:\Games\.minecraft";
        GameVersionDescriptor descriptor = new(
            "engine-root-id",
            "fabric-1.21",
            "Physical Folder",
            "release",
            "1.21",
            new JavaRequirement("java-runtime-gamma", 17),
            HasModLoader: true);
        VersionOverride versionOverride = new(
            rootId,
            descriptor.FolderName,
            "我的 Fabric 版本",
            IsolationOverride.ForceIsolated,
            "account-1",
            @"C:\Java\21\bin\javaw.exe",
            1024,
            4096,
            GcProfile.G1,
            ["-Dexample=true"],
            ["--demo"]);

        var result = await new ListGameVersions(
            new FakeGameEngine([descriptor]),
            new FakeVersionOverrideRepository([versionOverride]))
            .ExecuteAsync(
                new GameRoot(rootId, rootPath, "Minecraft", GameRootAvailability.Available, null),
                LauncherSettings.Default,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        ListedGameVersion listed = Assert.Single(result.Value);
        Assert.Equal(rootId, listed.Descriptor.GameRootId);
        Assert.Equal(descriptor.FolderName, listed.FolderName);
        Assert.Equal(descriptor.VersionType, listed.VersionType);
        Assert.Equal(descriptor.InheritsFrom, listed.InheritsFrom);
        Assert.Equal(descriptor.Java, listed.Java);
        Assert.True(listed.HasModLoader);
        Assert.Equal(versionOverride.DisplayName, listed.DisplayName);
        Assert.Equal(versionOverride.AccountId, listed.AccountId);
        Assert.Equal(versionOverride.JavaPath, listed.JavaPath);
        Assert.Equal(versionOverride.MinimumMemoryMb, listed.MinimumMemoryMb);
        Assert.Equal(versionOverride.MaximumMemoryMb, listed.MaximumMemoryMb);
        Assert.Equal(versionOverride.GcProfile, listed.GcProfile);
        Assert.Equal(versionOverride.JvmArguments, listed.JvmArguments);
        Assert.Equal(versionOverride.GameArguments, listed.GameArguments);
        Assert.True(listed.IsolationDecision.IsIsolated);
        Assert.False(listed.IsolationDecision.RequiresUserNotice);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotReturnStaleOverrideButRepositoryKeepsIt()
    {
        const string rootId = "root-1";
        VersionOverride stale = new(
            rootId,
            "deleted-version",
            "保留用于恢复",
            IsolationOverride.ForceIsolated,
            null,
            null,
            null,
            null,
            null,
            [],
            []);
        FakeVersionOverrideRepository repository = new([stale]);
        GameVersionDescriptor descriptor = new(
            rootId,
            "installed-version",
            "Installed Version",
            "release",
            null,
            new JavaRequirement("java-runtime-gamma", 17));

        var result = await new ListGameVersions(
            new FakeGameEngine([descriptor]),
            repository)
            .ExecuteAsync(
                new GameRoot(rootId, @"C:\Games\.minecraft", "Minecraft", GameRootAvailability.Available, null),
                LauncherSettings.Default,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        ListedGameVersion listed = Assert.Single(result.Value);
        Assert.Equal(descriptor.FolderName, listed.FolderName);
        Assert.DoesNotContain(result.Value, version => version.FolderName == stale.VersionFolder);
        Assert.Contains(repository.Stored, value => value == stale);
    }

    private sealed class FakeGameEngine(IReadOnlyList<GameVersionDescriptor> descriptors) : IGameEngine
    {
        public Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(
            string gameRootPath,
            CancellationToken cancellationToken)
        {
            Assert.Equal(@"C:\Games\.minecraft", gameRootPath);
            return Task.FromResult(Result<IReadOnlyList<GameVersionDescriptor>>.Success(descriptors));
        }
    }

    private sealed class FakeVersionOverrideRepository(IReadOnlyList<VersionOverride> overrides) : IVersionOverrideRepository
    {
        public IReadOnlyList<VersionOverride> Stored => overrides;

        public Task<IReadOnlyList<VersionOverride>> GetForGameRootAsync(
            string gameRootId,
            CancellationToken cancellationToken) =>
            Task.FromResult(overrides);

        public Task<Result<Unit>> UpsertAsync(
            VersionOverride versionOverride,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RemoveAsync(
            string gameRootId,
            string versionFolder,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RenameAsync(
            string gameRootId,
            string sourceFolder,
            string targetFolder,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
