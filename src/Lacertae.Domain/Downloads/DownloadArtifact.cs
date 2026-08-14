using System.Security.Cryptography;
using System.Text;

namespace Lacertae.Domain.Downloads;

public sealed record DownloadArtifact(
    string ArtifactId,
    ArtifactKind Kind,
    Uri OfficialUri,
    string RelativeDestinationPath,
    long ExpectedSize,
    IReadOnlyList<ArtifactHash> Hashes)
{
    public static DownloadArtifact Create(
        ArtifactKind kind,
        Uri officialUri,
        string relativeDestinationPath,
        long expectedSize,
        IReadOnlyList<ArtifactHash> hashes)
    {
        ArgumentNullException.ThrowIfNull(officialUri);
        ArgumentNullException.ThrowIfNull(hashes);
        if (!Enum.IsDefined(kind) || !officialUri.IsAbsoluteUri || officialUri.Scheme != Uri.UriSchemeHttps ||
            expectedSize <= 0 || string.IsNullOrWhiteSpace(relativeDestinationPath) || hashes.Count == 0)
        {
            throw new ArgumentException("Download artifact metadata is invalid.", nameof(relativeDestinationPath));
        }

        string path = relativeDestinationPath.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Download artifact path must be relative and normalized.", nameof(relativeDestinationPath));
        }

        List<ArtifactHash> normalizedHashes = hashes
            .Select(static hash =>
            {
                if (hash is null || hash.Algorithm is null || hash.HexDigest is null)
                {
                    throw new ArgumentException("Download artifact hashes are invalid.", nameof(hashes));
                }

                return new ArtifactHash(hash.Algorithm, hash.HexDigest);
            })
            .OrderBy(static hash => hash.NormalizedAlgorithm, StringComparer.Ordinal)
            .ToList();
        if (normalizedHashes.Any(static hash =>
                hash.NormalizedAlgorithm is not ("sha1" or "sha256") ||
                !IsHex(hash.NormalizedHexDigest) ||
                (hash.NormalizedAlgorithm == "sha1" && hash.NormalizedHexDigest.Length != 40) ||
                (hash.NormalizedAlgorithm == "sha256" && hash.NormalizedHexDigest.Length != 64)) ||
            normalizedHashes.GroupBy(static hash => hash.NormalizedAlgorithm, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Download artifact hashes are invalid.", nameof(hashes));
        }

        string canonical = string.Join('|',
            kind,
            path,
            expectedSize,
            string.Join(',', normalizedHashes.Select(static hash => $"{hash.NormalizedAlgorithm}:{hash.NormalizedHexDigest}")));
        string artifactId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new DownloadArtifact(artifactId, kind, officialUri, path, expectedSize, normalizedHashes);
    }

    private static bool IsHex(string value) =>
        value.Length > 0 && value.All(static character => char.IsAsciiHexDigit(character));
}
