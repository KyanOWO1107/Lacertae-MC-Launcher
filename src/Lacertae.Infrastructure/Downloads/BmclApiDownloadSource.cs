using Lacertae.Application.Downloads;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Downloads;

public sealed class BmclApiDownloadSource
    : IDownloadSource
{
    private static readonly DownloadSourceId Source = new("bmclapi");
    private readonly Uri baseUri;

    public BmclApiDownloadSource(Uri? baseUri = null)
    {
        this.baseUri = baseUri ?? new Uri("https://bmclapi2.bangbang93.com/", UriKind.Absolute);
        if (!IsSafeBaseUri(this.baseUri))
        {
            throw new ArgumentException("BMCLAPI base URI must be a plain HTTPS origin.", nameof(baseUri));
        }
    }

    public DownloadSourceId Id => Source;

    public bool IsOfficial => false;

    public bool CanMap(DownloadArtifact artifact) => TryMap(artifact, out _);

    public Result<DownloadCandidate> Map(DownloadArtifact artifact, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || !TryMap(artifact, out Uri? uri))
        {
            return Result<DownloadCandidate>.Failure(Unavailable(correlationId));
        }

        return Result<DownloadCandidate>.Success(new DownloadCandidate(
            Source,
            uri!,
            IsOfficial: false,
            SupportsRanges: true));
    }

    private bool TryMap(DownloadArtifact? artifact, out Uri? mappedUri)
    {
        mappedUri = null;
        Uri? official = artifact?.OfficialUri;
        if (!OfficialDownloadSource.IsTrustedOfficialUri(official))
        {
            return false;
        }

        string[] segments = GetSafePathSegments(official!.AbsolutePath);
        if (segments.Length == 0)
        {
            return false;
        }

        string[] mappedSegments;
        if (official.Host.Equals("resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length != 2 || segments[0].Length != 2 || segments[1].Length != 40 ||
                !segments[0].All(char.IsAsciiHexDigit) || !segments[1].All(char.IsAsciiHexDigit))
            {
                return false;
            }

            mappedSegments = ["assets", segments[0].ToLowerInvariant(), segments[1].ToLowerInvariant()];
        }
        else if (official.Host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            mappedSegments = ["maven", .. segments];
        }
        else if (official.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
                 official.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
                 official.Host.Equals("launcher.mojang.com", StringComparison.OrdinalIgnoreCase) ||
                 official.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
        {
            mappedSegments = segments;
        }
        else
        {
            return false;
        }

        mappedUri = new Uri(baseUri, string.Join('/', mappedSegments));
        return true;
    }

    private static string[] GetSafePathSegments(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('%') || path.Contains('\\') || path.Contains('\0'))
        {
            return [];
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 || segments.Any(static segment =>
                segment is "." or ".." || segment.Any(character =>
                    char.IsControl(character) || character is ':' or '?' or '#'))
            ? []
            : segments;
    }

    private static bool IsSafeBaseUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath is "/" or "" &&
        uri.Host.Equals("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase);

    private static Problem Unavailable(string correlationId) => new(
        "DOWNLOAD_SOURCE_UNAVAILABLE",
        ProblemStage.Download,
        "problem.download.source_unavailable",
        true,
        string.IsNullOrWhiteSpace(correlationId) ? "download-source" : correlationId,
        ["action.download.choose_source"],
        new Dictionary<string, string>(StringComparer.Ordinal) { ["sourceId"] = Source.Value });
}
