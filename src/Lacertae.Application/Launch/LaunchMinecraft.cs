using System.Collections.Concurrent;
using Lacertae.Application.Games;
using Lacertae.Domain.Common;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Launch;

public sealed class LaunchMinecraft
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LaunchLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILaunchPreflight preflight;
    private readonly IGameEngine gameEngine;
    private readonly IGameProcessHost processHost;
    private readonly ILauncherDispositionController? dispositionController;

    public LaunchMinecraft(
        ILaunchPreflight preflight,
        IGameEngine gameEngine,
        IGameProcessHost processHost,
        ILauncherDispositionController? dispositionController = null)
    {
        this.preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        this.gameEngine = gameEngine ?? throw new ArgumentNullException(nameof(gameEngine));
        this.processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
        this.dispositionController = dispositionController;
    }

    public async Task<Result<GameExitResult>> ExecuteAsync(
        LaunchPlan plan,
        IProgress<GameLogLine> log,
        IProgress<GameProcessState>? state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(log);
        IProgress<GameProcessState> stateProgress = state ?? new Progress<GameProcessState>(_ => { });
        string lockKey = CreateLockKey(plan);
        SemaphoreSlim launchLock = LaunchLocks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));
        await launchLock.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteLockedAsync(plan, log, stateProgress, cancellationToken);
        }
        finally
        {
            launchLock.Release();
        }
    }

    public Task<Result<GameExitResult>> ExecuteAsync(
        LaunchPlan plan,
        IProgress<GameLogLine> log,
        CancellationToken cancellationToken) =>
        ExecuteAsync(plan, log, null, cancellationToken);

    private async Task<Result<GameExitResult>> ExecuteLockedAsync(
        LaunchPlan plan,
        IProgress<GameLogLine> log,
        IProgress<GameProcessState> state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<LaunchPreflightResult> checkedPlan = await preflight.ExecuteAsync(plan, cancellationToken);
        if (!checkedPlan.IsSuccess)
        {
            return Result<GameExitResult>.Failure(checkedPlan.Problem!);
        }

        if (!checkedPlan.Value.IsReady)
        {
            return Result<GameExitResult>.Failure(PreflightProblem(plan, checkedPlan.Value));
        }

        Result<GameProcessSpec> specResult = await gameEngine.BuildProcessSpecAsync(plan, cancellationToken);
        if (!specResult.IsSuccess)
        {
            return Result<GameExitResult>.Failure(specResult.Problem!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        state.Report(GameProcessState.Starting);
        try
        {
            // The Windows host creates the process synchronously at the beginning
            // of RunAsync. Applying the disposition immediately before that call
            // keeps the launcher responsive without exposing process handles here.
            dispositionController?.Apply(plan.Disposition);
            state.Report(GameProcessState.Running);
            Result<GameExitResult> result = await processHost.RunAsync(specResult.Value, log, cancellationToken);
            if (result.IsSuccess)
            {
                state.Report(result.Value.State);
            }

            return result;
        }
        finally
        {
            dispositionController?.Restore();
        }
    }

    private static string CreateLockKey(LaunchPlan plan)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.GameRootPath));
        return root + "\u001f" + plan.VersionId;
    }

    private static Problem PreflightProblem(LaunchPlan plan, LaunchPreflightResult result) => new(
        result.MissingOrDamagedArtifactIds.Count > 0
            ? "LAUNCH_REQUIRED_FILES_INVALID"
            : result.FailureCodes.Count > 0
                ? result.FailureCodes[0]
                : "LAUNCH_PREFLIGHT_FAILED",
        ProblemStage.LaunchPlanning,
        "problem.launch.preflight_failed",
        false,
        plan.CorrelationId,
        result.SuggestedActionKeys.Count == 0
            ? ["action.launch.review_settings"]
            : result.SuggestedActionKeys);
}
