using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Downloads;

public interface IArtifactCache
{
    Task<Result<string?>> GetAsync(
        DownloadArtifact artifact,
        CancellationToken cancellationToken);

    Task<Result<Unit>> PutAsync(
        DownloadArtifact artifact,
        string verifiedFilePath,
        CancellationToken cancellationToken);
}
