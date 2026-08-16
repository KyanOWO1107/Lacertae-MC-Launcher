using Lacertae.Application.Games;
using Lacertae.Application.Launch;
using Lacertae.Application.Operations;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Java;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;
using Lacertae.Testing.Launch;

namespace Lacertae.Application.Tests.Launch;

public sealed class LaunchMinecraftOperationTests
{
    [Fact]
    public async Task ExecuteAsyncReportsAuthJavaLaunchAndRunningStages()
    {
        LaunchPlan plan = CreatePlan();
        FakeGameProcessHost host = new();
        LaunchMinecraft launcher = new(new ReadyPreflight(), new FakeEngine(), host);
        LaunchMinecraftOperation operation = new(launcher, plan, new FakeTaskStore());
        List<OperationProgress> progress = [];

        Result<Unit> result = await operation.ExecuteAsync(new InlineProgress<OperationProgress>(progress.Add), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(["auth", "java", "launch", "running"], progress.Select(static item => item.Stage).Distinct().ToArray());
        Assert.Equal(operation.CorrelationId, host.Started.Single().CorrelationId);
    }

    [Fact]
    public async Task ExecuteAsyncPersistsOnlySecretFreeLaunchSummary()
    {
        LaunchPlan plan = CreatePlan(
            userJvmArguments: ["-DauthToken=do-not-persist"],
            gameArguments: ["--access-token", "do-not-persist"]);
        FakeTaskStore store = new();
        LaunchMinecraftOperation operation = new(
            new LaunchMinecraft(new ReadyPreflight(), new FakeEngine(), new FakeGameProcessHost()),
            plan,
            store);

        Result<Unit> result = await operation.ExecuteAsync(
            new InlineProgress<OperationProgress>(_ => { }),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.NotEmpty(store.Records);
        Assert.DoesNotContain("do-not-persist", store.Records[0].FrozenPlanJson, StringComparison.Ordinal);
        Assert.Contains(plan.VersionFolder, store.Records[0].FrozenPlanJson, StringComparison.Ordinal);
    }

    private static LaunchPlan CreatePlan(
        IReadOnlyList<string>? userJvmArguments = null,
        IReadOnlyList<string>? gameArguments = null) => new(
        "corr-op",
        "root-1",
        "fixture",
        "fixture",
        Path.GetTempPath(),
        Path.GetTempPath(),
        "java-17",
        Environment.ProcessPath!,
        17,
        "account-1",
        AccountType.Offline,
        "Player",
        "5627dd98-e6be-3c21-b8a8-e92344183641",
        new AuthSession("Player", "5627dd98-e6be-3c21-b8a8-e92344183641", new SensitiveString("token"), "legacy", null, null),
        1024,
        2048,
        userJvmArguments ?? [],
        gameArguments ?? [],
        [],
        [],
        LaunchDisposition.KeepLauncherOpen,
        DateTimeOffset.UtcNow);

    private sealed class ReadyPreflight : ILaunchPreflight
    {
        public Task<Result<LaunchPreflightResult>> ExecuteAsync(LaunchPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(Result<LaunchPreflightResult>.Success(LaunchPreflightResult.Ready(long.MaxValue)));
    }

    private sealed class FakeEngine : IGameEngine
    {
        public Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(string gameRootPath, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<GameVersionDescriptor>>.Success([]));
        public Task<Result<GameProcessSpec>> BuildProcessSpecAsync(LaunchPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(Result<GameProcessSpec>.Success(new GameProcessSpec(plan.JavaExecutablePath, [], plan.GameDirectory, new Dictionary<string, SensitiveString>(), plan.CorrelationId)));
    }

    private sealed class FakeTaskStore : IBackgroundTaskStore
    {
        public List<BackgroundTaskRecord> Records { get; } = [];

        public Task<Result<IReadOnlyList<OperationSnapshot>>> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<OperationSnapshot>>.Success([]));

        public Task<Result<Unit>> SaveAsync(BackgroundTaskRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
