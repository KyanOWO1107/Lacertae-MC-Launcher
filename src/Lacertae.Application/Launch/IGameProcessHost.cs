using Lacertae.Application.Games;
using Lacertae.Domain.Common;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Launch;

public interface IGameProcessHost
{
    Task<Result<GameExitResult>> RunAsync(
        GameProcessSpec spec,
        IProgress<GameLogLine> log,
        CancellationToken waitCancellationToken);

    Task<Result<Unit>> StopAsync(
        int processId,
        CancellationToken cancellationToken);
}

public interface ILauncherDispositionController
{
    void Apply(LaunchDisposition disposition);

    void Restore();
}
