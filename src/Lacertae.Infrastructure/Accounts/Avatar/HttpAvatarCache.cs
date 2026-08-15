using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Lacertae.Application.Accounts;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Accounts.Avatar;

public sealed class HttpAvatarCache : IAvatarCache, IDisposable
{
    private const int MaximumAvatarBytes = 1 * 1024 * 1024;
    private const int BufferSize = 64 * 1024;
    private static readonly Uri TextureRoot = new("https://textures.minecraft.net/texture/");

    private readonly string avatarRoot;
    private readonly string partRoot;
    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;
    private readonly bool ownsHttpClient;

    public HttpAvatarCache(string localPath, HttpClient? httpClient = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        string normalizedLocalPath = Path.GetFullPath(localPath);
        avatarRoot = Path.Combine(normalizedLocalPath, "cache", "avatars");
        partRoot = Path.Combine(avatarRoot, ".part");
        this.httpClient = httpClient ?? CreateDefaultHttpClient();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        ownsHttpClient = httpClient is null;
    }

    public async Task<Result<AvatarCacheResult>> RefreshAsync(
        Uri? skinUri,
        CancellationToken cancellationToken)
    {
        DateTimeOffset checkedUtc = timeProvider.GetUtcNow();
        if (!TryNormalizeSkinUri(skinUri, out Uri? trustedUri))
        {
            return Placeholder(checkedUtc);
        }

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(partRoot);
            if (HasReparsePointBetween(partRoot, avatarRoot))
            {
                return Placeholder(checkedUtc);
            }

            temporaryPath = Path.Combine(partRoot, Guid.NewGuid().ToString("N") + ".part");
            using HttpRequestMessage request = new(HttpMethod.Get, trustedUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode ||
                (response.RequestMessage?.RequestUri is Uri requestUri && !TryNormalizeSkinUri(requestUri, out _)) ||
                !IsPngContentType(response.Content.Headers.ContentType) ||
                response.Content.Headers.ContentLength > MaximumAvatarBytes)
            {
                return Placeholder(checkedUtc);
            }

            byte[] digest;
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream target = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan))
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                long total = 0;
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                    {
                        total += read;
                        if (total > MaximumAvatarBytes)
                        {
                            return Placeholder(checkedUtc);
                        }

                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }

                    await target.FlushAsync(cancellationToken);
                    target.Flush(flushToDisk: true);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                digest = hash.GetHashAndReset();
            }

            byte[] content = await File.ReadAllBytesAsync(temporaryPath, cancellationToken);
            if (!PngValidator.TryValidate(content, out _))
            {
                return Placeholder(checkedUtc);
            }

            string cacheKey = Convert.ToHexString(digest).ToLowerInvariant();
            string finalPath = Path.Combine(avatarRoot, cacheKey + ".png");
            if (HasReparsePointBetween(finalPath, avatarRoot))
            {
                return Placeholder(checkedUtc);
            }

            if (!File.Exists(finalPath))
            {
                File.Move(temporaryPath, finalPath);
                temporaryPath = null;
            }

            return Result<AvatarCacheResult>.Success(new AvatarCacheResult(cacheKey, false, checkedUtc));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Placeholder(checkedUtc);
        }
        catch (HttpRequestException)
        {
            return Placeholder(checkedUtc);
        }
        catch (IOException)
        {
            return Placeholder(checkedUtc);
        }
        catch (UnauthorizedAccessException)
        {
            return Placeholder(checkedUtc);
        }
        catch (CryptographicException)
        {
            return Placeholder(checkedUtc);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDelete(temporaryPath);
            }
        }
    }

    public string? ResolvePath(string? cacheKey)
    {
        if (!IsCacheKey(cacheKey))
        {
            return null;
        }

        string path = Path.Combine(avatarRoot, cacheKey + ".png");
        return File.Exists(path) && !HasReparsePointBetween(path, avatarRoot)
            ? path
            : null;
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseDefaultCredentials = false,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private static bool TryNormalizeSkinUri(Uri? uri, out Uri? trustedUri)
    {
        trustedUri = null;
        if (uri is null || !uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, TextureRoot.Host, StringComparison.OrdinalIgnoreCase) ||
            (!uri.IsDefaultPort && uri.Port != TextureRoot.Port) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        const string prefix = "/texture/";
        string path = uri.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length != prefix.Length + 64)
        {
            return false;
        }

        string digest = path[prefix.Length..];
        if (digest.Any(static character =>
                !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))))
        {
            return false;
        }

        trustedUri = uri;
        return true;
    }

    private static bool IsPngContentType(MediaTypeHeaderValue? contentType) =>
        contentType is not null &&
        string.Equals(contentType.MediaType, "image/png", StringComparison.OrdinalIgnoreCase);

    private static bool IsCacheKey(string? cacheKey) =>
        cacheKey is not null && cacheKey.Length == 64 &&
        cacheKey.All(static character =>
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));

    private static Result<AvatarCacheResult> Placeholder(DateTimeOffset checkedUtc) =>
        Result<AvatarCacheResult>.Success(new AvatarCacheResult(null, true, checkedUtc));

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

            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(current.FullName),
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
