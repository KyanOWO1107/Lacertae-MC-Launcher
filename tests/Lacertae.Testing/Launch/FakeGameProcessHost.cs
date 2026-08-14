using Lacertae.Application.Games;
using Lacertae.Application.Launch;
using Lacertae.Domain.Common;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Results;

namespace Lacertae.Testing.Launch;

public sealed class FakeGameProcessHost : IGameProcessHost
{
    private readonly TaskCompletionSource<Result<GameExitResult>> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<GameProcessSpec> Started { get; } = [];

    public List<int> Stopped { get; } = [];

    public GameExitResult ExitResult { get; set; } = new(
        1234,
        0,
        GameProcessState.Exited,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "corr-1");

    public bool WaitForCompletion { get; set; }

    public Task<Result<GameExitResult>> RunAsync(
        GameProcessSpec spec,
        IProgress<GameLogLine> log,
        CancellationToken waitCancellationToken)
    {
        Started.Add(spec);
        if (!WaitForCompletion)
        {
            return Task.FromResult(Result<GameExitResult>.Success(ExitResult));
        }

        return completion.Task.WaitAsync(waitCancellationToken);
    }

    public Task<Result<Unit>> StopAsync(int processId, CancellationToken cancellationToken)
    {
        Stopped.Add(processId);
        return Task.FromResult(Result.Success());
    }

    public void Complete(GameExitResult result) => completion.TrySetResult(Result<GameExitResult>.Success(result));
}
