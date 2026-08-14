using Lacertae.Desktop.ViewModels.Startup;
using Lacertae.Domain.Problems;

namespace Lacertae.Desktop.Tests.Startup;

public sealed class StartupViewModelTests
{
    [Fact]
    public void FailureSnapshotExposesSafeRecoveryDetailsWithoutReadingPaths()
    {
        Problem problem = new(
            "SETTINGS_CORRUPT",
            ProblemStage.Configuration,
            "problem.settings.invalid",
            true,
            "corr-startup",
            ["action.startup.retry", "action.settings.restore_backup", "action.startup.open_log"],
            new Dictionary<string, string>
            {
                ["safePath"] = "logs/startup.log",
                ["summary"] = "设置文件无法读取",
            });

        StartupViewModel viewModel = new(
            problem,
            new StartupProgressSnapshot("settings", 0.4),
            new FakeRecoveryHost());

        Assert.Equal("SETTINGS_CORRUPT", viewModel.ProblemCode);
        Assert.Equal("设置文件无法读取", viewModel.LocalizedSummary);
        Assert.Equal("logs/startup.log", viewModel.SafePath);
        Assert.True(viewModel.CanRetry);
        Assert.True(viewModel.CanRestore);
        Assert.True(viewModel.CanOpenLog);
        Assert.Equal("settings", viewModel.Progress.StageKey);
        Assert.Equal(0.4, viewModel.Progress.Fraction);
    }

    [Fact]
    public async Task RecoveryActionsAreDelegatedToTypedHost()
    {
        Problem problem = new(
            "STARTUP_FAILED",
            ProblemStage.Unknown,
            "problem.startup.failed",
            true,
            "corr-startup",
            ["action.startup.retry", "action.startup.open_log"]);
        FakeRecoveryHost host = new();
        StartupViewModel viewModel = new(problem, recoveryHost: host);

        await viewModel.RetryAsync(TestContext.Current.CancellationToken);
        await viewModel.OpenLogAsync(TestContext.Current.CancellationToken);

        Assert.True(host.RetryCalled);
        Assert.True(host.OpenLogCalled);
        Assert.False(viewModel.CanRestore);
    }

    [Fact]
    public void DefaultRecoveryHostDoesNotAdvertiseUnavailableActions()
    {
        Problem problem = new(
            "STARTUP_FAILED",
            ProblemStage.Unknown,
            "problem.unknown",
            true,
            "corr-startup",
            ["action.startup.retry", "action.settings.restore_backup", "action.startup.open_log"]);

        StartupViewModel viewModel = new(problem);

        Assert.False(viewModel.CanRetry);
        Assert.False(viewModel.CanRestore);
        Assert.False(viewModel.CanOpenLog);
        Assert.Equal("启动初始化失败，请查看问题代码和日志。", viewModel.LocalizedSummary);
    }

    [Fact]
    public void GenericStartupFailureRetainsSafeRelativeLogPath()
    {
        Problem problem = Lacertae.Desktop.CompositionRoot.CreateStartupFailureProblem();

        Assert.Equal("logs/lacertae-*.log", problem.SafeContext["safePath"]);
    }

    private sealed class FakeRecoveryHost : IStartupRecoveryHost
    {
        public bool CanRetry => true;
        public bool CanRestore => true;
        public bool CanOpenLog => true;
        public bool RetryCalled { get; private set; }
        public bool OpenLogCalled { get; private set; }

        public Task RetryAsync(CancellationToken cancellationToken)
        {
            RetryCalled = true;
            return Task.CompletedTask;
        }

        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OpenLogAsync(CancellationToken cancellationToken)
        {
            OpenLogCalled = true;
            return Task.CompletedTask;
        }
    }
}
