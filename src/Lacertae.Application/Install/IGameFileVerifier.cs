using Lacertae.Domain.Downloads;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Install;

public interface IGameFileVerifier
{
    Task<Result<bool>> VerifyAsync(
        DownloadArtifact artifact,
        string filePath,
        CancellationToken cancellationToken);
}
