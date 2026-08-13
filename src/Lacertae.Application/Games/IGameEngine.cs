using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Games;

public interface IGameEngine
{
    Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(
        CancellationToken cancellationToken);
}
