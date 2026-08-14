using Lacertae.Application.Games;
using Lacertae.Application.Launch;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Java;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;
using Lacertae.Testing.Launch;

namespace Lacertae.Application.Tests.Launch;

public sealed class LaunchMinecraftTests
{
    [Fact]
    public async Task ExecuteAsyncPreflightsBuildsStartsAndRestoresDisposition()
    {
        LaunchPlan plan = CreatePlan("root", "version");
        FakePreflight preflight = new();
        FakeGameEngine engine = new();
        FakeGameProcessHost host = new();
        FakeDispositionController disposition = new();
        List<GameProcessState> states = [];

        var result = await new LaunchMinecraft(preflight, engine, host, disposition).ExecuteAsync(
            plan,
            new InlineProgress<GameLogLine>(_ => { }),
            new InlineProgress<GameProcessState>(states.Add),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(GameProcessState.Exited, result.Value.State);
        Assert.Equal([GameProcessState.Starting, GameProcessState.Running, GameProcessState.Exited], states);
        Assert.Single(host.Started);
        Assert.Equal([LaunchDisposition.KeepLauncherOpen], disposition.Applied);
        Assert.Equal(1, disposition.RestoreCount);
        Assert.True(engine.BuildCalled);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotStartWhenPreflightIsNotReady()
    {
        LaunchPlan plan = CreatePlan("root", "version");
        FakePreflight preflight = new() { Result = new LaunchPreflightResult(false, ["artifact"], ["LAUNCH_REQUIRED_FILES_INVALID"], ["action.version.repair"], 0) };
        FakeGameProcessHost host = new();

        var result = await new LaunchMinecraft(preflight, new FakeGameEngine(), host).ExecuteAsync(
            plan,
            new InlineProgress<GameLogLine>(_ => { }),
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("LAUNCH_REQUIRED_FILES_INVALID", result.Problem?.Code);
        Assert.Empty(host.Started);
    }

    [Fact]
    public async Task ExecuteAsyncSerializesConcurrentLaunchesForOneVersion()
    {
        LaunchPlan plan = CreatePlan("root", "version");
        FakeGameProcessHost host = new() { WaitForCompletion = true };
        LaunchMinecraft launch = new(new FakePreflight(), new FakeGameEngine(), host);

        Task<Result<GameExitResult>> first = launch.ExecuteAsync(plan, new InlineProgress<GameLogLine>(_ => { }), null, TestContext.Current.CancellationToken);
        while (host.Started.Count != 1)
        {
            await Task.Yield();
        }

        Task<Result<GameExitResult>> second = launch.ExecuteAsync(plan, new InlineProgress<GameLogLine>(_ => { }), null, TestContext.Current.CancellationToken);
        await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.Single(host.Started);

        host.Complete(new GameExitResult(1234, 0, GameProcessState.Exited, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, plan.CorrelationId));
        Assert.True((await first).IsSuccess);
        Assert.True((await second).IsSuccess);
        Assert.Equal(2, host.Started.Count);
    }

    [Fact]
    public async Task ExecuteAsyncHonorsCancellationBeforeProcessCreation()
    {
        LaunchPlan plan = CreatePlan("root", "cancelled");
        FakeGameProcessHost host = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LaunchMinecraft(
                new FakePreflight(),
                new FakeGameEngine(),
                host)
            .ExecuteAsync(plan, new InlineProgress<GameLogLine>(_ => { }), null, cancellation.Token));
        Assert.Empty(host.Started);
    }

    private static LaunchPlan CreatePlan(string rootId, string version) => new(
        "corr-" + version,
        rootId,
        version,
        version,
        Path.Combine(Path.GetTempPath(), "lacertae-launch-root"),
        Path.Combine(Path.GetTempPath(), "lacertae-launch-root"),
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
        [],
        [],
        [],
        [],
        LaunchDisposition.KeepLauncherOpen,
        DateTimeOffset.UtcNow);

    private sealed class FakePreflight : ILaunchPreflight
    {
        public LaunchPreflightResult Result { get; set; } = LaunchPreflightResult.Ready(1024L * 1024 * 1024);

        public Task<Result<LaunchPreflightResult>> ExecuteAsync(LaunchPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(Result<LaunchPreflightResult>.Success(Result));
    }

    private sealed class FakeGameEngine : IGameEngine
    {
        public bool BuildCalled { get; private set; }

        public Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(string gameRootPath, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<GameVersionDescriptor>>.Success([]));

        public Task<Result<GameProcessSpec>> BuildProcessSpecAsync(LaunchPlan plan, CancellationToken cancellationToken)
        {
            BuildCalled = true;
            return Task.FromResult(Result<GameProcessSpec>.Success(new GameProcessSpec(
                plan.JavaExecutablePath,
                [],
                plan.GameDirectory,
                new Dictionary<string, SensitiveString>(StringComparer.Ordinal),
                plan.CorrelationId)));
        }
    }

    private sealed class FakeDispositionController : ILauncherDispositionController
    {
        public List<LaunchDisposition> Applied { get; } = [];
        public int RestoreCount { get; private set; }
        public void Apply(LaunchDisposition disposition) => Applied.Add(disposition);
        public void Restore() => RestoreCount++;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
