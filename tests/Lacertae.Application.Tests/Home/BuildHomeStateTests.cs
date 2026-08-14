using Lacertae.Application.Accounts;
using Lacertae.Application.GameRoots;
using Lacertae.Application.Games;
using Lacertae.Application.Home;
using Lacertae.Application.Java;
using Lacertae.Application.SystemInfo;
using Lacertae.Application.Versions;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Home;
using Lacertae.Domain.Java;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Tests.Home;

public sealed class BuildHomeStateTests
{
    [Fact]
    public async Task ExecuteAsyncUsesSelectedVersionAccountAndJavaWithoutExposingSecrets()
    {
        string rootPath = CreateTemporaryDirectory();
        GameRoot root = new("root-1", rootPath, "主游戏", GameRootAvailability.Available, null);
        GameVersionDescriptor descriptor = new(
            "root-1",
            "1.21.1",
            "Minecraft 1.21.1",
            "release",
            null,
            new JavaRequirement("java-runtime", 21));
        Account account = new(
            "account-1",
            new AccountIdentity(AccountIdentity.OfflineProviderId, "5627dd98-e6be-3c21-b8a8-e92344183641"),
            AccountType.Offline,
            "Alex",
            null,
            "secret-ref-only",
            AccountStatus.Active,
            null);
        JavaInstallation installation = new(
            "java-21",
            Path.Combine(rootPath, "java", "bin", "java.exe"),
            21,
            "21.0.1",
            "Fixture",
            JavaArchitecture.X64,
            JavaSource.Managed,
            true);
        LauncherSettings settings = LauncherSettings.Default with
        {
            SelectedGameRootId = root.Id,
            SelectedVersionFolder = descriptor.FolderName,
            DefaultAccountId = account.Id,
            GlobalJavaPath = installation.ExecutablePath,
        };

        FakeJavaDiscovery javaDiscovery = new(new JavaDiscoveryResult([installation], []));
        BuildHomeState useCase = new(
            new FakeRootRepository(root),
            new FakeAccountRepository(account),
            new ListGameVersions(new FakeGameEngine(descriptor), new FakeVersionOverrideRepository()),
            javaDiscovery,
            new FakeMemoryInfo(new MemorySnapshot(16UL * 1024 * 1024 * 1024, 8UL * 1024 * 1024 * 1024)));

        Result<HomeState> result = await useCase.ExecuteAsync(
            settings,
            [new OperationSnapshot("task-1", "install", OperationState.Running, null, null)],
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.True(result.Value.LaunchCard.CanLaunch);
        Assert.Equal("Minecraft 1.21.1", result.Value.LaunchCard.VersionDisplayName);
        Assert.Equal("Alex", result.Value.LaunchCard.AccountPlayerName);
        Assert.Equal("Java 21 · Fixture", result.Value.LaunchCard.JavaSummary);
        Assert.Equal(2048, result.Value.LaunchCard.MaximumMemoryMb);
        Assert.Equal([HomeModuleId.RecentVersions, HomeModuleId.ActiveTasks, HomeModuleId.QuickActions, HomeModuleId.ReleaseNotes], result.Value.Modules.Select(static module => module.Module));
        Assert.Single(result.Value.ActiveTasks);
        Assert.Contains(result.Value.QuickActions, action => action.Id == HomeQuickActionId.OpenSaves);
        Assert.DoesNotContain("secret-ref-only", result.Value.ToString(), StringComparison.Ordinal);
        Assert.Contains(javaDiscovery.AdditionalCandidates, candidate => candidate.ExecutablePath == installation.ExecutablePath);
    }

    [Fact]
    public async Task ExecuteAsyncReportsOnlyTheHighestPriorityMissingRequirement()
    {
        string rootPath = CreateTemporaryDirectory();
        LauncherSettings settings = LauncherSettings.Default with
        {
            SelectedGameRootId = "root-1",
            SelectedVersionFolder = "missing",
            DefaultAccountId = "missing-account",
        };
        BuildHomeState useCase = new(
            new FakeRootRepository(new GameRoot("root-1", rootPath, "主游戏", GameRootAvailability.Available, null)),
            new FakeAccountRepository(null),
            new ListGameVersions(new FakeGameEngine(), new FakeVersionOverrideRepository()),
            new FakeJavaDiscovery(new JavaDiscoveryResult([], [])),
            new FakeMemoryInfo(new MemorySnapshot(8UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024)));

        Result<HomeState> result = await useCase.ExecuteAsync(settings, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.False(result.Value.LaunchCard.CanLaunch);
        HomeLaunchRequirement requirement = Assert.Single(result.Value.LaunchCard.Requirements);
        Assert.Equal(HomeLaunchRequirementId.Version, requirement.Id);
        Assert.False(string.IsNullOrWhiteSpace(requirement.ActionableReason));
        Assert.Equal(HomeRouteIds.Versions, requirement.RouteId);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotAddStaleVersionReasonWhenRootIsMissing()
    {
        BuildHomeState useCase = new(
            new FakeRootRepository(null),
            new FakeAccountRepository(null),
            new ListGameVersions(new FakeGameEngine(), new FakeVersionOverrideRepository()),
            new FakeJavaDiscovery(new JavaDiscoveryResult([], [])),
            new FakeMemoryInfo(new MemorySnapshot(8UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024)));

        Result<HomeState> result = await useCase.ExecuteAsync(
            LauncherSettings.Default with { SelectedVersionFolder = "stale" },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        HomeLaunchRequirement requirement = Assert.Single(result.Value.LaunchCard.Requirements);
        Assert.Equal(HomeLaunchRequirementId.Root, requirement.Id);
    }

    [Fact]
    public async Task ExecuteAsyncReportsDamagedFilesAsRepairPreviewRequirement()
    {
        string rootPath = CreateTemporaryDirectory();
        GameRoot root = new("root-1", rootPath, "主游戏", GameRootAvailability.Available, null);
        GameVersionDescriptor descriptor = new(
            "root-1",
            "1.21.1",
            "Minecraft 1.21.1",
            "release",
            null,
            new JavaRequirement("java-runtime", 21));
        Account account = new(
            "account-1",
            new AccountIdentity(AccountIdentity.OfflineProviderId, "5627dd98-e6be-3c21-b8a8-e92344183641"),
            AccountType.Offline,
            "Alex",
            null,
            null,
            AccountStatus.Active,
            null);
        JavaInstallation installation = new(
            "java-21",
            Path.Combine(rootPath, "java", "bin", "java.exe"),
            21,
            "21.0.1",
            "Fixture",
            JavaArchitecture.X64,
            JavaSource.Managed,
            true);
        LauncherSettings settings = LauncherSettings.Default with
        {
            SelectedGameRootId = root.Id,
            SelectedVersionFolder = descriptor.FolderName,
            DefaultAccountId = account.Id,
        };
        BuildHomeState useCase = new(
            new FakeRootRepository(root),
            new FakeAccountRepository(account),
            new ListGameVersions(new FakeGameEngine(descriptor), new FakeVersionOverrideRepository()),
            new FakeJavaDiscovery(new JavaDiscoveryResult([installation], [])),
            new FakeMemoryInfo(new MemorySnapshot(16UL * 1024 * 1024 * 1024, 8UL * 1024 * 1024 * 1024)));

        Result<HomeState> result = await useCase.ExecuteAsync(
            settings,
            hasDamagedFiles: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.False(result.Value.LaunchCard.CanLaunch);
        Assert.True(result.Value.LaunchCard.HasDamagedFiles);
        HomeLaunchRequirement requirement = Assert.Single(result.Value.LaunchCard.Requirements);
        Assert.Equal(HomeLaunchRequirementId.Files, requirement.Id);
        Assert.True(requirement.IsRepairPreview);
        Assert.Equal(HomeRouteIds.Downloads, requirement.RouteId);
    }

    [Fact]
    public async Task ExecuteAsyncConvertsModuleFailureToErrorCard()
    {
        BuildHomeState useCase = new(
            new FakeRootRepository(null),
            new FakeAccountRepository(null),
            new ListGameVersions(new FakeGameEngine(), new FakeVersionOverrideRepository()),
            new FakeJavaDiscovery(new JavaDiscoveryResult([], [])),
            new FakeMemoryInfo(new MemorySnapshot(8UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024)));

        Result<HomeState> result = await useCase.ExecuteAsync(
            LauncherSettings.Default,
            [new OperationSnapshot("", "install", OperationState.Running, null, null)],
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        HomeModuleState activeTasks = Assert.Single(
            result.Value.Modules,
            module => module.Module == HomeModuleId.ActiveTasks);
        Assert.True(activeTasks.HasError);
        Assert.Equal("HOME_MODULE_UNAVAILABLE", activeTasks.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsyncSurfacesActiveTaskStoreFailureAsModuleError()
    {
        BuildHomeState useCase = new(
            new FakeRootRepository(null),
            new FakeAccountRepository(null),
            new ListGameVersions(new FakeGameEngine(), new FakeVersionOverrideRepository()),
            new FakeJavaDiscovery(new JavaDiscoveryResult([], [])),
            new FakeMemoryInfo(new MemorySnapshot(8UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024)));

        Result<HomeState> result = await useCase.ExecuteAsync(
            LauncherSettings.Default,
            activeTasksReadFailed: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        HomeModuleState activeTasks = Assert.Single(
            result.Value.Modules,
            module => module.Module == HomeModuleId.ActiveTasks);
        Assert.True(activeTasks.HasError);
        Assert.Equal("HOME_MODULE_UNAVAILABLE", activeTasks.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsUnknownDuplicateOrMissingHomeModules()
    {
        LauncherSettings settings = LauncherSettings.Default with
        {
            HomeModules =
            [
                new HomeModulePlacement(HomeModuleId.RecentVersions, 0, true),
                new HomeModulePlacement((HomeModuleId)99, 1, true),
                new HomeModulePlacement(HomeModuleId.QuickActions, 1, true),
                new HomeModulePlacement(HomeModuleId.ReleaseNotes, 2, true),
            ],
        };
        BuildHomeState useCase = new(
            new FakeRootRepository(null),
            new FakeAccountRepository(null),
            new ListGameVersions(new FakeGameEngine(), new FakeVersionOverrideRepository()),
            new FakeJavaDiscovery(new JavaDiscoveryResult([], [])),
            new FakeMemoryInfo(new MemorySnapshot(8UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024)));

        Result<HomeState> result = await useCase.ExecuteAsync(settings, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("SETTINGS_CORRUPT", result.Problem?.Code);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lacertae-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeRootRepository(GameRoot? root) : IGameRootRepository
    {
        public Task<IReadOnlyList<GameRoot>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GameRoot>>(root is null ? [] : [root]);

        public Task<GameRoot?> FindByNormalizedPathAsync(string normalizedPath, CancellationToken cancellationToken) =>
            Task.FromResult(root);

        public Task<Result<Unit>> UpsertAsync(GameRoot gameRoot, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RemoveAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeAccountRepository(Account? account) : IAccountRepository
    {
        public Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Account>>(account is null ? [] : [account]);

        public Task<Account?> GetAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(account is not null && account.Id == accountId ? account : null);

        public Task<Account?> FindByIdentityAsync(AccountIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult(account);

        public Task<Result<Unit>> UpsertAsync(Account value, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> SetStatusAsync(string accountId, AccountStatus status, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> DeleteAndClearVersionReferencesAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeGameEngine(params GameVersionDescriptor[] descriptors) : IGameEngine
    {
        public Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(
            string gameRootPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<GameVersionDescriptor>>.Success(descriptors));
    }

    private sealed class FakeVersionOverrideRepository : IVersionOverrideRepository
    {
        public Task<IReadOnlyList<VersionOverride>> GetForGameRootAsync(string gameRootId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VersionOverride>>([]);

        public Task<Result<Unit>> UpsertAsync(VersionOverride versionOverride, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RemoveAsync(string gameRootId, string versionFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RenameAsync(string gameRootId, string sourceFolder, string targetFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeJavaDiscovery(JavaDiscoveryResult result) : IJavaDiscovery, IJavaDiscoveryWithCandidates
    {
        public IReadOnlyList<JavaCandidate> AdditionalCandidates { get; private set; } = [];

        public Task<Result<JavaDiscoveryResult>> ExecuteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<JavaDiscoveryResult>.Success(result));

        public Task<Result<JavaDiscoveryResult>> ExecuteAsync(
            IReadOnlyList<JavaCandidate> additionalCandidates,
            CancellationToken cancellationToken)
        {
            AdditionalCandidates = additionalCandidates;
            return Task.FromResult(Result<JavaDiscoveryResult>.Success(result));
        }
    }

    private sealed class FakeMemoryInfo(MemorySnapshot snapshot) : IMemoryInfo
    {
        public MemorySnapshot GetSnapshot() => snapshot;
    }
}
