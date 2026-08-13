using Lacertae.Application.Startup;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Storage;

namespace Lacertae.Application.Tests.Startup;

public sealed class StartupCoordinatorTests
{
    [Fact]
    public async Task InitializeAsyncRunsStorageStepsInStrictOrder()
    {
        List<string> events = [];
        FakeStartupStorage storage = new(events);
        FakeDataRootResolver resolver = new(events);
        FakeLoggingInitializer logging = new(events);
        StartupCoordinator coordinator = new(resolver, logging, new FakeStorageFactory(storage));

        var result = await coordinator.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(["resolve", "logging", "settings", "migrate", "recover", "roots"], events);
        Assert.Equal(DataRootMode.UserProfile, result.Value.DataRoot.Mode);
        Assert.Equal(LauncherSettings.Default, result.Value.Settings);
        Assert.Single(result.Value.GameRoots);
    }

    [Fact]
    public async Task InitializeAsyncStopsAfterFirstFailureAndReturnsOriginalProblem()
    {
        List<string> events = [];
        FakeStartupStorage storage = new(events) { FailureStep = "migrate" };
        StartupCoordinator coordinator = new(
            new FakeDataRootResolver(events),
            new FakeLoggingInitializer(events),
            new FakeStorageFactory(storage));

        var result = await coordinator.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("TEST_MIGRATE_FAILED", result.Problem?.Code);
        Assert.Equal(["resolve", "logging", "settings", "migrate"], events);
    }

    [Fact]
    public async Task InitializeAsyncStopsBeforeLoggingWhenDataRootFails()
    {
        List<string> events = [];
        FakeDataRootResolver resolver = new(events) { Fail = true };
        FakeLoggingInitializer logging = new(events);
        FakeStartupStorage storage = new(events);
        StartupCoordinator coordinator = new(resolver, logging, new FakeStorageFactory(storage));

        var result = await coordinator.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("TEST_ROOT_FAILED", result.Problem?.Code);
        Assert.False(logging.WasInitialized);
        Assert.Equal(["resolve"], events);
    }

    private sealed class FakeDataRootResolver(List<string> events) : IStartupDataRootResolver
    {
        public bool Fail { get; set; }

        public Result<DataRoot> Resolve()
        {
            events.Add("resolve");
            return Fail
                ? Result<DataRoot>.Failure(Problem("TEST_ROOT_FAILED"))
                : Result<DataRoot>.Success(new DataRoot(DataRootMode.UserProfile, @"C:\Roaming\Lacertae", @"C:\Local\Lacertae"));
        }
    }

    private sealed class FakeLoggingInitializer(List<string> events) : IStartupLoggingInitializer
    {
        public bool WasInitialized { get; private set; }

        public Result<Unit> Initialize(DataRoot dataRoot)
        {
            events.Add("logging");
            WasInitialized = true;
            return Result.Success();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeStorageFactory(FakeStartupStorage storage) : IStartupStorageFactory
    {
        public IStartupStorage Create(DataRoot dataRoot) => storage;
    }

    private sealed class FakeStartupStorage(List<string> events) : IStartupStorage
    {
        public string? FailureStep { get; set; }

        public Task<Result<LauncherSettings>> LoadSettingsAsync(CancellationToken cancellationToken)
        {
            events.Add("settings");
            return Task.FromResult(FailureStep == "settings"
                ? Result<LauncherSettings>.Failure(Problem("TEST_SETTINGS_FAILED"))
                : Result<LauncherSettings>.Success(LauncherSettings.Default));
        }

        public Task<Result<Unit>> MigrateDatabaseAsync(CancellationToken cancellationToken)
        {
            events.Add("migrate");
            return Task.FromResult(FailureStep == "migrate"
                ? Result.Failure(Problem("TEST_MIGRATE_FAILED"))
                : Result.Success());
        }

        public Task<Result<Unit>> RecoverVersionRenameAsync(CancellationToken cancellationToken)
        {
            events.Add("recover");
            return Task.FromResult(FailureStep == "recover"
                ? Result.Failure(Problem("TEST_RECOVER_FAILED"))
                : Result.Success());
        }

        public Task<Result<IReadOnlyList<GameRoot>>> RefreshGameRootsAsync(CancellationToken cancellationToken)
        {
            events.Add("roots");
            IReadOnlyList<GameRoot> roots = [new("root-1", @"C:\Games\.minecraft", "Minecraft", GameRootAvailability.Available, null)];
            return Task.FromResult(FailureStep == "roots"
                ? Result<IReadOnlyList<GameRoot>>.Failure(Problem("TEST_ROOTS_FAILED"))
                : Result<IReadOnlyList<GameRoot>>.Success(roots));
        }
    }

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.Storage,
        "problem.test.startup",
        false,
        "startup-test",
        []);
}
