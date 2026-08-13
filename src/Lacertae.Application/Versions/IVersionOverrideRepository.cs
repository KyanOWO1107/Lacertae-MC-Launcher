using Lacertae.Domain.Common;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Versions;

public interface IVersionOverrideRepository
{
    Task<IReadOnlyList<VersionOverride>> GetForGameRootAsync(
        string gameRootId,
        CancellationToken cancellationToken);

    Task<Result<Unit>> UpsertAsync(
        VersionOverride versionOverride,
        CancellationToken cancellationToken);

    Task<Result<Unit>> RemoveAsync(
        string gameRootId,
        string versionFolder,
        CancellationToken cancellationToken);
}
