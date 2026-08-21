using System.Globalization;
using System.Text.Json;
using Lacertae.Application.Operations;
using Lacertae.Domain.Common;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Launch;

public sealed class LaunchMinecraftOperation : IBackgroundOperation
{
    private readonly LaunchMinecraft launcher;
    private readonly LaunchPlan plan;
    private readonly IBackgroundTaskStore taskStore;
    private readonly TimeProvider timeProvider;
    private readonly string frozenPlanJson;

    public LaunchMinecraftOperation(
        LaunchMinecraft launcher,
        LaunchPlan plan,
        IBackgroundTaskStore store,
        TimeProvider? timeProvider = null)
    {
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        taskStore = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        try
        {
            // Durable task records are used for progress/recovery bookkeeping only.
            // Persist the deliberately secret-free summary instead of the complete
            // launch plan, which may contain user-supplied JVM/game arguments.
            frozenPlanJson = JsonSerializer.Serialize(LaunchSummary.From(plan), OperationSerialization.JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Launch plan cannot be serialized safely.", nameof(plan), exception);
        }
    }

    public string Id => plan.CorrelationId;

    public string CorrelationId => plan.CorrelationId;

    public string Kind => "minecraft-launch";

    public async Task<Result<Unit>> ExecuteAsync(
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        MonotonicOperationProgress normalizedProgress = new(progress);

        Result<Unit> saved = await SaveAsync(
            OperationState.Running,
            null,
            null,
            cancellationToken);
        if (!saved.IsSuccess)
        {
            return saved;
        }

        bool processStarted = false;
        try
        {
            ReportStage(normalizedProgress, "auth");
            ReportStage(normalizedProgress, "java");
            normalizedProgress.Report(new OperationProgress("launch", 0, 1, 0, 0));

            Result<GameExitResult> launched = await launcher.ExecuteAsync(
                plan,
                new InlineProgress<GameLogLine>(_ => { }),
                new InlineProgress<GameProcessState>(state =>
                {
                    if (state == GameProcessState.Starting)
                    {
                        processStarted = true;
                        normalizedProgress.Report(new OperationProgress("launch", 1, 1, 0, 0));
                    }
                    else if (state == GameProcessState.Running)
                    {
                        processStarted = true;
                        ReportStage(normalizedProgress, "running");
                    }
                }),
                cancellationToken);

            Result<Unit> mapped = MapExitResult(launched);
            if (!mapped.IsSuccess)
            {
                Result<Unit> persistedFailure = await SaveAsync(
                    OperationState.Failed,
                    null,
                    mapped.Problem?.Code,
                    CancellationToken.None);
                return persistedFailure.IsSuccess ? mapped : persistedFailure;
            }

            if (!processStarted)
            {
                normalizedProgress.Report(new OperationProgress("launch", 1, 1, 0, 0));
            }

            ReportStage(normalizedProgress, "running");
            Result<Unit> persistedSuccess = await SaveAsync(
                OperationState.Succeeded,
                null,
                null,
                CancellationToken.None);
            return persistedSuccess;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = await SaveAsync(
                OperationState.Cancelled,
                null,
                "OPERATION_CANCELLED",
                CancellationToken.None);
            throw;
        }
    }

    private async Task<Result<Unit>> SaveAsync(
        OperationState state,
        string? journalJson,
        string? problemCode,
        CancellationToken cancellationToken) =>
        await taskStore.SaveAsync(
            new BackgroundTaskRecord(
                Id,
                Kind,
                state,
                frozenPlanJson,
                journalJson,
                problemCode,
                timeProvider.GetUtcNow()),
            cancellationToken);

    private Result<Unit> MapExitResult(Result<GameExitResult> launched)
    {
        if (!launched.IsSuccess)
        {
            return Result<Unit>.Failure(launched.Problem!);
        }

        GameExitResult exit = launched.Value;
        return exit.State switch
        {
            GameProcessState.Exited when exit.ExitCode == 0 => Result.Success(),
            GameProcessState.UserTerminated => Result.Success(),
            GameProcessState.Exited when exit.ExitCode is int exitCode =>
                Result<Unit>.Failure(new Problem(
                    "GAME_ABNORMAL_EXIT",
                    ProblemStage.Process,
                    "problem.process.abnormal_exit",
                    false,
                    CorrelationId,
                    ["action.diagnostics.open_log"],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["exitCode"] = exitCode.ToString(CultureInfo.InvariantCulture),
                    })),
            GameProcessState.Exited => Result<Unit>.Failure(ProcessProblem(
                "PROCESS_EXIT_STATE_UNAVAILABLE",
                "problem.process.exit_state_unavailable")),
            GameProcessState.StartFailed => Result<Unit>.Failure(ProcessProblem(
                "PROCESS_START_FAILED",
                "problem.process.start_failed")),
            _ => Result<Unit>.Failure(ProcessProblem(
                "PROCESS_EXIT_STATE_INVALID",
                "problem.process.exit_state_unavailable")),
        };
    }

    private static void ReportStage(MonotonicOperationProgress progress, string stage)
    {
        progress.Report(new OperationProgress(stage, 0, 1, 0, 0));
        progress.Report(new OperationProgress(stage, 1, 1, 0, 0));
    }

    private Problem ProcessProblem(string code, string messageKey) => new(
        code,
        ProblemStage.Process,
        messageKey,
        false,
        CorrelationId,
        ["action.launch.review_result"]);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
