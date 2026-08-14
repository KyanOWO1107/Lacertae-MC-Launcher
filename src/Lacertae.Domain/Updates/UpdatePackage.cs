namespace Lacertae.Domain.Updates;

/// <summary>
/// The single win-x64 package described by a signed update manifest.
/// </summary>
public sealed record UpdatePackage(
    string Runtime,
    Uri Url,
    long Size,
    string Sha256,
    string FileManifestSha256);
