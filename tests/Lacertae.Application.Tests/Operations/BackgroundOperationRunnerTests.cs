using Lacertae.Application.Operations;
using Lacertae.Domain.Common;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Operations;

public sealed class BackgroundOperationRunnerTests
{
    [Fact]
    public async Task RunAsyncPublishesRunningProgressAndSuccess()
    {
        List<OperationSnapshot> snapshots = [];
        FakeOperation operation = new(async (progress, _) =>
        {
            progress.Report(new OperationProgress("download", 2, 4, 128, 256));
            await Task.Yield();
            return Result.Success();
        });

        BackgroundOperationRunner runner = new();
        Result<Unit> result = await runner.RunAsync(
            operation,
            new InlineProgress<OperationSnapshot>(snapshots.Add),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains(snapshots, snapshot => snapshot.State == OperationState.Running);
        Assert.Contains(snapshots, snapshot => snapshot.Progress?.Stage == "download");
        Assert.Equal(OperationState.Succeeded, snapshots[^1].State);
    }

    [Fact]
    public async Task RunAsyncMapsCancellationToCancelledState()
    {
        List<OperationSnapshot> snapshots = [];
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        FakeOperation operation = new((_, token) => Task.FromCanceled<Result<Unit>>(token));

        Result<Unit> result = await new BackgroundOperationRunner().RunAsync(
            operation,
            new InlineProgress<OperationSnapshot>(snapshots.Add),
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("OPERATION_CANCELLED", result.Problem?.Code);
        Assert.Equal(OperationState.Cancelled, snapshots[^1].State);
    }

    private sealed class FakeOperation(
        Func<IProgress<OperationProgress>, CancellationToken, Task<Result<Unit>>> execute) : IBackgroundOperation
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string Kind => "fake";

        public Task<Result<Unit>> ExecuteAsync(
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken) => execute(progress, cancellationToken);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
