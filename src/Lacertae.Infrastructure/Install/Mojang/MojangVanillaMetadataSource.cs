using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lacertae.Application.Install;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Infrastructure.Install.Mojang;

public sealed class MojangVanillaMetadataSourceOptions
{
    public Uri VersionManifestUri { get; init; } = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    public string? VersionManifestJson { get; init; }
    public IReadOnlyDictionary<string, string> VersionMetadataJson { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> AssetIndexJson { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public int MaximumManifestBytes { get; init; } = 10 * 1024 * 1024;
    public int MaximumVersionBytes { get; init; } = 10 * 1024 * 1024;
    public int MaximumAssetIndexBytes { get; init; } = 32 * 1024 * 1024;
}

public sealed class MojangVanillaMetadataSource : IVanillaMetadataSource, IVanillaVersionCatalog
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly MojangVanillaMetadataSourceOptions options;
    private readonly HttpClient httpClient;

    public MojangVanillaMetadataSource(
        MojangVanillaMetadataSourceOptions options,
        HttpClient? httpClient = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    public async Task<Result<VanillaMetadataSnapshot>> GetAsync(
        string versionId,
        VanillaPlatform platform,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(versionId) || platform is null ||
            string.IsNullOrWhiteSpace(platform.OsName) || string.IsNullOrWhiteSpace(platform.Architecture))
        {
            return Result<VanillaMetadataSnapshot>.Failure(InvalidProblem());
        }

        try
        {
            string manifestJson = options.VersionManifestJson ?? await LoadTextAsync(
                options.VersionManifestUri,
                options.MaximumManifestBytes,
                cancellationToken);
            using JsonDocument manifestDocument = JsonDocument.Parse(manifestJson);
            JsonElement versionEntry = FindVersionEntry(manifestDocument.RootElement, versionId);
            string metadataUriText = StrictJsonReader.RequiredString(versionEntry, "url");
            Uri metadataUri = ParseOfficialUri(metadataUriText, "version metadata");
            string metadataSha1 = RequiredSha1(versionEntry, "sha1");

            bool injectedMetadata = options.VersionMetadataJson.TryGetValue(versionId, out string? injectedVersionJson);
            string versionJson = injectedMetadata
                ? injectedVersionJson!
                : await LoadTextAsync(metadataUri, options.MaximumVersionBytes, cancellationToken);
            byte[] versionBytes = StrictUtf8.GetBytes(versionJson);
            if (!injectedMetadata && !Sha1Matches(versionBytes, metadataSha1))
            {
                return Result<VanillaMetadataSnapshot>.Failure(InvalidProblem());
            }

            using JsonDocument versionDocument = JsonDocument.Parse(versionJson);
            await EnsureNoInheritanceCycleAsync(versionId, versionDocument.RootElement, [], cancellationToken);
            VanillaMetadataSnapshot metadata = await ParseVersionAsync(
                versionId,
                metadataUri,
                metadataSha1,
                versionBytes.Length,
                versionDocument.RootElement,
                platform,
                cancellationToken);
            return Result<VanillaMetadataSnapshot>.Success(metadata);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Result<VanillaMetadataSnapshot>.Failure(UnavailableProblem());
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or UriFormatException or ArgumentException or OverflowException or DecoderFallbackException)
        {
            return Result<VanillaMetadataSnapshot>.Failure(InvalidProblem());
        }
    }

    public async Task<Result<IReadOnlyList<VanillaVersionSummary>>> ListAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            string manifestJson = options.VersionManifestJson ?? await LoadTextAsync(
                options.VersionManifestUri,
                options.MaximumManifestBytes,
                cancellationToken);
            using JsonDocument document = JsonDocument.Parse(manifestJson);
            JsonElement versions = StrictJsonReader.RequiredProperty(
                document.RootElement,
                "versions",
                JsonValueKind.Array);
            List<VanillaVersionSummary> result = [];
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (JsonElement version in versions.EnumerateArray())
            {
                if (version.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Version manifest entry is not an object.");
                }

                string id = StrictJsonReader.RequiredString(version, "id");
                string type = StrictJsonReader.RequiredString(version, "type");
                DateTimeOffset releaseTime = StrictJsonReader.RequiredDateTimeOffset(version, "releaseTime");
                Uri metadataUri = ParseOfficialUri(
                    StrictJsonReader.RequiredString(version, "url"),
                    "version metadata");
                string metadataSha1 = RequiredSha1(version, "sha1");
                if (!IsSafeSegment(id) || string.IsNullOrWhiteSpace(type) || !ids.Add(id))
                {
                    throw new InvalidDataException("Version manifest entry is invalid or duplicated.");
                }

                result.Add(new VanillaVersionSummary(id, type, releaseTime, metadataUri, metadataSha1));
            }

            return Result<IReadOnlyList<VanillaVersionSummary>>.Success(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Result<IReadOnlyList<VanillaVersionSummary>>.Failure(UnavailableProblem());
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or UriFormatException or ArgumentException or OverflowException or DecoderFallbackException)
        {
            return Result<IReadOnlyList<VanillaVersionSummary>>.Failure(InvalidProblem());
        }
    }

