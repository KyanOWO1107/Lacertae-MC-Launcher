using Lacertae.Domain.Diagnostics;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Diagnostics;

public interface IGameCrashAnalyzer
{
    Task<Result<GameCrashReport>> AnalyzeAsync(
        LaunchPlan plan,
        GameExitResult gameExit,
        string sanitizedLogPath,
        CancellationToken cancellationToken);
}
