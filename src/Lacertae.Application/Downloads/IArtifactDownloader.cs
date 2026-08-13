using Lacertae.Domain.Downloads;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Downloads;

public interface IArtifactDownloader
{
    Task<Result<string>> DownloadAsync(
        DownloadArtifact artifact,
        string stagingDirectory,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken);
}