    private async Task<VanillaMetadataSnapshot> ParseVersionAsync(
        string requestedVersionId,
        Uri metadataUri,
        string metadataSha1,
        int metadataSize,
        JsonElement root,
        VanillaPlatform platform,
        CancellationToken cancellationToken)
    {
        string id = StrictJsonReader.RequiredString(root, "id");
        if (!string.Equals(id, requestedVersionId, StringComparison.Ordinal) || !IsSafeSegment(id))
        {
            throw new InvalidDataException("Version ID does not match the requested release.");
        }

        string type = StrictJsonReader.RequiredString(root, "type");
        DateTimeOffset releaseTime = StrictJsonReader.RequiredDateTimeOffset(root, "releaseTime");
        JsonElement javaVersion = StrictJsonReader.RequiredProperty(root, "javaVersion", JsonValueKind.Object);
        string javaComponent = StrictJsonReader.RequiredString(javaVersion, "component");
        int javaMajor = StrictJsonReader.RequiredInt(javaVersion, "majorVersion");
        if (javaMajor < 1)
        {
            throw new InvalidDataException("Java major version is invalid.");
        }

        JsonElement downloads = StrictJsonReader.RequiredProperty(root, "downloads", JsonValueKind.Object);
        DownloadArtifact client = ParseDownloadArtifact(
            StrictJsonReader.RequiredProperty(downloads, "client", JsonValueKind.Object),
            ArtifactKind.ClientJar,
            $"versions/{id}/{id}.jar",
            ["piston-data.mojang.com"]);

        DownloadArtifact? logging = ParseLogging(root);
        JsonElement assetIndexInfo = StrictJsonReader.RequiredProperty(root, "assetIndex", JsonValueKind.Object);
        string assetIndexId = StrictJsonReader.RequiredString(assetIndexInfo, "id");
        string assetIndexSha1 = RequiredSha1(assetIndexInfo, "sha1");
        long assetIndexSize = StrictJsonReader.RequiredLong(assetIndexInfo, "size");
        Uri assetIndexUri = ParseOfficialUri(StrictJsonReader.RequiredString(assetIndexInfo, "url"), "asset index");
        if (assetIndexSize <= 0)
        {
            throw new InvalidDataException("Asset index size is invalid.");
        }

        bool injectedAssetIndex = options.AssetIndexJson.TryGetValue(assetIndexId, out string? injectedAssetIndexJson);
        string assetIndexJson;
        if (injectedAssetIndex)
        {
            assetIndexJson = injectedAssetIndexJson!;
        }
        else
        {
            if (assetIndexSize > options.MaximumAssetIndexBytes)
            {
                throw new InvalidDataException("Asset index is larger than the configured response limit.");
            }

            byte[] assetIndexBytes = await LoadBytesAsync(
                assetIndexUri,
                options.MaximumAssetIndexBytes,
                cancellationToken);
            if (assetIndexBytes.LongLength != assetIndexSize || !Sha1Matches(assetIndexBytes, assetIndexSha1))
            {
                throw new InvalidDataException("Asset index bytes do not match Mojang metadata.");
            }

            assetIndexJson = StrictUtf8.GetString(assetIndexBytes);
        }
        List<DownloadArtifact> assets = ParseAssetObjects(assetIndexJson);
        DownloadArtifact assetIndex = DownloadArtifact.Create(
            ArtifactKind.AssetIndex,
            assetIndexUri,
            $"assets/indexes/{assetIndexId}.json",
            assetIndexSize,
            [new ArtifactHash("sha1", assetIndexSha1)]);

        JsonElement librariesElement = StrictJsonReader.RequiredProperty(root, "libraries", JsonValueKind.Array);
        List<DownloadArtifact> libraries = ParseLibraries(librariesElement, platform);
        List<DownloadArtifact> allArtifacts = [client];
        if (logging is not null)
        {
            allArtifacts.Add(logging);
        }

        allArtifacts.Add(assetIndex);
        allArtifacts.AddRange(libraries);
        allArtifacts.AddRange(assets);
        EnsureNoConflictingDestinations(allArtifacts);

        DownloadArtifact metadataArtifact = DownloadArtifact.Create(
            ArtifactKind.VersionMetadata,
            metadataUri,
            $"versions/{id}/{id}.json",
            metadataSize,
            [new ArtifactHash("sha1", metadataSha1)]);
        return new VanillaMetadataSnapshot(
            id,
            type,
            releaseTime,
            new JavaRequirement(javaComponent, javaMajor),
            metadataArtifact,
            client,
            logging,
            libraries,
            assetIndex,
            assets);
    }

