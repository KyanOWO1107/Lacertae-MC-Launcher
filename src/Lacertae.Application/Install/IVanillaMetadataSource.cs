using Lacertae.Domain.Downloads;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Install;

public sealed record VanillaPlatform(
    string OsName,
    string Architecture,
    string? OsVersion = null)
{
    public static VanillaPlatform WindowsX64 { get; } = new("windows", "x64");
}

public sealed record VanillaMetadataSnapshot(
    string VersionId,
    string VersionType,
    DateTimeOffset ReleaseTime,
    JavaRequirement Java,
    DownloadArtifact MetadataArtifact,
    DownloadArtifact ClientArtifact,
    DownloadArtifact? LoggingArtifact,
    IReadOnlyList<DownloadArtifact> LibraryArtifacts,
    DownloadArtifact AssetIndexArtifact,
    IReadOnlyList<DownloadArtifact> AssetObjectArtifacts);

public interface IVanillaMetadataSource
{
    Task<Result<VanillaMetadataSnapshot>> GetAsync(
        string versionId,
        VanillaPlatform platform,
        CancellationToken cancellationToken);
}
