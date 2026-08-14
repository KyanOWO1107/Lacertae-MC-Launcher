using Lacertae.Application.Launch;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Games;

public interface IGameEngine
{
    Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(
        string gameRootPath,
        CancellationToken cancellationToken);

    Task<Result<GameProcessSpec>> BuildProcessSpecAsync(
        LaunchPlan plan,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result<GameProcessSpec>.Failure(new Problem(
            "GAME_PROCESS_BUILD_UNSUPPORTED",
            ProblemStage.Process,
            "problem.game.process_build_unsupported",
            false,
            Guid.NewGuid().ToString("N"),
            ["action.launch.select_supported_engine"])));
}
