using Lacertae.Domain.Downloads;

namespace Lacertae.Application.Downloads;

public sealed record DownloadCandidate(
    DownloadSourceId SourceId,
    Uri Uri,
    bool IsOfficial,
    bool SupportsRanges);
