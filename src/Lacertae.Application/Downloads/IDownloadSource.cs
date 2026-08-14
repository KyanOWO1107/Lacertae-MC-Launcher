using Lacertae.Domain.Downloads;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Downloads;

public interface IDownloadSource
{
    DownloadSourceId Id { get; }

    bool IsOfficial { get; }

    bool CanMap(DownloadArtifact artifact);

    Result<DownloadCandidate> Map(DownloadArtifact artifact, string correlationId);
}
