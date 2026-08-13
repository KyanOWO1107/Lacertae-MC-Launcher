using Lacertae.Domain.Common;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Operations;

public sealed class BackgroundOperationRunner
{
#pragma warning disable CA1822
    public async Task<Result<Unit>> RunAsync(
        IBackgroundOperation operation,
        IProgress<OperationSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(snapshots);

        snapshots.Report(new(operation.Id, operation.Kind, OperationState.Running, null, null));
        InlineProgress<OperationProgress> progress = new(value =>
            snapshots.Report(new(operation.Id, operation.Kind, OperationState.Running, value, null)));

        try
        {
            Result<Unit> result = await operation.ExecuteAsync(progress, cancellationToken);
            OperationState state = result.IsSuccess ? OperationState.Succeeded : OperationState.Failed;
            snapshots.Report(new(operation.Id, operation.Kind, state, null, result.Problem?.Code));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Problem problem = new(
                "OPERATION_CANCELLED",
                ProblemStage.Unknown,
                "problem.operation.cancelled",
                false,
                operation.Id,
                []);
            snapshots.Report(new(operation.Id, operation.Kind, OperationState.Cancelled, null, problem.Code));
            return Result.Failure(problem);
        }
    }
#pragma warning restore CA1822

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
