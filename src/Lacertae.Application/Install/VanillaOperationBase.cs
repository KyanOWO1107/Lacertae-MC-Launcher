using System.Text.Json;
using Lacertae.Application.Operations;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Install;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Install;

public abstract class VanillaOperationBase : IBackgroundOperation
{
    private readonly PlanVanillaInstall planner;
    private readonly ExecuteVanillaInstall executor;
    private readonly GameRoot gameRoot;
    private readonly string versionId;
    private readonly VanillaPlatform platform;
    private readonly IBackgroundTaskStore taskStore;
    private readonly TimeProvider timeProvider;

    protected VanillaOperationBase(
        PlanVanillaInstall planner,
        ExecuteVanillaInstall executor,
        GameRoot gameRoot,
        string versionId,
        VanillaPlatform platform,
        InstallAction action,
        string kind,
        IBackgroundTaskStore taskStore,
        TimeProvider? timeProvider = null)
    {
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.gameRoot = gameRoot ?? throw new ArgumentNullException(nameof(gameRoot));
        this.versionId = string.IsNullOrWhiteSpace(versionId)
            ? throw new ArgumentException("Version ID cannot be blank.", nameof(versionId))
            : versionId;
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        Action = action;
        Kind = string.IsNullOrWhiteSpace(kind)
            ? throw new ArgumentException("Operation kind cannot be blank.", nameof(kind))
            : kind;
        this.taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Id = Guid.NewGuid().ToString("N");
    }

    public string Id { get; }

    public string Kind { get; }

    public string CorrelationId => Id;

    public InstallAction Action { get; }

    public Task<Result<Unit>> ExecuteAsync(
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(progress, cancellationToken);

    private async Task<Result<Unit>> ExecuteCoreAsync(
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        MonotonicOperationProgress normalizedProgress = new(progress);
        normalizedProgress.Report(new OperationProgress("metadata", 0, 1, 0, 0));

        Result<VanillaInstallPlan> planned = await planner.ExecuteAsync(
            gameRoot,
            versionId,
            Action,
            platform,
            cancellationToken);
        if (!planned.IsSuccess)
        {
            return Result<Unit>.Failure(planned.Problem!);
        }

        // The planner owns metadata resolution. The operation owns the stable
        // durable identity used by staging, journals and UI correlation.
        VanillaInstallPlan plan = planned.Value with { OperationId = Id };
        string frozenPlanJson;
        try
        {
            frozenPlanJson = JsonSerializer.Serialize(plan, OperationSerialization.JsonOptions);
        }
        catch (JsonException)
        {
            return Result<Unit>.Failure(Problem(
                "BACKGROUND_TASK_PLAN_SERIALIZATION_FAILED",
                "problem.background_task.plan_serialization_failed"));
        }

        normalizedProgress.Report(new OperationProgress(
            "metadata",
            1,
            1,
            0,
            plan.RequiredDownloadBytes));
        Result<Unit> saved = await SaveAsync(
            frozenPlanJson,
            OperationState.Running,
            null,
            null,
            cancellationToken);
        if (!saved.IsSuccess)
        {
            return saved;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            normalizedProgress.Report(new OperationProgress(
                "preflight",
                0,
                1,
                0,
                plan.RequiredWorkingBytes));
            cancellationToken.ThrowIfCancellationRequested();
            normalizedProgress.Report(new OperationProgress(
                "preflight",
                1,
                1,
                0,
                plan.RequiredWorkingBytes));

            Result<Unit> executed = await executor.ExecuteAsync(
                plan,
                normalizedProgress,
                cancellationToken);
            if (!executed.IsSuccess)
            {
                Result<Unit> persistedFailure = await SaveAsync(
                    frozenPlanJson,
                    OperationState.Failed,
                    null,
                    executed.Problem?.Code,
                    CancellationToken.None);
                return persistedFailure.IsSuccess ? executed : persistedFailure;
            }

            normalizedProgress.Report(new OperationProgress(
                "commit",
                1,
                1,
                plan.RequiredDownloadBytes,
                plan.RequiredWorkingBytes));
            Result<Unit> persistedSuccess = await SaveAsync(
                frozenPlanJson,
                OperationState.Succeeded,
                null,
                null,
                CancellationToken.None);
            return persistedSuccess;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = await SaveAsync(
                frozenPlanJson,
                OperationState.Cancelled,
                null,
                "OPERATION_CANCELLED",
                CancellationToken.None);
            throw;
        }
    }

    private Task<Result<Unit>> SaveAsync(
        string frozenPlanJson,
        OperationState state,
        string? journalJson,
        string? problemCode,
        CancellationToken cancellationToken) =>
        taskStore.SaveAsync(
            new BackgroundTaskRecord(
                Id,
                Kind,
                state,
                frozenPlanJson,
                journalJson,
                problemCode,
                timeProvider.GetUtcNow()),
            cancellationToken);

    private Problem Problem(string code, string messageKey) => new(
        code,
        ProblemStage.Storage,
        messageKey,
        false,
        Id,
        ["action.background_task.retry"]);
}
