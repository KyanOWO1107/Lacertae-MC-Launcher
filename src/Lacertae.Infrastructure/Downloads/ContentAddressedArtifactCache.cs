using System.Buffers;
using System.Security.Cryptography;
using Lacertae.Application.Downloads;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Downloads;

public sealed class ContentAddressedArtifactCache : IArtifactCache
{
    private const int BufferSize = 128 * 1024;
    private readonly string rootPath;

    public ContentAddressedArtifactCache(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
    }

    public async Task<Result<string?>> GetAsync(
        DownloadArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!TryGetTrustedDigest(artifact, out string? algorithm, out string? digest))
        {
            return Result<string?>.Failure(Problem("DOWNLOAD_CACHE_INVALID", artifact));
        }

        string path = Path.Combine(rootPath, algorithm!, digest!);
        try
        {
            if (!File.Exists(path) || HasReparsePointBetween(path, rootPath))
            {
                return Result<string?>.Success(null);
            }

            if (await VerifyAsync(path, artifact, cancellationToken))
            {
                return Result<string?>.Success(path);
            }

            Quarantine(path);
            return Result<string?>.Success(null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return Result<string?>.Failure(Problem("DOWNLOAD_CACHE_UNAVAILABLE", artifact));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<string?>.Failure(Problem("DOWNLOAD_CACHE_UNAVAILABLE", artifact));
        }
    }

    public async Task<Result<Unit>> PutAsync(
        DownloadArtifact artifact,
        string verifiedFilePath,
        CancellationToken cancellationToken)
    {
        if (!TryGetTrustedDigest(artifact, out string? algorithm, out string? digest) ||
            string.IsNullOrWhiteSpace(verifiedFilePath))
        {
            return Result<Unit>.Failure(Problem("DOWNLOAD_CACHE_INVALID", artifact));
        }

        string sourcePath;
        try
        {
            sourcePath = Path.GetFullPath(verifiedFilePath);
            if (!File.Exists(sourcePath) || HasReparsePointBetween(sourcePath, Path.GetDirectoryName(sourcePath)!))
            {
                return Result<Unit>.Failure(Problem("DOWNLOAD_CACHE_INVALID", artifact));
            }

            if (!await VerifyAsync(sourcePath, artifact, cancellationToken))
            {
                return Result<Unit>.Failure(Problem("DOWNLOAD_HASH_MISMATCH", artifact));
            }

            string directory = Path.Combine(rootPath, algorithm!);
            Directory.CreateDirectory(directory);
            if (HasReparsePointBetween(directory, rootPath))
            {
                return Result<Unit>.Failure(Problem("DOWNLOAD_CACHE_INVALID", artifact));
            }

            string targetPath = Path.Combine(directory, digest!);
            if (File.Exists(targetPath) && await VerifyAsync(targetPath, artifact, cancellationToken))
            {
                return Result.Success();
            }

            if (File.Exists(targetPath))
            {
                Quarantine(targetPath);
            }

            string temporaryPath = Path.Combine(directory, $".{digest}.{Guid.NewGuid():N}.tmp");
            try
            {
                await CopyAsync(sourcePath, temporaryPath, cancellationToken);
                if (!await VerifyAsync(temporaryPath, artifact, cancellationToken))
                {
                    return Result<Unit>.Failure(Problem("DOWNLOAD_HASH_MISMATCH", artifact));
                }

                File.Move(temporaryPath, targetPath);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return Result<Unit>.Failure(Problem("DOWNLOAD_CACHE_UNAVAILABLE", artifact));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<Unit>.Failure(Problem("DOWNLOAD_CACHE_UNAVAILABLE", artifact));
        }
    }

    private static async Task CopyAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        await using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using FileStream target = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await target.FlushAsync(cancellationToken);
            target.Flush(true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> VerifyAsync(
        string path,
        DownloadArtifact artifact,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (info.Length != artifact.ExpectedSize)
        {
            return false;
        }

        Dictionary<string, IncrementalHash> hashes = artifact.Hashes
            .Select(static hash => hash.NormalizedAlgorithm)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                static algorithm => algorithm,
                static algorithm => IncrementalHash.CreateHash(AlgorithmName(algorithm)),
                StringComparer.Ordinal);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                foreach (IncrementalHash hash in hashes.Values)
                {
                    hash.AppendData(buffer, 0, read);
                }
            }

            foreach (ArtifactHash expected in artifact.Hashes)
            {
                string actual = Convert.ToHexString(hashes[expected.NormalizedAlgorithm].GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actual, expected.NormalizedHexDigest, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            foreach (IncrementalHash hash in hashes.Values)
            {
                hash.Dispose();
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryGetTrustedDigest(
        DownloadArtifact? artifact,
        out string? algorithm,
        out string? digest)
    {
        algorithm = null;
        digest = null;
        if (artifact is null || artifact.ExpectedSize <= 0 || artifact.Hashes is null || artifact.Hashes.Count == 0)
        {
            return false;
        }

        ArtifactHash? trusted = artifact.Hashes.FirstOrDefault(static hash =>
            hash is not null && hash.NormalizedAlgorithm == "sha256") ??
            artifact.Hashes.FirstOrDefault(static hash => hash is not null && hash.NormalizedAlgorithm == "sha1");
        if (trusted is null || !IsDigest(trusted.NormalizedAlgorithm, trusted.NormalizedHexDigest))
        {
            return false;
        }

        foreach (ArtifactHash? hash in artifact.Hashes)
        {
            if (hash is null || !IsDigest(hash.NormalizedAlgorithm, hash.NormalizedHexDigest))
            {
                return false;
            }
        }

        algorithm = trusted.NormalizedAlgorithm;
        digest = trusted.NormalizedHexDigest;
        return true;
    }

    private static bool IsDigest(string algorithm, string digest) =>
        algorithm is "sha1" or "sha256" &&
        digest.Length == (algorithm == "sha1" ? 40 : 64) &&
        digest.All(char.IsAsciiHexDigit);

    private static HashAlgorithmName AlgorithmName(string algorithm) => algorithm switch
    {
        "sha1" => HashAlgorithmName.SHA1,
        "sha256" => HashAlgorithmName.SHA256,
        _ => throw new InvalidOperationException("Unsupported artifact hash algorithm."),
    };

    private void Quarantine(string path)
    {
        string quarantine = Path.Combine(rootPath, ".quarantine");
        Directory.CreateDirectory(quarantine);
        string target = Path.Combine(quarantine, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".bad");
        File.Move(path, target, true);
    }

    private static bool HasReparsePointBetween(string path, string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(path)
            : Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new DirectoryInfo(Path.GetDirectoryName(path)!);
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

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }

        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Problem Problem(string code, DownloadArtifact? artifact) => new(
        code,
        ProblemStage.Download,
        code switch
        {
            "DOWNLOAD_HASH_MISMATCH" => "problem.download.hash_mismatch",
            "DOWNLOAD_CACHE_INVALID" => "problem.download.cache_invalid",
            _ => "problem.download.cache_unavailable",
        },
        code is "DOWNLOAD_CACHE_UNAVAILABLE" or "DOWNLOAD_HASH_MISMATCH",
        Guid.NewGuid().ToString("N"),
        ["action.download.retry"],
        artifact is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["artifactId"] = artifact.ArtifactId });
}
