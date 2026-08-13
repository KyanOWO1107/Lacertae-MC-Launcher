using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Results;

namespace Lacertae.Application.GameRoots;

public interface IGameRootRepository
{
    Task<IReadOnlyList<GameRoot>> GetAllAsync(CancellationToken cancellationToken);
    Task<GameRoot?> FindByNormalizedPathAsync(string normalizedPath, CancellationToken cancellationToken);
    Task<Result<Unit>> UpsertAsync(GameRoot gameRoot, CancellationToken cancellationToken);
    Task<Result<Unit>> RemoveAsync(string id, CancellationToken cancellationToken);
}