    private static DownloadArtifact? ParseLogging(JsonElement root)
    {
        if (!root.TryGetProperty("logging", out JsonElement logging))
        {
            return null;
        }

        JsonElement loggingObject = StrictJsonReader.RequiredProperty(logging, "client", JsonValueKind.Object);
        JsonElement file = StrictJsonReader.RequiredProperty(loggingObject, "file", JsonValueKind.Object);
        string id = StrictJsonReader.RequiredString(file, "id");
        return ParseDownloadArtifact(
            file,
            ArtifactKind.LoggingConfiguration,
            $"assets/log_configs/{id}",
            ["piston-data.mojang.com"]);
    }

    private static List<DownloadArtifact> ParseLibraries(JsonElement libraries, VanillaPlatform platform)
    {
        List<DownloadArtifact> result = [];
        foreach (JsonElement library in libraries.EnumerateArray())
        {
            if (library.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Library entry is not an object.");
            }

            bool allowed = EvaluateRules(library, platform);
            if (!allowed)
            {
                continue;
            }

            JsonElement downloads = StrictJsonReader.RequiredProperty(library, "downloads", JsonValueKind.Object);
            bool added = false;
            if (downloads.TryGetProperty("artifact", out JsonElement artifact))
            {
                result.Add(ParseDownloadArtifact(artifact, ArtifactKind.Library,
                    $"libraries/{StrictJsonReader.RequiredString(artifact, "path")}",
                    ["libraries.minecraft.net"]));
                added = true;
            }

            if (downloads.TryGetProperty("classifiers", out JsonElement classifiers) &&
                classifiers.ValueKind == JsonValueKind.Object &&
                library.TryGetProperty("natives", out JsonElement natives) &&
                natives.ValueKind == JsonValueKind.Object &&
                natives.TryGetProperty(platform.OsName, out JsonElement classifierNameElement) &&
                classifierNameElement.ValueKind == JsonValueKind.String)
            {
                string classifierName = classifierNameElement.GetString()!;
                if (classifiers.TryGetProperty(classifierName, out JsonElement classifier))
                {
                    result.Add(ParseDownloadArtifact(classifier, ArtifactKind.Library,
                        $"libraries/{StrictJsonReader.RequiredString(classifier, "path")}",
                        ["libraries.minecraft.net"]));
                    added = true;
                }
            }

            if (!added)
            {
                throw new InvalidDataException("Allowed library has no platform artifact.");
            }
        }

        return result;
    }

    private static bool EvaluateRules(JsonElement library, VanillaPlatform platform)
    {
        if (!library.TryGetProperty("rules", out JsonElement rules))
        {
            return true;
        }

        if (rules.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Library rules are not an array.");
        }

        bool allowed = false;
        foreach (JsonElement rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Library rule is not an object.");
            }

            string action = StrictJsonReader.RequiredString(rule, "action");
            if (action is not ("allow" or "disallow"))
            {
                throw new InvalidDataException("Library rule action is unsupported.");
            }

            if (RuleMatches(rule, platform))
            {
                allowed = action == "allow";
            }
        }

