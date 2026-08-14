using Lacertae.Domain.Downloads;

namespace Lacertae.Domain.Install;

public sealed record VanillaInstallPlan(
    string OperationId,
    InstallAction Action,
    string GameRootId,
    string GameRootPath,
    string VersionId,
    string VersionDirectory,
    long RequiredDownloadBytes,
    long RequiredWorkingBytes,
    IReadOnlyList<DownloadArtifact> Artifacts,
    DateTimeOffset CreatedUtc);
