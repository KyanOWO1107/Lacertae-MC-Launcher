using System.Security.Cryptography;
using System.Text.Json;
using Lacertae.Application.Archives;
using Lacertae.Application.Downloads;
using Lacertae.Application.Storage;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Updates;

namespace Lacertae.Application.Updates;

public sealed record StageUpdateRequest(
    VerifiedUpdateManifest Update,
    string CurrentLauncherVersion,
    string UpdatesPath,
    bool Confirmed,
    bool GameRunning,
    bool InstallRunning,
    string CorrelationId);

public sealed record StagedUpdate(
    string Version,
    string RelativeStagingPath,
    string MetadataPath);

/// <summary>
/// Downloads, independently verifies and extracts an already trusted update.
/// It does not start the updater or replace the running application.
/// </summary>
public sealed class StageUpdate
{
    private const int PackageManifestSchemaVersion = 1;
    private const int MaximumPackageManifestEntries = 20_000;
    private const long MaximumPackageManifestBytes = 10L * 1024 * 1024;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumExpansionRatio = 100;
    private readonly IArtifactDownloader downloader;
    private readonly IArchiveExtractor extractor;

    public StageUpdate(IArtifactDownloader downloader, IArchiveExtractor extractor)
    {
        this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        this.extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public async Task<Result<StagedUpdate>> ExecuteAsync(
        StageUpdateRequest request,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        if (!request.Confirmed)
        {
            return Failure<StagedUpdate>("UPDATE_CONFIRMATION_REQUIRED", request.CorrelationId);
        }

        if (request.GameRunning || request.InstallRunning)
        {
            return Failure<StagedUpdate>("UPDATE_ACTIVE_OPERATION", request.CorrelationId);
        }

        if (!IsValidRequest(request))
        {
            return Failure<StagedUpdate>("UPDATE_STAGE_INVALID", request.CorrelationId);
        }

        string? stagingPath = null;
        try
        {
            string updatesPath = Path.GetFullPath(request.UpdatesPath);
            SecureFileSystem.EnsureDirectory(updatesPath);
            using IDisposable updatesLease = SecureFileSystem.OpenDirectoryLease(updatesPath);
            string downloadsPath = Path.Combine(updatesPath, "downloads");
            SecureFileSystem.EnsureDirectory(downloadsPath, updatesPath);
            using IDisposable downloadsLease = SecureFileSystem.OpenDirectoryLease(downloadsPath, updatesPath);
            string stagingRoot = Path.Combine(updatesPath, "staging");
            SecureFileSystem.EnsureDirectory(stagingRoot, updatesPath);
            using IDisposable stagingRootLease = SecureFileSystem.OpenDirectoryLease(stagingRoot, updatesPath);

            DownloadArtifact artifact = DownloadArtifact.Create(
                ArtifactKind.LauncherUpdatePackage,
                request.Update.Manifest.Package.Url,
                "launcher-update.zip",
                request.Update.Manifest.Package.Size,
                [new ArtifactHash("sha256", request.Update.Manifest.Package.Sha256)]);
            Result<DownloadReceipt> downloaded = await downloader.DownloadAsync(
                new DownloadRequest(
                    artifact,
                    downloadsPath,
                    DownloadSourcePreference.Pinned(new DownloadSourceId("official")),
                    TemporaryFallbackApproved: false,
                    request.CorrelationId),
                progress,
                cancellationToken);
            if (!downloaded.IsSuccess)
            {
                return Result<StagedUpdate>.Failure(downloaded.Problem!);
            }

            if (!IsSafeFile(downloaded.Value.VerifiedFilePath, downloadsPath) ||
                !await HasSha256Async(
                    downloaded.Value.VerifiedFilePath,
                    artifact.Hashes[0].NormalizedHexDigest,
                    artifact.ExpectedSize,
                    cancellationToken))
            {
                return Failure<StagedUpdate>("UPDATE_PACKAGE_HASH_MISMATCH", request.CorrelationId);
            }

            string stagingName = request.Update.Manifest.Version + "-" + Guid.NewGuid().ToString("N");
            stagingPath = Path.Combine(stagingRoot, stagingName);
            SecureFileSystem.EnsureDirectory(stagingPath, stagingRoot);
            using IDisposable stagingLease = SecureFileSystem.OpenDirectoryLease(stagingPath, stagingRoot);
            Result<Unit> extracted = await extractor.ExtractAsync(
                new ArchiveExtractionRequest(
                    downloaded.Value.VerifiedFilePath,
                    stagingPath,
                    MaximumPackageManifestEntries,
                    MaximumExpandedBytes,
                    MaximumExpansionRatio,
                    AllowLinks: false),
                progress,
                cancellationToken);
            if (!extracted.IsSuccess)
            {
                return Result<StagedUpdate>.Failure(extracted.Problem!);
            }

            Result<Unit> manifestResult = await ValidatePackageContentsAsync(
                stagingPath,
                request.Update.Manifest.Package.FileManifestSha256,
                cancellationToken);
            if (!manifestResult.IsSuccess)
            {
                return Result<StagedUpdate>.Failure(manifestResult.Problem!);
            }

            string signedManifestPath = Path.Combine(stagingPath, "signed-manifest.json");
            string signaturePath = Path.Combine(stagingPath, "signed-manifest.sig");
            await SecureFileSystem.WriteAtomicallyAsync(signedManifestPath, request.Update.CanonicalBytes, cancellationToken);
            await SecureFileSystem.WriteAtomicallyAsync(signaturePath, request.Update.Signature, cancellationToken);
            string relativeStagingPath = Path.GetRelativePath(updatesPath, stagingPath).Replace(Path.DirectorySeparatorChar, '/');
            string metadataPath = Path.Combine(updatesPath, "staged-update.json");
            if (File.Exists(metadataPath) && !IsSafeFile(metadataPath, updatesPath))
            {
                return Failure<StagedUpdate>("UPDATE_STAGE_INVALID", request.CorrelationId);
            }

            StagedUpdateDocument metadata = new(
                1,
                request.Update.Manifest.Version,
                UpdateManifest.SupportedRuntime,
                relativeStagingPath,
                "signed-manifest.json",
                "signed-manifest.sig",
                request.Update.Manifest.Package.FileManifestSha256);
            byte[] metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
            await SecureFileSystem.WriteAtomicallyAsync(metadataPath, metadataBytes, cancellationToken);
            return Result<StagedUpdate>.Success(new StagedUpdate(
                request.Update.Manifest.Version,
                relativeStagingPath,
                metadataPath));
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(stagingPath);
            throw;
        }
        catch (JsonException)
        {
            TryDeleteDirectory(stagingPath);
            return Failure<StagedUpdate>("UPDATE_PACKAGE_MANIFEST_INVALID", request.CorrelationId);
        }
        catch (IOException)
        {
            TryDeleteDirectory(stagingPath);
            return Failure<StagedUpdate>("UPDATE_STAGE_FAILED", request.CorrelationId);
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteDirectory(stagingPath);
            return Failure<StagedUpdate>("UPDATE_STAGE_FAILED", request.CorrelationId);
        }
        catch (InvalidDataException)
        {
            TryDeleteDirectory(stagingPath);
            return Failure<StagedUpdate>("UPDATE_PACKAGE_MANIFEST_INVALID", request.CorrelationId);
        }
        catch (ArgumentException)
        {
            TryDeleteDirectory(stagingPath);
            return Failure<StagedUpdate>("UPDATE_STAGE_INVALID", request.CorrelationId);
        }
        catch (NotSupportedException)
        {
            TryDeleteDirectory(stagingPath);
            return Failure<StagedUpdate>("UPDATE_STAGE_INVALID", request.CorrelationId);
        }
    }

    private static async Task<Result<Unit>> ValidatePackageContentsAsync(
        string stagingPath,
        string expectedManifestHash,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(stagingPath, "package-manifest.json");
        if (!IsSafeFile(manifestPath, stagingPath))
        {
            return Failure<Unit>("UPDATE_PACKAGE_MANIFEST_INVALID", "update-package");
        }

        await using Stream manifestStream = SecureFileSystem.OpenRead(manifestPath, stagingPath);
        if (manifestStream.Length > MaximumPackageManifestBytes)
        {
            return Failure<Unit>("UPDATE_PACKAGE_MANIFEST_INVALID", "update-package");
        }

        using MemoryStream manifestBuffer = new();
        await manifestStream.CopyToAsync(manifestBuffer, cancellationToken);
        byte[] manifestBytes = manifestBuffer.ToArray();
        string actualManifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (!string.Equals(actualManifestHash, expectedManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            return Failure<Unit>("UPDATE_FILE_MANIFEST_HASH_MISMATCH", "update-package");
        }

        using JsonDocument document = JsonDocument.Parse(manifestBytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Select(static property => property.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
        {
            return Failure<Unit>("UPDATE_PACKAGE_MANIFEST_INVALID", "update-package");
        }

        HashSet<string> knownProperties = ["schemaVersion", "files"];
        if (root.EnumerateObject().Any(property => !knownProperties.Contains(property.Name)) ||
            !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != PackageManifestSchemaVersion ||
            !root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array ||
            files.GetArrayLength() > MaximumPackageManifestEntries)
        {
            return Failure<Unit>("UPDATE_PACKAGE_MANIFEST_INVALID", "update-package");
        }

        Dictionary<string, PackageFile> expected = new(StringComparer.Ordinal);
        foreach (JsonElement file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object ||
                file.EnumerateObject().Select(static property => property.Name).Distinct(StringComparer.Ordinal).Count() != file.EnumerateObject().Count())
            {
                return Failure<Unit>("UPDATE_PACKAGE_MANIFEST_INVALID", "update-package");
            }

            HashSet<string> fileProperties = ["path", "size", "sha256"];
            if (file.EnumerateObject().Any(property => !fileProperties.Contains(property.Name)) ||
                !file.TryGetProperty("path", out JsonElement pathElement) || pathElement.ValueKind != JsonValueKind.String ||
                !file.TryGetProperty("size", out JsonElement sizeElement) || sizeElement.ValueKind != JsonValueKind.Number ||
                !file.TryGetProperty("sha256", out JsonElement hashElement) || hashElement.ValueKind != JsonValueKind.String)
            {
                return Failure<Unit>("UPDATE_PACKAGE_MANIFEST_INVALID", "update-package");
            }

            string? path = pathElement.GetString();
            string? hash = hashElement.GetString();
            if (path is null || !IsSafeRelativePath(path) || !long.TryParse(sizeElement.GetRawText(), out long size) || size < 0 ||
                hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit) ||
                string.Equals(path, "package-manifest.json", StringComparison.Ordinal) ||
                !expected.TryAdd(path, new PackageFile(size, hash.ToLowerInvariant())))
            {
                return Failure<Unit>("UPDATE_PACKAGE_MANIFEST_INVALID", "update-package");
            }
        }

        Dictionary<string, string> actualPaths = new(StringComparer.Ordinal);
        foreach (string filePath in Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories))
        {
            if (!IsSafeFile(filePath, stagingPath))
            {
                return Failure<Unit>("UPDATE_PACKAGE_REPARSE_POINT", "update-package");
            }

            string relative = Path.GetRelativePath(stagingPath, filePath).Replace(Path.DirectorySeparatorChar, '/');
            if (relative is "package-manifest.json" or "signed-manifest.json" or "signed-manifest.sig")
            {
                continue;
            }

            await using Stream stream = SecureFileSystem.OpenRead(filePath, stagingPath);
            long fileSize = stream.Length;
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!actualPaths.TryAdd(relative, hash) || !expected.TryGetValue(relative, out PackageFile? expectedFile) ||
                expectedFile.Size != fileSize || !string.Equals(expectedFile.Sha256, hash, StringComparison.Ordinal))
            {
                return Failure<Unit>("UPDATE_PACKAGE_FILE_MISMATCH", "update-package");
            }
        }

        if (actualPaths.Count != expected.Count || expected.Keys.Any(path => !actualPaths.ContainsKey(path)))
        {
            return Failure<Unit>("UPDATE_PACKAGE_FILE_MISMATCH", "update-package");
        }

        return Result.Success();
    }

