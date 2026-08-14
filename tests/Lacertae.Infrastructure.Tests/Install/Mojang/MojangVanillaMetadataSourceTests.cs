using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lacertae.Application.Install;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Java;
using Lacertae.Domain.Versions;
using Lacertae.Infrastructure.Install.Mojang;

namespace Lacertae.Infrastructure.Tests.Install.Mojang;

public sealed class MojangVanillaMetadataSourceTests
{
    [Fact]
    public async Task GetAsyncMapsWindowsX64MetadataAndContentAddressedAssets()
    {
        var result = await CreateSource().GetAsync(
            "1.21.8",
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        var metadata = result.Value;
        Assert.Equal("1.21.8", metadata.VersionId);
        Assert.Equal("release", metadata.VersionType);
        Assert.Equal(new DateTimeOffset(2025, 7, 17, 12, 4, 2, TimeSpan.Zero), metadata.ReleaseTime);
        Assert.Equal(new JavaRequirement("java-runtime-delta", 21), metadata.Java);
        Assert.Equal("c13e92ba70ee9db6ba69c89e8f3831388d6b06c6", metadata.MetadataArtifact.Hashes.Single().NormalizedHexDigest);
        Assert.True(metadata.MetadataArtifact.ExpectedSize > 0);
        Assert.Equal("versions/1.21.8/1.21.8.jar", metadata.ClientArtifact.RelativeDestinationPath);
        Assert.Equal(ArtifactKind.ClientJar, metadata.ClientArtifact.Kind);
        Assert.Equal("assets/log_configs/client-1.21.2.xml", metadata.LoggingArtifact?.RelativeDestinationPath);
        Assert.Contains(metadata.LibraryArtifacts, artifact =>
            artifact.RelativeDestinationPath == "libraries/com/mojang/authlib/6.0.58/authlib-6.0.58.jar");
        Assert.Contains(metadata.LibraryArtifacts, artifact =>
            artifact.RelativeDestinationPath.EndsWith("jtracy-1.0.29-natives-windows.jar", StringComparison.Ordinal));
        Assert.DoesNotContain(metadata.LibraryArtifacts, artifact =>
            artifact.RelativeDestinationPath.EndsWith("jtracy-1.0.29-natives-linux.jar", StringComparison.Ordinal));
        Assert.Equal("assets/indexes/26.json", metadata.AssetIndexArtifact.RelativeDestinationPath);
        Assert.Contains(metadata.AssetObjectArtifacts, artifact =>
            artifact.RelativeDestinationPath == "assets/objects/5f/5ff04807c356f1beed0b86ccf659b44b9983e3fa");
        Assert.Contains(metadata.AssetObjectArtifacts, artifact =>
            artifact.OfficialUri.AbsoluteUri == "https://resources.download.minecraft.net/25/25fe1e45f2f3f67a59ccf53652f8e4182056f6cb");
    }

    [Fact]
    public async Task GetAsyncRejectsMissingHashOrSize()
    {
        JsonObject document = VersionDocument();
        document["downloads"]!["client"]!["sha1"] = null;

        var result = await CreateSource(document.ToJsonString()).GetAsync(
            "1.21.8",
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", result.Problem?.Code);
    }

    [Fact]
    public async Task GetAsyncRejectsUnsupportedAbsoluteDestination()
    {
        JsonObject document = VersionDocument();
        document["libraries"]![0]!["downloads"]!["artifact"]!["path"] = "/escape.jar";

        var result = await CreateSource(document.ToJsonString()).GetAsync(
            "1.21.8",
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", result.Problem?.Code);
    }

    [Fact]
    public async Task GetAsyncRejectsDuplicateDestinationWithDifferentHash()
    {
        JsonObject document = VersionDocument();
        JsonArray libraries = document["libraries"]!.AsArray();
        JsonObject duplicate = JsonNode.Parse(libraries[0]!.ToJsonString())!.AsObject();
        duplicate["downloads"]!["artifact"]!["sha1"] = new string('b', 40);
        libraries.Add(duplicate);

        var result = await CreateSource(document.ToJsonString()).GetAsync(
            "1.21.8",
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", result.Problem?.Code);
    }

    [Fact]
    public async Task GetAsyncRejectsUnknownRequiredStructureAndInheritanceCycle()
    {
        JsonObject wrongType = VersionDocument();
        wrongType["javaVersion"]!["majorVersion"] = "21";
        var wrongTypeResult = await CreateSource(wrongType.ToJsonString()).GetAsync(
            "1.21.8",
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        JsonObject cycle = VersionDocument();
        cycle["inheritsFrom"] = "1.21.8";
        var cycleResult = await CreateSource(cycle.ToJsonString()).GetAsync(
            "1.21.8",
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.False(wrongTypeResult.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", wrongTypeResult.Problem?.Code);
        Assert.False(cycleResult.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", cycleResult.Problem?.Code);
    }

    [Fact]
    public async Task GetAsyncIgnoresUnknownOptionalFields()
    {
        JsonObject document = VersionDocument();
        document["futureOptional"] = new JsonObject { ["schemaVersion"] = 99 };

        var result = await CreateSource(document.ToJsonString()).GetAsync(
            "1.21.8",
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
    }

    [Fact]
    public async Task GetAsyncMapsNativeClassifierForSelectedPlatform()
    {
        JsonObject document = VersionDocument();
        document["libraries"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "org.example:native:1.0",
            ["natives"] = new JsonObject { ["windows"] = "natives-windows" },
            ["downloads"] = new JsonObject
            {
                ["classifiers"] = new JsonObject
                {
                    ["natives-windows"] = new JsonObject
                    {
                        ["path"] = "org/example/native/1.0/native-1.0-windows.jar",
                        ["sha1"] = new string('c', 40),
                        ["size"] = 1234,
                        ["url"] = "https://libraries.minecraft.net/org/example/native/1.0/native-1.0-windows.jar",
                    },
                },
            },
        });

        var result = await CreateSource(document.ToJsonString()).GetAsync(
            "1.21.8",
            VanillaPlatform.WindowsX64,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Contains(result.Value.LibraryArtifacts, artifact =>
            artifact.RelativeDestinationPath == "libraries/org/example/native/1.0/native-1.0-windows.jar");
    }

    [Fact]
    public async Task GetAsyncVerifiesDownloadedAssetIndexBytes()
    {
        byte[] assetIndexBytes = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        JsonObject document = VersionDocument();
        JsonObject assetIndex = document["assetIndex"]!.AsObject();
        assetIndex["sha1"] = Sha1Hex(assetIndexBytes);
        assetIndex["size"] = assetIndexBytes.Length;

        var result = await CreateSource(
            document.ToJsonString(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new FixedResponseHandler(assetIndexBytes)).GetAsync(
                "1.21.8",
                VanillaPlatform.WindowsX64,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Empty(result.Value.AssetObjectArtifacts);
    }

    [Fact]
    public async Task GetAsyncRejectsDownloadedAssetIndexWhenHashOrSizeDiffers()
    {
        byte[] assetIndexBytes = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        JsonObject document = VersionDocument();
        JsonObject assetIndex = document["assetIndex"]!.AsObject();
        assetIndex["sha1"] = new string('0', 40);
        assetIndex["size"] = assetIndexBytes.Length;

        var hashResult = await CreateSource(
            document.ToJsonString(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new FixedResponseHandler(assetIndexBytes)).GetAsync(
                "1.21.8",
                VanillaPlatform.WindowsX64,
                TestContext.Current.CancellationToken);

        assetIndex["sha1"] = Sha1Hex(assetIndexBytes);
        assetIndex["size"] = assetIndexBytes.Length + 1;
        var sizeResult = await CreateSource(
            document.ToJsonString(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new FixedResponseHandler(assetIndexBytes)).GetAsync(
                "1.21.8",
                VanillaPlatform.WindowsX64,
                TestContext.Current.CancellationToken);

        Assert.False(hashResult.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", hashResult.Problem?.Code);
        Assert.False(sizeResult.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", sizeResult.Problem?.Code);
    }

    private static MojangVanillaMetadataSource CreateSource(
        string? versionJson = null,
        IReadOnlyDictionary<string, string>? assetIndexJson = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        string fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Install", "Mojang", "Fixtures");
        return new MojangVanillaMetadataSource(
            new MojangVanillaMetadataSourceOptions
            {
                VersionManifestJson = File.ReadAllText(Path.Combine(fixtureRoot, "version_manifest_v2.json")),
                VersionMetadataJson = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["1.21.8"] = versionJson ?? File.ReadAllText(Path.Combine(fixtureRoot, "version-1.21.8.json")),
                },
                AssetIndexJson = assetIndexJson ?? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["26"] = File.ReadAllText(Path.Combine(fixtureRoot, "asset-index.json")),
                },
            },
            httpMessageHandler is null ? null : new HttpClient(httpMessageHandler));
    }

    private static JsonObject VersionDocument()
    {
        string fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Install", "Mojang", "Fixtures");
        return JsonNode.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "version-1.21.8.json")))!.AsObject();
    }

    private static string Sha1Hex(byte[] bytes)
    {
#pragma warning disable CA5350
        return Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA5350
    }

    private sealed class FixedResponseHandler(byte[] responseBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes),
            });
    }
}
