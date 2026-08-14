using Lacertae.Domain.Downloads;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Downloads;

public sealed class DownloadSourceSelector
{
    private readonly IReadOnlyList<IDownloadSource> sources;

    public DownloadSourceSelector(IReadOnlyList<IDownloadSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Any(static source => source is null))
        {
            throw new ArgumentException("Download source registrations cannot contain null.", nameof(sources));
        }

        this.sources = sources.ToArray();
    }

    public Result<IReadOnlyList<DownloadCandidate>> Select(
        DownloadArtifact artifact,
        DownloadSourcePreference preference,
        bool temporaryFallbackApproved,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(preference);
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Correlation ID cannot be blank.", nameof(correlationId));
        }

        if (preference.PinnedSourceId is { } pinnedSourceId)
        {
            return SelectPinned(artifact, pinnedSourceId, temporaryFallbackApproved, correlationId);
        }

        return SelectOrdered(artifact, sources, correlationId);
    }

    private Result<IReadOnlyList<DownloadCandidate>> SelectPinned(
        DownloadArtifact artifact,
        DownloadSourceId pinnedSourceId,
        bool temporaryFallbackApproved,
        string correlationId)
    {
        IDownloadSource? pinnedSource = sources.FirstOrDefault(source => source.Id == pinnedSourceId);
        if (pinnedSource is null || !pinnedSource.CanMap(artifact))
        {
            return Result<IReadOnlyList<DownloadCandidate>>.Failure(SourceUnavailable(pinnedSourceId, correlationId));
        }

        Result<DownloadCandidate> pinnedResult = pinnedSource.Map(artifact, correlationId);
        if (!pinnedResult.IsSuccess)
        {
            return Result<IReadOnlyList<DownloadCandidate>>.Failure(pinnedResult.Problem!);
        }

        if (!temporaryFallbackApproved)
        {
            return Result<IReadOnlyList<DownloadCandidate>>.Success([pinnedResult.Value]);
        }

        List<IDownloadSource> fallbackSources = sources
            .Where(source => source.Id != pinnedSourceId)
            .OrderByDescending(static source => source.IsOfficial)
            .ToList();
        Result<IReadOnlyList<DownloadCandidate>> fallbackResult = SelectOrdered(artifact, fallbackSources, correlationId);
        if (!fallbackResult.IsSuccess)
        {
            return Result<IReadOnlyList<DownloadCandidate>>.Success([pinnedResult.Value]);
        }

        return Result<IReadOnlyList<DownloadCandidate>>.Success(
            [pinnedResult.Value, .. fallbackResult.Value]);
    }

    private static Result<IReadOnlyList<DownloadCandidate>> SelectOrdered(
        DownloadArtifact artifact,
        IEnumerable<IDownloadSource> registeredSources,
        string correlationId)
    {
        List<DownloadCandidate> candidates = [];
        HashSet<DownloadSourceId> seenSourceIds = [];
        Problem? firstMappingProblem = null;
        foreach (IDownloadSource source in registeredSources.OrderByDescending(static source => source.IsOfficial))
        {
            if (!seenSourceIds.Add(source.Id) || !source.CanMap(artifact))
            {
                continue;
            }

            Result<DownloadCandidate> result = source.Map(artifact, correlationId);
            if (result.IsSuccess)
            {
                candidates.Add(result.Value);
            }
            else
            {
                firstMappingProblem ??= result.Problem;
            }
        }

        if (candidates.Count > 0)
        {
            return Result<IReadOnlyList<DownloadCandidate>>.Success(candidates);
        }

        return Result<IReadOnlyList<DownloadCandidate>>.Failure(
            firstMappingProblem ?? SourceUnavailable(null, correlationId));
    }

    private static Problem SourceUnavailable(DownloadSourceId? sourceId, string correlationId) => new(
        "DOWNLOAD_SOURCE_UNAVAILABLE",
        ProblemStage.Download,
        "problem.download.source_unavailable",
        true,
        correlationId,
        ["action.download.choose_source"],
        sourceId is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["sourceId"] = sourceId.Value });
}