    private static bool IsValidRequest(StageUpdateRequest request) =>
        request.Update is not null &&
        UpdateManifest.IsValidSemanticVersion(request.CurrentLauncherVersion) &&
        UpdateManifest.IsValidSemanticVersion(request.Update.Manifest.MinimumLauncherVersion) &&
        UpdateManifest.IsValidSemanticVersion(request.Update.Manifest.Version) &&
        UpdateManifest.CompareSemanticVersions(request.Update.Manifest.Version, request.CurrentLauncherVersion) > 0 &&
        UpdateManifest.CompareSemanticVersions(request.CurrentLauncherVersion, request.Update.Manifest.MinimumLauncherVersion) >= 0 &&
        request.Update.Manifest.Package.Runtime == UpdateManifest.SupportedRuntime &&
        !string.IsNullOrWhiteSpace(request.UpdatesPath) &&
        !string.IsNullOrWhiteSpace(request.CorrelationId);

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains('\0'))
        {
            return false;
        }

        string[] segments = path.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not "." and not ".." &&
            !segment.EndsWith('.') && !segment.EndsWith(' ') &&
            !segment.Any(character => char.IsControl(character) || character is '<' or '>' or ':' or '"' or '|' or '?' or '*') &&
            !IsReservedWindowsName(segment));
    }

    private static bool IsReservedWindowsName(string segment)
    {
        string stem = segment.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                char.IsAsciiDigit(stem[3]));
    }

    private static bool IsSafeFile(string path, string root)
    {
        return SecureFileSystem.IsSafeFile(path, root);
    }

    private static async Task<bool> HasSha256Async(
        string path,
        string expected,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        await using Stream stream = SecureFileSystem.OpenRead(path);
        if (stream.Length != expectedSize)
        {
            return false;
        }

        string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                SecureFileSystem.DeleteDirectory(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Result<T> Failure<T>(string code, string correlationId) => Result<T>.Failure(new Problem(
        code,
        ProblemStage.Update,
        "problem.update.stage_failed",
        code is "UPDATE_STAGE_FAILED" or "UPDATE_CHECK_FAILED",
        string.IsNullOrWhiteSpace(correlationId) ? "update-stage" : correlationId,
        ["action.update.retry"]));

    private sealed record PackageFile(long Size, string Sha256);

    private sealed record StagedUpdateDocument(
        int SchemaVersion,
        string Version,
        string Runtime,
        string StagingPath,
        string SignedManifestFile,
        string SignatureFile,
        string FileManifestSha256);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
