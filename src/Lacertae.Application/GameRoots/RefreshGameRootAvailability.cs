using Lacertae.Application.Storage;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Results;

namespace Lacertae.Application.GameRoots;

public sealed class RefreshGameRootAvailability(IGameRootRepository repository, IFileSystem fileSystem)
{
    public async Task<Result<Unit>> ExecuteAsync(CancellationToken cancellationToken)
    {
        foreach (GameRoot root in await repository.GetAllAsync(cancellationToken))
        {
            GameRoot updated = root with
            {
                Availability = fileSystem.DirectoryExists(root.NormalizedPath)
                    ? GameRootAvailability.Available
                    : GameRootAvailability.Unavailable,
                LastScannedUtc = DateTimeOffset.UtcNow,
            };
            Result<Unit> result = await repository.UpsertAsync(updated, cancellationToken);
            if (!result.IsSuccess)
            {
                return result;
            }
        }

        return Result.Success();
    }
}
