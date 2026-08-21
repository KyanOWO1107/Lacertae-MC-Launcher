using System.Buffers;
using System.Security.Cryptography;
using Lacertae.Application.Install;
using Lacertae.Application.Storage;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Install;

public sealed class StreamingGameFileVerifier : IGameFileVerifier
{
    private const int BufferSize = 128 * 1024;

    public async Task<Result<bool>> VerifyAsync(
        DownloadArtifact artifact,
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Result<bool>.Failure(Problem("INSTALL_FILE_VERIFY_FAILED"));
        }

        try
        {
            Dictionary<string, IncrementalHash> hashes = artifact.Hashes
                .Select(static hash => hash.NormalizedAlgorithm)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(
                    static algorithm => algorithm,
                    static algorithm => IncrementalHash.CreateHash(ToAlgorithm(algorithm)),
                    StringComparer.Ordinal);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                await using Stream stream = SecureFileSystem.OpenRead(filePath);
                if (stream.Length != artifact.ExpectedSize)
                {
                    return Result<bool>.Success(false);
                }
                long readTotal = 0;
                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    readTotal = checked(readTotal + read);
                    if (readTotal > artifact.ExpectedSize)
                    {
                        return Result<bool>.Success(false);
                    }

                    foreach (IncrementalHash hash in hashes.Values)
                    {
                        hash.AppendData(buffer, 0, read);
                    }
                }

                if (readTotal != artifact.ExpectedSize)
                {
                    return Result<bool>.Success(false);
                }

                foreach (ArtifactHash expected in artifact.Hashes)
                {
                    if (!hashes.TryGetValue(expected.NormalizedAlgorithm, out IncrementalHash? hash) ||
                        !string.Equals(
                            Convert.ToHexString(hash.GetHashAndReset()),
                            expected.NormalizedHexDigest,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<bool>.Success(false);
                    }
                }

                return Result<bool>.Success(true);
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return Result<bool>.Success(false);
        }
        catch (DirectoryNotFoundException)
        {
            return Result<bool>.Success(false);
        }
        catch (IOException)
        {
            return Result<bool>.Failure(Problem("INSTALL_FILE_VERIFY_FAILED", retryable: true));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<bool>.Failure(Problem("INSTALL_FILE_VERIFY_FAILED"));
        }
    }

    private static HashAlgorithmName ToAlgorithm(string algorithm) => algorithm switch
    {
        "sha1" => HashAlgorithmName.SHA1,
        "sha256" => HashAlgorithmName.SHA256,
        _ => throw new InvalidOperationException("Unsupported artifact hash algorithm."),
    };

    private static Problem Problem(string code, bool retryable = false) => new(
        code,
        ProblemStage.Installation,
        "problem.install.file_verify_failed",
        retryable,
        Guid.NewGuid().ToString("N"),
        ["action.install.retry"]);
}
