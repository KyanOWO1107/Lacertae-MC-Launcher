using Lacertae.Domain.Downloads;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Install;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Install;

public sealed class PlanVanillaInstall(
    IVanillaMetadataSource metadataSource,
    TimeProvider? timeProvider = null)
{
    private const long WorkingSafetyMarginBytes = 256L * 1024 * 1024;
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<Result<VanillaInstallPlan>> ExecuteAsync(
        GameRoot gameRoot,
        string versionId,
        InstallAction action,
        VanillaPlatform platform,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(metadataSource);
        if (string.IsNullOrWhiteSpace(gameRoot.Id) || string.IsNullOrWhiteSpace(gameRoot.NormalizedPath) ||
            !IsSafeSegment(versionId) || !Enum.IsDefined(action) ||
            string.IsNullOrWhiteSpace(platform.OsName) || string.IsNullOrWhiteSpace(platform.Architecture))
        {
            return Result<VanillaInstallPlan>.Failure(InvalidProblem());
        }

        string gameRootPath;
        try
        {
            gameRootPath = Path.GetFullPath(gameRoot.NormalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Result<VanillaInstallPlan>.Failure(InvalidProblem());
        }

        Result<VanillaMetadataSnapshot> metadataResult = await metadataSource.GetAsync(
            versionId,
            platform,
            cancellationToken);
        if (!metadataResult.IsSuccess)
        {
            return Result<VanillaInstallPlan>.Failure(metadataResult.Problem!);
        }

        VanillaMetadataSnapshot metadata = metadataResult.Value;
        if (!string.Equals(metadata.VersionId, versionId, StringComparison.Ordinal) ||
            !IsSafeSegment(metadata.VersionId) ||
            string.IsNullOrWhiteSpace(metadata.VersionType) ||
            metadata.LibraryArtifacts is null || metadata.AssetObjectArtifacts is null)
        {
            return Result<VanillaInstallPlan>.Failure(InvalidProblem());
        }

        List<DownloadArtifact> artifacts = [metadata.MetadataArtifact, metadata.ClientArtifact];
        if (metadata.LoggingArtifact is not null)
        {
            artifacts.Add(metadata.LoggingArtifact);
        }

        artifacts.AddRange(metadata.LibraryArtifacts);
        artifacts.Add(metadata.AssetIndexArtifact);
        artifacts.AddRange(metadata.AssetObjectArtifacts);
        Result<IReadOnlyList<DownloadArtifact>> normalizedResult = NormalizeArtifacts(artifacts, gameRootPath);
        if (!normalizedResult.IsSuccess)
        {
            return Result<VanillaInstallPlan>.Failure(normalizedResult.Problem!);
        }

        IReadOnlyList<DownloadArtifact> normalizedArtifacts = normalizedResult.Value;
        long requiredDownloadBytes;
        long nativeExtractionBytes;
        try
        {
            requiredDownloadBytes = normalizedArtifacts.Sum(static artifact => checked(artifact.ExpectedSize));
            nativeExtractionBytes = normalizedArtifacts
                .Where(static artifact => artifact.Kind == ArtifactKind.Library &&
                    artifact.RelativeDestinationPath.Contains("natives-", StringComparison.OrdinalIgnoreCase))
                .Sum(static artifact => checked(artifact.ExpectedSize));
        }
        catch (OverflowException)
        {
            return Result<VanillaInstallPlan>.Failure(InvalidProblem());
        }

        long quarantineBytes = action == InstallAction.Repair ? requiredDownloadBytes : 0;
        long requiredWorkingBytes;
        try
        {
            requiredWorkingBytes = checked(
                requiredDownloadBytes + quarantineBytes + nativeExtractionBytes + WorkingSafetyMarginBytes);
        }
        catch (OverflowException)
        {
            return Result<VanillaInstallPlan>.Failure(InvalidProblem());
        }

        string versionDirectory = Path.Combine(gameRootPath, "versions", metadata.VersionId);
        return Result<VanillaInstallPlan>.Success(new VanillaInstallPlan(
            Guid.NewGuid().ToString("N"),
            action,
            gameRoot.Id,
            gameRootPath,
            metadata.VersionId,
            versionDirectory,
            requiredDownloadBytes,
            requiredWorkingBytes,
            normalizedArtifacts,
            timeProvider.GetUtcNow()));
    }

    private static Result<IReadOnlyList<DownloadArtifact>> NormalizeArtifacts(
        IReadOnlyList<DownloadArtifact> artifacts,
        string gameRootPath)
    {
        if (artifacts is null || artifacts.Any(static artifact => artifact is null))
        {
            return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem());
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRootPath));
        Dictionary<string, DownloadArtifact> byDestination = new(StringComparer.OrdinalIgnoreCase);
        foreach (DownloadArtifact artifact in artifacts)
        {
            if (!Enum.IsDefined(artifact.Kind) || artifact.ExpectedSize <= 0 ||
                artifact.Hashes is null || artifact.Hashes.Count == 0 ||
                string.IsNullOrWhiteSpace(artifact.RelativeDestinationPath) ||
                artifact.RelativeDestinationPath.Contains('\0'))
            {
                return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem());
            }

            string normalizedPath = artifact.RelativeDestinationPath.Replace('\\', '/');
            if (!IsSafeRelativePath(normalizedPath))
            {
                return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem());
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(root, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem());
            }

            if (!IsUnderRoot(fullPath, root))
            {
                return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem());
            }

            DownloadArtifact normalized = artifact with { RelativeDestinationPath = normalizedPath };
            if (byDestination.TryGetValue(normalizedPath, out DownloadArtifact? existing))
            {
                if (existing.ExpectedSize != normalized.ExpectedSize || !HashesEqual(existing, normalized))
                {
                    return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem());
                }

                continue;
            }

            byDestination.Add(normalizedPath, normalized);
        }

        return Result<IReadOnlyList<DownloadArtifact>>.Success(byDestination.Values.ToArray());
    }

    private static bool HashesEqual(DownloadArtifact left, DownloadArtifact right)
    {
        Dictionary<string, string> leftHashes = left.Hashes.ToDictionary(
            static hash => hash.NormalizedAlgorithm,
            static hash => hash.NormalizedHexDigest,
            StringComparer.Ordinal);
        Dictionary<string, string> rightHashes = right.Hashes.ToDictionary(
            static hash => hash.NormalizedAlgorithm,
            static hash => hash.NormalizedHexDigest,
            StringComparer.Ordinal);
        return leftHashes.Count == rightHashes.Count && leftHashes.All(pair =>
            rightHashes.TryGetValue(pair.Key, out string? value) &&
            string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnderRoot(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeSegment(string value) =>
        value.Length <= 128 && value is not "." and not ".." &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') && !path.Contains(':') &&
        path.Split('/').All(static segment => segment is not ("" or "." or ".."));

    private static Problem InvalidProblem() => new(
        "VERSION_METADATA_INVALID",
        ProblemStage.VersionResolution,
        "problem.version.metadata_invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.version.review_metadata"]);
}
