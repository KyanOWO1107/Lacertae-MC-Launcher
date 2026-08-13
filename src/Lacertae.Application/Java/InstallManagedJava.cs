using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lacertae.Application.Downloads;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Java;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Storage;

namespace Lacertae.Application.Java;

public sealed class InstallManagedJavaOptions
{
    public int MaximumFileCount { get; init; } = 200_000;
    public long MaximumTotalBytes { get; init; } = 8L * 1024 * 1024 * 1024;
}

public sealed class InstallManagedJava(
    IManagedJavaCatalog catalog,
    IArtifactDownloader downloader,
    IJavaProbe probe,
    InstallManagedJavaOptions? options = null)
{
    private const int MaximumDownloadConcurrency = 4;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly InstallManagedJavaOptions options = options ?? new();

    public async Task<Result<JavaInstallation>> ExecuteAsync(
        DataRoot dataRoot,
        string component,
        JavaArchitecture architecture,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(probe);
        if (options.MaximumFileCount <= 0 || options.MaximumTotalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataRoot), "Managed Java installation limits must be positive.");
        }

        Result<ManagedJavaPackage> packageResult = await catalog.GetPackageAsync(component, architecture, cancellationToken);
        if (!packageResult.IsSuccess)
        {
            return Result<JavaInstallation>.Failure(packageResult.Problem!);
        }

        ManagedJavaPackage? package = packageResult.Value;
        if (package is null)
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
        }
        Result<PackageManifest> manifestResult = ValidatePackage(package, component, architecture);
        if (!manifestResult.IsSuccess)
        {
            return Result<JavaInstallation>.Failure(manifestResult.Problem!);
        }

        PackageManifest manifest = manifestResult.Value;
        string runtimesRoot = Path.GetFullPath(dataRoot.RuntimesPath);
        string targetParent = Path.Combine(runtimesRoot, package.Component);
        string targetPath = Path.Combine(targetParent, package.PackageVersion + "-" + ArchitectureName(package.Architecture));
        if (!IsUnderRoot(targetPath, runtimesRoot))
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
        }

        if (Directory.Exists(targetPath))
        {
            Result<JavaInstallation> existing = await ValidateExistingAsync(targetPath, package, manifest, cancellationToken);
            if (existing.IsSuccess || existing.Problem?.Code is "JAVA_RUNTIME_TARGET_CONFLICT")
            {
                return existing;
            }
        }
        else if (File.Exists(targetPath))
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
        }

        string operationId = Guid.NewGuid().ToString("N");
        string stagingRoot = Path.Combine(runtimesRoot, ".staging", operationId);
        bool committed = false;
        try
        {
            if (!IsWithinConfiguredLimits(manifest))
            {
                return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_MANIFEST_TOO_LARGE"));
            }

            Directory.CreateDirectory(stagingRoot);
            if (HasReparsePointBetween(stagingRoot, runtimesRoot))
            {
                return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }
            foreach (string directory in manifest.Directories)
            {
                Directory.CreateDirectory(CombineRelative(stagingRoot, directory));
            }

            DownloadProgressState progressState = new();
            Progress<OperationProgress> effectiveProgress = new(value => progress?.Report(value));
            using SemaphoreSlim semaphore = new(MaximumDownloadConcurrency, MaximumDownloadConcurrency);
            for (int start = 0; start < manifest.Files.Count; start += MaximumDownloadConcurrency)
            {
                Task<Result<VerifiedArtifact>>[] batch = manifest.Files
                    .Skip(start)
                    .Take(MaximumDownloadConcurrency)
                    .Select(file => DownloadAndVerifyAsync(file, stagingRoot, semaphore, effectiveProgress, progressState, manifest.Files.Count, manifest.TotalBytes, cancellationToken))
                    .ToArray();
                Result<VerifiedArtifact>[] downloaded = await Task.WhenAll(batch);
                foreach (Result<VerifiedArtifact> result in downloaded)
                {
                    if (!result.IsSuccess)
                    {
                        return Result<JavaInstallation>.Failure(result.Problem!);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            string executablePath = CombineRelative(stagingRoot, manifest.ExecutableRelativePath);
            Result<JavaInstallation> probed = await probe.ProbeAsync(
                executablePath,
                JavaSource.Managed,
                true,
                cancellationToken);
            if (!probed.IsSuccess)
            {
                return Result<JavaInstallation>.Failure(probed.Problem!);
            }

            JavaInstallation installation = probed.Value;
            if (!PathsEqual(Path.GetFullPath(installation.ExecutablePath), Path.GetFullPath(executablePath)) ||
                installation.MajorVersion != package.MajorVersion ||
                installation.Architecture != package.Architecture ||
                !installation.IsManaged)
            {
                return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_PROBE_MISMATCH"));
            }

            await WriteRuntimeManifestAsync(stagingRoot, package, manifest, installation, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(targetParent);
            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                Result<JavaInstallation> raced = Directory.Exists(targetPath)
                    ? await ValidateExistingAsync(targetPath, package, manifest, cancellationToken)
                    : Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
                return raced.IsSuccess
                    ? raced
                    : Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
            }

            Directory.Move(stagingRoot, targetPath);
            committed = true;
            return Result<JavaInstallation>.Success(installation with
            {
                ExecutablePath = CombineRelative(targetPath, manifest.ExecutableRelativePath),
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_INSTALL_FAILED"));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_INSTALL_FAILED"));
        }
        finally
        {
            if (!committed && Directory.Exists(stagingRoot))
            {
                TryDeleteDirectory(stagingRoot);
            }
        }
    }

    private async Task<Result<VerifiedArtifact>> DownloadAndVerifyAsync(
        ValidatedArtifact file,
        string stagingRoot,
        SemaphoreSlim semaphore,
        IProgress<OperationProgress> progress,
        DownloadProgressState progressState,
        int totalFileCount,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            string expectedPath = CombineRelative(stagingRoot, file.Artifact.RelativeDestinationPath);
            Result<string> downloaded = await downloader.DownloadAsync(
                file.Artifact,
                stagingRoot,
                progress,
                cancellationToken);
            if (!downloaded.IsSuccess)
            {
                return Result<VerifiedArtifact>.Failure(downloaded.Problem!);
            }

            string actualPath;
            try
            {
                actualPath = Path.GetFullPath(downloaded.Value);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return Result<VerifiedArtifact>.Failure(Problem("DOWNLOAD_PATH_INVALID"));
            }

            if (!PathsEqual(actualPath, expectedPath) || !File.Exists(actualPath) || HasReparsePointBetween(Path.GetDirectoryName(actualPath)!, stagingRoot))
            {
                return Result<VerifiedArtifact>.Failure(Problem("DOWNLOAD_PATH_INVALID"));
            }

            FileInfo info = new(actualPath);
            if (info.Length != file.Artifact.ExpectedSize)
            {
                return Result<VerifiedArtifact>.Failure(DownloadHashMismatch(file));
            }

            Dictionary<string, string> hashes = await CalculateHashesAsync(actualPath, file.HashAlgorithms, cancellationToken);
            foreach ((string algorithm, string expected) in file.ExpectedHashes)
            {
                if (!hashes.TryGetValue(algorithm, out string? actual) ||
                    !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return Result<VerifiedArtifact>.Failure(DownloadHashMismatch(file));
                }
            }

            int item = Interlocked.Increment(ref progressState.CompletedItems);
            long bytes = Interlocked.Add(ref progressState.CompletedBytes, info.Length);
            progress.Report(new OperationProgress("download", item, totalFileCount, bytes, totalBytes));
            return Result<VerifiedArtifact>.Success(new VerifiedArtifact(file.Artifact, hashes));
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<Dictionary<string, string>> CalculateHashesAsync(
        string path,
        IReadOnlyList<string> algorithms,
        CancellationToken cancellationToken)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        IncrementalHash? sha1 = algorithms.Contains("sha1", StringComparer.Ordinal) ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        IncrementalHash? sha256 = algorithms.Contains("sha256", StringComparer.Ordinal) ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            sha1?.AppendData(buffer, 0, read);
            sha256?.AppendData(buffer, 0, read);
        }

        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (sha1 is not null)
        {
            result["sha1"] = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
            sha1.Dispose();
        }

        if (sha256 is not null)
        {
            result["sha256"] = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            sha256.Dispose();
        }

        return result;
    }

    private static async Task WriteRuntimeManifestAsync(
        string stagingRoot,
        ManagedJavaPackage package,
        PackageManifest manifest,
        JavaInstallation installation,
        CancellationToken cancellationToken)
    {
        RuntimeManifest document = new(
            1,
            package.Component,
            package.MajorVersion,
            ArchitectureName(package.Architecture),
            package.PackageVersion,
            package.ExecutableRelativePath,
            new RuntimeInstallation(
                installation.Id,
                installation.MajorVersion,
                installation.FullVersion,
                installation.Vendor,
                ArchitectureName(installation.Architecture),
                installation.Source,
                installation.IsManaged),
            manifest.Files
                .Select(file => new RuntimeFile(
                    file.Artifact.ArtifactId,
                    file.Artifact.RelativeDestinationPath,
                    file.Artifact.ExpectedSize,
                    file.Artifact.Hashes.Select(hash => new RuntimeHash(hash.NormalizedAlgorithm, hash.NormalizedHexDigest)).ToArray()))
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToArray());
        string temporaryPath = Path.Combine(stagingRoot, "runtime.json.tmp");
        string finalPath = Path.Combine(stagingRoot, "runtime.json");
        await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(true);
        }

        File.Move(temporaryPath, finalPath);
    }

    private static async Task<Result<JavaInstallation>> ValidateExistingAsync(
        string targetPath,
        ManagedJavaPackage package,
        PackageManifest manifest,
        CancellationToken cancellationToken)
    {
        string runtimePath = Path.Combine(targetPath, "runtime.json");
        if (!File.Exists(runtimePath))
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
        }

        try
        {
            await using FileStream stream = File.OpenRead(runtimePath);
            RuntimeManifest? document = await JsonSerializer.DeserializeAsync<RuntimeManifest>(stream, JsonOptions, cancellationToken);
            if (document is null || document.SchemaVersion != 1 ||
                !string.Equals(document.Component, package.Component, StringComparison.Ordinal) ||
                document.MajorVersion != package.MajorVersion ||
                !string.Equals(document.Architecture, ArchitectureName(package.Architecture), StringComparison.Ordinal) ||
                !string.Equals(document.PackageVersion, package.PackageVersion, StringComparison.Ordinal) ||
                !string.Equals(document.ExecutableRelativePath, package.ExecutableRelativePath, StringComparison.Ordinal) ||
                document.Files is null)
            {
                return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
            }

            Dictionary<string, RuntimeFile> files;
            try
            {
                files = document.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
            }
            if (files.Count != manifest.Files.Count || manifest.Files.Any(file => !files.ContainsKey(file.Artifact.RelativeDestinationPath)))
            {
                return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
            }

            foreach (ValidatedArtifact file in manifest.Files)
            {
                string path = CombineRelative(targetPath, file.Artifact.RelativeDestinationPath);
                if (!File.Exists(path) || new FileInfo(path).Length != file.Artifact.ExpectedSize ||
                    !await MatchesHashesAsync(path, file, cancellationToken))
                {
                    return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
                }
            }

            RuntimeInstallation? saved = document.Installation;
            string executablePath = CombineRelative(targetPath, manifest.ExecutableRelativePath);
            if (saved is null || !File.Exists(executablePath) || saved.MajorVersion != package.MajorVersion ||
                !string.Equals(saved.Architecture, ArchitectureName(package.Architecture), StringComparison.Ordinal) ||
                !saved.IsManaged)
            {
                return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
            }

            return Result<JavaInstallation>.Success(new JavaInstallation(
                saved.Id,
                executablePath,
                saved.MajorVersion,
                saved.FullVersion,
                saved.Vendor,
                package.Architecture,
                saved.Source,
                true));
        }
        catch (JsonException)
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
        }
        catch (IOException)
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
        }
        catch (ArgumentException)
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_RUNTIME_TARGET_CONFLICT"));
        }
    }

    private static async Task<bool> MatchesHashesAsync(string path, ValidatedArtifact file, CancellationToken cancellationToken)
    {
        Dictionary<string, string> actual = await CalculateHashesAsync(path, file.HashAlgorithms, cancellationToken);
        return file.ExpectedHashes.All(pair => actual.TryGetValue(pair.Key, out string? value) &&
            string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase));
    }

    private static Result<PackageManifest> ValidatePackage(
        ManagedJavaPackage package,
        string component,
        JavaArchitecture architecture)
    {
        IReadOnlyList<DownloadArtifact>? packageFiles = package.Files;
        IReadOnlyList<string>? packageDirectories = package.Directories;
        if (!string.Equals(package.Component, component, StringComparison.Ordinal) ||
            package.Architecture != architecture ||
            package.MajorVersion < 1 ||
            !IsSafeSegment(package.Component) ||
            !IsSafeSegment(package.PackageVersion) ||
            packageFiles is null || packageDirectories is null ||
            packageFiles.Count + packageDirectories.Count > 200_000 ||
            !IsSafeRelativePath(package.ExecutableRelativePath))
        {
            return Result<PackageManifest>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
        }

        List<string> directories = [];
        HashSet<string> directorySet = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in packageDirectories)
        {
            if (!IsSafeRelativePath(directory) || !directorySet.Add(NormalizePath(directory)))
            {
                return Result<PackageManifest>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }

            directories.Add(NormalizePath(directory));
        }

        List<ValidatedArtifact> files = [];
        HashSet<string> fileSet = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (DownloadArtifact artifact in packageFiles)
        {
            if (artifact is null || !IsValidArtifact(artifact) || !fileSet.Add(NormalizePath(artifact.RelativeDestinationPath)))
            {
                return Result<PackageManifest>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }

            string path = NormalizePath(artifact.RelativeDestinationPath);
            if (directorySet.Contains(path) ||
                directories.Any(directory =>
                    directory.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
            {
                return Result<PackageManifest>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }

            try
            {
                totalBytes = checked(totalBytes + artifact.ExpectedSize);
            }
            catch (OverflowException)
            {
                return Result<PackageManifest>.Failure(Problem("JAVA_RUNTIME_MANIFEST_TOO_LARGE"));
            }

            if (files.Any(existing => path.StartsWith(existing.Artifact.RelativeDestinationPath + "/", StringComparison.OrdinalIgnoreCase) ||
                                      existing.Artifact.RelativeDestinationPath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
            {
                return Result<PackageManifest>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }

            files.Add(new ValidatedArtifact(
                artifact with { RelativeDestinationPath = path },
                artifact.Hashes.Select(hash => hash.NormalizedAlgorithm).Distinct(StringComparer.Ordinal).ToArray(),
                artifact.Hashes.ToDictionary(hash => hash.NormalizedAlgorithm, hash => hash.NormalizedHexDigest, StringComparer.Ordinal),
                packageFiles.Count,
                totalBytes));
        }

        if (files.Count == 0 || totalBytes < 0 ||
            !fileSet.Contains(NormalizePath(package.ExecutableRelativePath)))
        {
            return Result<PackageManifest>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
        }

        return Result<PackageManifest>.Success(new PackageManifest(
            directories,
            files,
            NormalizePath(package.ExecutableRelativePath),
            totalBytes));
    }

    private bool IsWithinConfiguredLimits(PackageManifest manifest) =>
        manifest.Files.Count + manifest.Directories.Count <= options.MaximumFileCount &&
        manifest.TotalBytes <= options.MaximumTotalBytes;

    private static bool IsValidArtifact(DownloadArtifact artifact)
    {
        if (artifact is null || artifact.Kind != ArtifactKind.JavaRuntime || artifact.OfficialUri is null || !artifact.OfficialUri.IsAbsoluteUri || artifact.OfficialUri.Scheme != Uri.UriSchemeHttps ||
            !IsSafeRelativePath(artifact.RelativeDestinationPath) || artifact.ExpectedSize < 0 || artifact.Hashes is null || artifact.Hashes.Count == 0)
        {
            return false;
        }

        HashSet<string> algorithms = new(StringComparer.Ordinal);
        foreach (ArtifactHash? hash in artifact.Hashes)
        {
            if (hash is null || hash.Algorithm is null || hash.HexDigest is null)
            {
                return false;
            }

            string algorithm = hash.NormalizedAlgorithm;
            string digest = hash.NormalizedHexDigest;
            int expectedLength = algorithm switch
            {
                "sha1" => 40,
                "sha256" => 64,
                _ => 0,
            };
            if (expectedLength == 0 || digest.Length != expectedLength || !digest.All(char.IsAsciiHexDigit) || !algorithms.Add(algorithm))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\'))
        {
            return false;
        }

        string[] segments = path.Replace('\\', '/').Split('/');
        return segments.Length > 0 && segments.All(segment =>
            !string.IsNullOrWhiteSpace(segment) && segment is not "." and not ".." &&
            !segment.EndsWith(' ') && !segment.EndsWith('.') &&
            !segment.Any(character => char.IsControl(character) || "*?\"<>|".Contains(character)) &&
            !IsReservedWindowsName(segment));
    }

    private static bool IsSafeSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." &&
        value.Length <= 128 && !value.Contains('/') && !value.Contains('\\') &&
        !value.Contains(':') && !value.Contains('\0') &&
        !value.Any(character => char.IsControl(character) || "*?\"<>|".Contains(character)) &&
        !value.EndsWith(' ') && !value.EndsWith('.') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsReservedWindowsName(string segment)
    {
        string name = segment.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9');
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string CombineRelative(string root, string relative) =>
        Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool IsUnderRoot(string path, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePointBetween(string path, string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        DirectoryInfo? current = new(Path.GetFullPath(path));
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            current = current.Parent;
        }

        return true;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string ArchitectureName(JavaArchitecture architecture) => architecture switch
    {
        JavaArchitecture.X86 => "x86",
        JavaArchitecture.X64 => "x64",
        JavaArchitecture.Arm64 => "arm64",
        _ => "unknown",
    };

    private static Problem DownloadHashMismatch(ValidatedArtifact file) => Problem(
        "DOWNLOAD_HASH_MISMATCH",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["artifactId"] = file.Artifact.ArtifactId,
            ["path"] = file.Artifact.RelativeDestinationPath,
        });

    private static Problem Problem(string code, IReadOnlyDictionary<string, string>? context = null) => new(
        code,
        code.StartsWith("DOWNLOAD", StringComparison.Ordinal) ? ProblemStage.Download : ProblemStage.Installation,
        code == "DOWNLOAD_HASH_MISMATCH" ? "problem.download.hash_mismatch" : "problem.java.runtime_install_failed",
        code is "DOWNLOAD_HASH_MISMATCH" or "JAVA_RUNTIME_INSTALL_FAILED",
        Guid.NewGuid().ToString("N"),
        ["action.java.retry_runtime_download"],
        context);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PackageManifest(
        IReadOnlyList<string> Directories,
        IReadOnlyList<ValidatedArtifact> Files,
        string ExecutableRelativePath,
        long TotalBytes);

    private sealed record ValidatedArtifact(
        DownloadArtifact Artifact,
        IReadOnlyList<string> HashAlgorithms,
        IReadOnlyDictionary<string, string> ExpectedHashes,
        int TotalFileCount,
        long TotalBytes);

    private sealed record VerifiedArtifact(DownloadArtifact Artifact, IReadOnlyDictionary<string, string> Hashes);

    private sealed class DownloadProgressState
    {
        public int CompletedItems;
        public long CompletedBytes;
    }

    private sealed record RuntimeManifest(
        int SchemaVersion,
        string Component,
        int MajorVersion,
        string Architecture,
        string PackageVersion,
        string ExecutableRelativePath,
        RuntimeInstallation? Installation,
        IReadOnlyList<RuntimeFile>? Files);

    private sealed record RuntimeInstallation(
        string Id,
        int MajorVersion,
        string FullVersion,
        string Vendor,
        string Architecture,
        JavaSource Source,
        bool IsManaged);

    private sealed record RuntimeFile(
        string ArtifactId,
        string Path,
        long ExpectedSize,
        IReadOnlyList<RuntimeHash> Hashes);

    private sealed record RuntimeHash(string Algorithm, string HexDigest);
}
