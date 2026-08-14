using Lacertae.Domain.Downloads;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Downloads;

public sealed record DownloadRequest(
    DownloadArtifact Artifact,
    string StagingDirectory,
    DownloadSourcePreference SourcePreference,
    bool TemporaryFallbackApproved,
    string CorrelationId);

public sealed record DownloadReceipt(
    string VerifiedFilePath,
    DownloadSourceId SourceId,
    long BytesTransferred,
    bool WasResumed,
    ArtifactHash VerifiedHash);

public interface IArtifactDownloader
{
    Task<Result<DownloadReceipt>> DownloadAsync(
        DownloadRequest request,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken);
}
