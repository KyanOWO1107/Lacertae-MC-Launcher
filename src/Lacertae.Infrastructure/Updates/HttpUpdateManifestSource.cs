using System.Globalization;
using System.Net;
using System.Text.Json;
using Lacertae.Application.Updates;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Updates;

namespace Lacertae.Infrastructure.Updates;

public sealed record HttpUpdateManifestSourceOptions(
    Uri ManifestUri,
    Uri SignatureUri,
    int MaximumManifestBytes = 1024 * 1024,
    int MaximumSignatureBytes = 256);

/// <summary>
/// Fetches a manifest and detached signature from owner-configured HTTPS
/// endpoints. It does not follow redirects or accept arbitrary URLs.
/// </summary>
public sealed class HttpUpdateManifestSource : IUpdateManifestSource
{
    private readonly HttpClient httpClient;
    private readonly HttpUpdateManifestSourceOptions options;

    public HttpUpdateManifestSource(
        HttpUpdateManifestSourceOptions options,
        HttpClient? httpClient = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        this.httpClient = httpClient ?? CreateDefaultClient();
    }

    public async Task<Result<UpdateManifestEnvelope>> FetchAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(channel))
        {
            return Failure<UpdateManifestEnvelope>("UPDATE_CHANNEL_INVALID");
        }

        try
        {
            byte[] manifestBytes = await GetBoundedAsync(
                options.ManifestUri,
                options.MaximumManifestBytes,
                cancellationToken);
            byte[] signature = await GetBoundedAsync(
                options.SignatureUri,
                options.MaximumSignatureBytes,
                cancellationToken);
            Result<UpdateManifest> parsed = ParseManifest(manifestBytes);
            if (!parsed.IsSuccess)
            {
                return Result<UpdateManifestEnvelope>.Failure(parsed.Problem!);
            }

            if (parsed.Value.Channel != channel)
            {
                return Failure<UpdateManifestEnvelope>("UPDATE_CHANNEL_MISMATCH");
            }

            return Result<UpdateManifestEnvelope>.Success(new UpdateManifestEnvelope(
                parsed.Value,
                signature,
                manifestBytes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Failure<UpdateManifestEnvelope>("UPDATE_CHECK_FAILED", retryable: true);
        }
        catch (InvalidDataException)
        {
            return Failure<UpdateManifestEnvelope>("UPDATE_MANIFEST_INVALID");
        }
        catch (JsonException)
        {
            return Failure<UpdateManifestEnvelope>("UPDATE_MANIFEST_INVALID");
        }
    }

    private async Task<byte[]> GetBoundedAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is not (>= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices))
        {
            throw new HttpRequestException($"Update endpoint returned {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is long length && (length < 0 || length > maximumBytes))
        {
            throw new InvalidDataException("Update response exceeded the configured limit.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Update response exceeded the configured limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    private static Result<UpdateManifest> ParseManifest(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root))
        {
            return Failure<UpdateManifest>("UPDATE_MANIFEST_INVALID");
        }

        HashSet<string> allowed =
        [
            "schemaVersion",
            "keyId",
            "channel",
            "version",
            "publishedUtc",
            "minimumLauncherVersion",
            "releaseNotes",
            "releaseNotesUrl",
            "package",
        ];
        if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
        {
            return Failure<UpdateManifest>("UPDATE_MANIFEST_UNKNOWN_FIELD");
        }

        if (!TryGetInt(root, "schemaVersion", out int schemaVersion) ||
            !TryGetString(root, "keyId", out string? keyId) ||
            !TryGetString(root, "channel", out string? channelText) ||
            !TryGetString(root, "version", out string? version) ||
            !TryGetString(root, "publishedUtc", out string? publishedText) ||
            !TryGetString(root, "minimumLauncherVersion", out string? minimumLauncherVersion) ||
            !TryGetString(root, "releaseNotesUrl", out string? releaseNotesUrlText) ||
            !Uri.TryCreate(releaseNotesUrlText, UriKind.Absolute, out Uri? releaseNotesUrl) ||
            !IsHttpsUri(releaseNotesUrl) ||
            !TryParseChannel(channelText, out UpdateChannel channel) ||
            !DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset publishedUtc) ||
            !TryParseNotes(root, out IReadOnlyDictionary<string, string>? releaseNotes) ||
            !TryParsePackage(root, out UpdatePackage? package))
        {
            return Failure<UpdateManifest>("UPDATE_MANIFEST_INVALID");
        }

        return Result<UpdateManifest>.Success(new UpdateManifest(
            schemaVersion,
            keyId!,
            channel,
            version!,
            publishedUtc,
            minimumLauncherVersion!,
            releaseNotes!,
            releaseNotesUrl!,
            package!));
    }

    private static bool TryParsePackage(JsonElement root, out UpdatePackage? package)
    {
        package = null;
        if (!root.TryGetProperty("package", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object || HasDuplicateProperties(value))
        {
            return false;
        }

        HashSet<string> allowed = ["runtime", "url", "size", "sha256", "fileManifestSha256"];
        if (value.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
            !TryGetString(value, "runtime", out string? runtime) ||
            !TryGetString(value, "url", out string? urlText) ||
            !Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url) ||
            !IsHttpsUri(url) ||
            !TryGetLong(value, "size", out long size) ||
            !TryGetString(value, "sha256", out string? sha256) ||
            !TryGetString(value, "fileManifestSha256", out string? fileManifestSha256))
        {
            return false;
        }

        package = new UpdatePackage(runtime!, url!, size, sha256!, fileManifestSha256!);
        return true;
    }

    private static bool TryParseNotes(JsonElement root, out IReadOnlyDictionary<string, string>? notes)
    {
        notes = null;
        if (!root.TryGetProperty("releaseNotes", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object || HasDuplicateProperties(value))
        {
            return false;
        }

        Dictionary<string, string> parsed = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || property.Name.Length > 16)
            {
                return false;
            }

            string? text = property.Value.GetString();
            if (text is null || text.Length > 65536 || !parsed.TryAdd(property.Name, text))
            {
                return false;
            }
        }

        notes = parsed;
        return parsed.Count is >= 1 and <= 16;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        return element.EnumerateObject().Any(property => !names.Add(property.Name));
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        if (element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = default;
        return element.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static bool TryGetLong(JsonElement element, string name, out long value)
    {
        value = default;
        return element.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value);
    }

    private static bool TryParseChannel(string? value, out UpdateChannel channel) => value switch
    {
        "stable" => Set(UpdateChannel.Stable, out channel),
        "preview" => Set(UpdateChannel.Preview, out channel),
        "nightly" => Set(UpdateChannel.Nightly, out channel),
        _ => Set(default, out channel, false),
    };

    private static bool Set(UpdateChannel value, out UpdateChannel target, bool success = true)
    {
        target = value;
        return success;
    }

    private static void ValidateOptions(HttpUpdateManifestSourceOptions options)
    {
        if (!IsValidEndpoint(options.ManifestUri) || !IsValidEndpoint(options.SignatureUri) ||
            options.MaximumManifestBytes is <= 0 or > 1024 * 1024 ||
            options.MaximumSignatureBytes is <= 0 or > 4096)
        {
            throw new ArgumentException("Update endpoint options are invalid.", nameof(options));
        }
    }

    private static bool IsValidEndpoint(Uri? uri) =>
        uri is not null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && string.IsNullOrEmpty(uri.Query);

    private static bool IsHttpsUri(Uri? uri) =>
        uri is not null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static HttpClient CreateDefaultClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static Result<T> Failure<T>(string code, bool retryable = false) => Result<T>.Failure(new Problem(
        code,
        ProblemStage.Update,
        "problem.update.check_failed",
        retryable,
        Guid.NewGuid().ToString("N"),
        ["action.update.retry"]));
}
