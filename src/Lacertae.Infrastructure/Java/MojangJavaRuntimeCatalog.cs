using System.Text.Json;
using Lacertae.Application.Java;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Java;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Java;

public sealed class MojangJavaRuntimeCatalogOptions
{
    public Uri ProductIndexUri { get; init; } = new("https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json");
    public string? ProductIndexJson { get; init; }
    public IReadOnlyDictionary<string, string> PackageManifests { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public string? PlatformKey { get; init; }
}

public sealed class MojangJavaRuntimeCatalog : IManagedJavaCatalog
{
    private static readonly Dictionary<string, int> ComponentMajors =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["java-runtime-alpha"] = 16,
            ["java-runtime-beta"] = 17,
            ["java-runtime-delta"] = 21,
            ["java-runtime-epsilon"] = 25,
            ["java-runtime-gamma"] = 16,
            ["java-runtime-gamma-snapshot"] = 16,
        };

    private readonly MojangJavaRuntimeCatalogOptions options;
    private readonly HttpClient httpClient;

    public MojangJavaRuntimeCatalog(MojangJavaRuntimeCatalogOptions options, HttpClient? httpClient = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.httpClient = httpClient ?? new HttpClient();
    }

    public async Task<Result<ManagedJavaPackage>> GetPackageAsync(
        string component,
        JavaArchitecture architecture,
        CancellationToken cancellationToken)
    {
        if (!ComponentMajors.TryGetValue(component, out int major))
        {
            return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_UNAVAILABLE"));
        }

        try
        {
            if (options.ProductIndexJson is null &&
                (!options.ProductIndexUri.IsAbsoluteUri || options.ProductIndexUri.Scheme != Uri.UriSchemeHttps || !IsOfficialHost(options.ProductIndexUri)))
            {
                return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_UNAVAILABLE"));
            }

            using JsonDocument productIndex = JsonDocument.Parse(await LoadProductIndexAsync(cancellationToken));
            string platform = options.PlatformKey ?? architecture switch
            {
                JavaArchitecture.X64 => "windows-x64",
                JavaArchitecture.X86 => "windows-x86",
                JavaArchitecture.Arm64 => "windows-arm64",
                _ => string.Empty,
            };
            if (!productIndex.RootElement.TryGetProperty(platform, out JsonElement platformElement) ||
                !platformElement.TryGetProperty(component, out JsonElement componentEntries) ||
                componentEntries.ValueKind != JsonValueKind.Array ||
                componentEntries.GetArrayLength() == 0)
            {
                return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_UNAVAILABLE"));
            }

            JsonElement manifestInfo = componentEntries[0].GetProperty("manifest");
            string packageVersion = manifestInfo.GetProperty("sha1").GetString()!;
            if (packageVersion.Length != 40 || !packageVersion.All(char.IsAsciiHexDigit))
            {
                return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }

            Uri manifestUri = new(manifestInfo.GetProperty("url").GetString()!);
            if (!manifestUri.IsAbsoluteUri || manifestUri.Scheme != Uri.UriSchemeHttps || !IsOfficialHost(manifestUri))
            {
                return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }
            (string manifestJson, bool isInjectedFixture) = await LoadManifestAsync(component, manifestUri, cancellationToken);
            if (!isInjectedFixture)
            {
#pragma warning disable CA5350
                string actualSha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(manifestJson))).ToLowerInvariant();
#pragma warning restore CA5350
                long expectedSize = manifestInfo.GetProperty("size").GetInt64();
                if (expectedSize < 0 || expectedSize != System.Text.Encoding.UTF8.GetByteCount(manifestJson) ||
                    !string.Equals(actualSha1, packageVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
                }
            }

            return ParsePackage(component, major, architecture, packageVersion, manifestJson);
        }
        catch (JsonException)
        {
            return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or UriFormatException or ArgumentException or OverflowException)
        {
            return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
        }
        catch (HttpRequestException)
        {
            return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_UNAVAILABLE"));
        }
    }

    private async Task<string> LoadProductIndexAsync(CancellationToken cancellationToken) =>
        options.ProductIndexJson ?? await httpClient.GetStringAsync(options.ProductIndexUri, cancellationToken);

    private async Task<(string Json, bool IsInjectedFixture)> LoadManifestAsync(string component, Uri manifestUri, CancellationToken cancellationToken) =>
        options.PackageManifests.TryGetValue(component, out string? fixture)
            ? (fixture, true)
            : (await httpClient.GetStringAsync(manifestUri, cancellationToken), false);

    private static Result<ManagedJavaPackage> ParsePackage(
        string component,
        int major,
        JavaArchitecture architecture,
        string packageVersion,
        string manifestJson)
    {
        using JsonDocument manifest = JsonDocument.Parse(manifestJson);
        JsonElement filesElement = manifest.RootElement.GetProperty("files");
        List<string> directories = [];
        List<DownloadArtifact> files = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        List<string> executableCandidates = [];
        foreach (JsonProperty property in filesElement.EnumerateObject())
        {
            JsonElement entry = property.Value;
            string relativePath = property.Name.Replace('\\', '/');
            if (!IsSafeRelativePath(relativePath) || !paths.Add(relativePath))
            {
                return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }

            string type = entry.GetProperty("type").GetString()!;
            if (type == "directory")
            {
                directories.Add(relativePath);
                continue;
            }

            if (type != "file")
            {
                return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }

            if (!entry.TryGetProperty("downloads", out JsonElement downloads) ||
                !downloads.TryGetProperty("raw", out JsonElement raw))
            {
                return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }

            Uri uri = new(raw.GetProperty("url").GetString()!);
            string sha1 = raw.GetProperty("sha1").GetString()!;
            long size = raw.GetProperty("size").GetInt64();
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !IsOfficialHost(uri) || sha1.Length != 40 ||
                !sha1.All(char.IsAsciiHexDigit) || size < 0)
            {
                return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
            }
            DownloadArtifact artifact = DownloadArtifact.Create(
                ArtifactKind.JavaRuntime,
                uri,
                relativePath,
                size,
                [new ArtifactHash("sha1", sha1)]);
            files.Add(artifact);
            if (entry.TryGetProperty("executable", out JsonElement isExecutable) && isExecutable.GetBoolean())
            {
                executableCandidates.Add(relativePath);
            }
        }

        string? executable = executableCandidates.FirstOrDefault(static path =>
            string.Equals(path, "bin/java.exe", StringComparison.OrdinalIgnoreCase)) ??
            executableCandidates.FirstOrDefault(static path =>
                string.Equals(path, "bin/javaw.exe", StringComparison.OrdinalIgnoreCase)) ??
            executableCandidates.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return Result<ManagedJavaPackage>.Failure(Problem("JAVA_RUNTIME_MANIFEST_INVALID"));
        }

        return Result<ManagedJavaPackage>.Success(new ManagedJavaPackage(
            component,
            major,
            architecture,
            packageVersion,
            directories,
            files,
            executable));
    }

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.JavaResolution,
        "problem.java.runtime_unavailable",
        code == "JAVA_RUNTIME_UNAVAILABLE",
        Guid.NewGuid().ToString("N"),
        ["action.java.retry_runtime_download"]);

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\'))
        {
            return false;
        }

        return path.Split('/').All(segment => segment is not ("" or "." or "..") &&
            !segment.EndsWith(' ') && !segment.EndsWith('.') &&
            !segment.Any(character => char.IsControl(character) || "*?\"<>|".Contains(character)));
    }

    private static bool IsOfficialHost(Uri uri) => uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase);
}
