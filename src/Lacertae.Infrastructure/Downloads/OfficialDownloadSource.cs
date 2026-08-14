using Lacertae.Application.Downloads;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Downloads;

public sealed class OfficialDownloadSource : IDownloadSource
{
    private static readonly DownloadSourceId Source = new("official");

    public DownloadSourceId Id => Source;

    public bool IsOfficial => true;

    public bool CanMap(DownloadArtifact artifact) =>
        artifact is not null && IsTrustedOfficialUri(artifact.OfficialUri);

    public Result<DownloadCandidate> Map(DownloadArtifact artifact, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || !CanMap(artifact))
        {
            return Result<DownloadCandidate>.Failure(Unavailable(correlationId));
        }

        return Result<DownloadCandidate>.Success(new DownloadCandidate(
            Source,
            artifact.OfficialUri,
            IsOfficial: true,
            SupportsRanges: true));
    }

    internal static bool IsTrustedOfficialUri(Uri? uri) =>
        uri is not null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.IsDefaultPort &&
        IsTrustedOfficialHost(uri.Host) && string.IsNullOrEmpty(uri.Query);

    internal static bool IsTrustedOfficialHost(string host) =>
        host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("launcher.mojang.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase);

    internal static Problem Unavailable(string correlationId) => new(
        "DOWNLOAD_SOURCE_UNAVAILABLE",
        ProblemStage.Download,
        "problem.download.source_unavailable",
        true,
        string.IsNullOrWhiteSpace(correlationId) ? "download-source" : correlationId,
        ["action.download.choose_source"],
        new Dictionary<string, string>(StringComparer.Ordinal) { ["sourceId"] = Source.Value });
}
