namespace Lacertae.Domain.Downloads;

public sealed record ArtifactHash(string Algorithm, string HexDigest)
{
    public string NormalizedAlgorithm => Algorithm.Trim().ToLowerInvariant();
    public string NormalizedHexDigest => HexDigest.Trim().ToLowerInvariant();
}