        return allowed;
    }

    private static bool RuleMatches(JsonElement rule, VanillaPlatform platform)
    {
        if (!rule.TryGetProperty("os", out JsonElement os))
        {
            return true;
        }

        if (os.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Library rule OS is not an object.");
        }

        if (os.TryGetProperty("name", out JsonElement name))
        {
            if (name.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Library rule OS name is not a string.");
            }

            if (!string.Equals(name.GetString(), platform.OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (os.TryGetProperty("arch", out JsonElement arch))
        {
            if (arch.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Library rule architecture is not a string.");
            }

            return ArchitectureMatches(arch.GetString()!, platform.Architecture);
        }

        if (os.TryGetProperty("version", out JsonElement version))
        {
            if (version.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Library rule OS version is not a string.");
            }

            return platform.OsVersion is not null &&
                System.Text.RegularExpressions.Regex.IsMatch(platform.OsVersion, version.GetString()!, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        return true;
    }

    private static bool ArchitectureMatches(string required, string actual) =>
        NormalizeArchitecture(required) == NormalizeArchitecture(actual);

    private static string NormalizeArchitecture(string value) => value.Trim().ToLowerInvariant() switch
    {
        "x86" or "i386" or "i686" => "x86",
        "x64" or "amd64" or "x86_64" => "x64",
        "arm64" or "aarch64" => "arm64",
        _ => value.Trim().ToLowerInvariant(),
    };

    private static List<DownloadArtifact> ParseAssetObjects(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement objects = StrictJsonReader.RequiredProperty(document.RootElement, "objects", JsonValueKind.Object);
        List<DownloadArtifact> result = [];
        foreach (JsonProperty property in objects.EnumerateObject())
        {
            if (!IsSafeRelativePath(property.Name))
            {
                throw new InvalidDataException("Asset path is unsafe.");
            }

            string hash = RequiredSha1(property.Value, "hash");
            long size = StrictJsonReader.RequiredLong(property.Value, "size");
            if (size <= 0)
            {
                throw new InvalidDataException("Asset size is invalid.");
            }

            Uri uri = new($"https://resources.download.minecraft.net/{hash[..2]}/{hash}");
            result.Add(DownloadArtifact.Create(
                ArtifactKind.AssetObject,
                uri,
                $"assets/objects/{hash[..2]}/{hash}",
                size,
                [new ArtifactHash("sha1", hash)]));
        }

        return result;
    }

    private static DownloadArtifact ParseDownloadArtifact(
        JsonElement element,
        ArtifactKind kind,
        string destination,
        IReadOnlyList<string> officialHosts)
    {
        if (kind == ArtifactKind.Library)
        {
            string path = StrictJsonReader.RequiredString(element, "path");
            if (!string.Equals(path.Replace('\\', '/'), destination[(destination.IndexOf('/') + 1)..], StringComparison.Ordinal))
            {
                throw new InvalidDataException("Library destination does not match metadata path.");
            }
        }

        string url = StrictJsonReader.RequiredString(element, "url");
        Uri uri = ParseOfficialUri(url, kind.ToString());
        if (!officialHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Artifact URL is not from the expected official host.");
        }

        string sha1 = RequiredSha1(element, "sha1");
        long size = StrictJsonReader.RequiredLong(element, "size");
        if (size <= 0)
        {
            throw new InvalidDataException("Artifact size is invalid.");
        }

        return DownloadArtifact.Create(kind, uri, destination, size, [new ArtifactHash("sha1", sha1)]);
    }

    private static JsonElement FindVersionEntry(JsonElement root, string versionId)
    {
        JsonElement versions = StrictJsonReader.RequiredProperty(root, "versions", JsonValueKind.Array);
        foreach (JsonElement version in versions.EnumerateArray())
        {
            if (version.ValueKind == JsonValueKind.Object &&
                version.TryGetProperty("id", out JsonElement id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), versionId, StringComparison.Ordinal))
            {
                return version;
            }
        }

        throw new InvalidDataException("Requested version is not in the official manifest.");
    }

    private async Task EnsureNoInheritanceCycleAsync(
        string versionId,
        JsonElement version,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(versionId))
        {
            throw new InvalidDataException("Version inheritance contains a cycle.");
        }

        if (version.TryGetProperty("inheritsFrom", out JsonElement parent))
        {
            if (parent.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(parent.GetString()))
            {
                throw new InvalidDataException("Version inheritance is invalid.");
            }

            string parentId = parent.GetString()!;
            if (!options.VersionMetadataJson.TryGetValue(parentId, out string? parentJson))
            {
                throw new InvalidDataException("Version inheritance parent is unavailable in the frozen source.");
            }

            using JsonDocument parentDocument = JsonDocument.Parse(parentJson);
            await EnsureNoInheritanceCycleAsync(parentId, parentDocument.RootElement, visited, cancellationToken);
        }

        visited.Remove(versionId);
        await Task.CompletedTask;
    }

    private async Task<string> LoadTextAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        byte[] bytes = await LoadBytesAsync(uri, maximumBytes, cancellationToken);
        return StrictUtf8.GetString(bytes);
    }

    private async Task<byte[]> LoadBytesAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
        {
            throw new InvalidDataException("Metadata response limit is invalid.");
        }

        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !IsOfficialHost(uri))
        {
            throw new InvalidDataException("Metadata URL is not official HTTPS.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new InvalidDataException("Metadata redirect requires source-policy handling.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException("Metadata response is too large.");
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        if (bytes.LongLength > maximumBytes)
        {
            throw new InvalidDataException("Metadata response is too large.");
        }

        return bytes;
    }

    private static Uri ParseOfficialUri(string value, string resourceName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !IsOfficialHost(uri))
        {
            throw new InvalidDataException($"{resourceName} URL is not official HTTPS.");
        }

        return uri;
    }

    private static string RequiredSha1(JsonElement element, string propertyName)
    {
        string sha1 = StrictJsonReader.RequiredString(element, propertyName).ToLowerInvariant();
        if (sha1.Length != 40 || !sha1.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException($"SHA-1 property '{propertyName}' is invalid.");
        }

        return sha1;
    }

    private static bool Sha1Matches(byte[] bytes, string expected)
    {
#pragma warning disable CA5350
        string actual = Convert.ToHexString(SHA1.HashData(bytes));
#pragma warning restore CA5350
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeSegment(string value) =>
        value.Length <= 128 && value is not "." and not ".." &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Contains('\0') &&
        !path.Replace('\\', '/').StartsWith('/') &&
        !path.Replace('\\', '/').Contains(':') &&
        path.Replace('\\', '/').Split('/').All(segment => segment is not ("" or "." or ".."));

    private static void EnsureNoConflictingDestinations(IReadOnlyList<DownloadArtifact> artifacts)
    {
        Dictionary<string, DownloadArtifact> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (DownloadArtifact artifact in artifacts)
        {
            if (!paths.TryAdd(artifact.RelativeDestinationPath, artifact) &&
                !ArtifactsEqual(paths[artifact.RelativeDestinationPath], artifact))
            {
                throw new InvalidDataException("Two artifacts have conflicting destinations.");
            }
        }
    }

    private static bool ArtifactsEqual(DownloadArtifact left, DownloadArtifact right) =>
        left.ExpectedSize == right.ExpectedSize &&
        left.Hashes.Count == right.Hashes.Count &&
        left.Hashes.All(leftHash => right.Hashes.Any(rightHash =>
            string.Equals(leftHash.NormalizedAlgorithm, rightHash.NormalizedAlgorithm, StringComparison.Ordinal) &&
            string.Equals(leftHash.NormalizedHexDigest, rightHash.NormalizedHexDigest, StringComparison.OrdinalIgnoreCase)));

    private static bool IsOfficialHost(Uri uri) =>
        uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase);

    private static Problem InvalidProblem() => new(
        "VERSION_METADATA_INVALID",
        ProblemStage.VersionResolution,
        "problem.version.metadata_invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.version.review_metadata"]);

    private static Problem UnavailableProblem() => new(
        "VERSION_METADATA_UNAVAILABLE",
        ProblemStage.VersionResolution,
        "problem.version.metadata_unavailable",
        true,
        Guid.NewGuid().ToString("N"),
        ["action.version.retry_metadata"]);
}
