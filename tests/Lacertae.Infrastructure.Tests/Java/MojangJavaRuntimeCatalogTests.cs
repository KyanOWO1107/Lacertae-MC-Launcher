using Lacertae.Domain.Downloads;
using Lacertae.Domain.Java;
using Lacertae.Infrastructure.Java;

namespace Lacertae.Infrastructure.Tests.Java;

public sealed class MojangJavaRuntimeCatalogTests
{
    [Theory]
    [InlineData("java-runtime-beta", 17, "89ce85ccb518c62e18b4b58d63399ba2d9611426")]
    [InlineData("java-runtime-delta", 21, "cb4394a27089d19f65d5baa6cf0482c27c3c7865")]
    public async Task GetPackageAsyncMapsWindowsX64ProductIndex(
        string component,
        int expectedMajor,
        string expectedPackageSha1)
    {
        MojangJavaRuntimeCatalog catalog = new(new MojangJavaRuntimeCatalogOptions
        {
            ProductIndexUri = new Uri("https://example.test/all.json"),
            ProductIndexJson = Fixture("all.json"),
            PackageManifests = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["java-runtime-beta"] = BetaManifest(),
                ["java-runtime-delta"] = DeltaManifest(),
            },
        });

        var result = await catalog.GetPackageAsync(
            component,
            JavaArchitecture.X64,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(component, result.Value.Component);
        Assert.Equal(expectedMajor, result.Value.MajorVersion);
        Assert.Equal(JavaArchitecture.X64, result.Value.Architecture);
        Assert.Equal(expectedPackageSha1, result.Value.PackageVersion);
        Assert.Contains("bin", result.Value.Directories);
        DownloadArtifact javaw = Assert.Single(result.Value.Files, file => file.RelativeDestinationPath == "bin/javaw.exe");
        Assert.Equal(39424, javaw.ExpectedSize);
        Assert.Equal("sha1", javaw.Hashes[0].Algorithm);
        Assert.Equal(component == "java-runtime-beta" ? "503664a377eb2e63a5f74b273299f622fca2bf95" : "47961a864810ae918b3d3bedb58fbdcdfaad6bdb", javaw.Hashes[0].HexDigest);
        Assert.Equal("bin/javaw.exe", result.Value.ExecutableRelativePath);
    }

    [Fact]
    public async Task GetPackageAsyncReturnsUnavailableForUnknownPlatform()
    {
        MojangJavaRuntimeCatalog catalog = new(new MojangJavaRuntimeCatalogOptions
        {
            ProductIndexUri = new Uri("https://example.test/all.json"),
            ProductIndexJson = Fixture("all.json"),
            PackageManifests = new Dictionary<string, string>(),
        });

        var result = await catalog.GetPackageAsync(
            "java-runtime-beta",
            JavaArchitecture.Arm64,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_RUNTIME_UNAVAILABLE", result.Problem?.Code);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "java-runtime", name));

    private static string BetaManifest() => """
        {
          "files": {
            "bin": { "type": "directory" },
            "conf": { "type": "directory" },
            "lib": { "type": "directory" },
            "bin/javaw.exe": {
              "type": "file",
              "executable": true,
              "downloads": {
                "raw": { "sha1": "503664a377eb2e63a5f74b273299f622fca2bf95", "size": 39424, "url": "https://piston-data.mojang.com/v1/objects/503664a377eb2e63a5f74b273299f622fca2bf95/javaw.exe" },
                "lzma": { "sha1": "005484d56aaceedc42ac0a0f6daa2fc8b68cd2e8", "size": 14452, "url": "https://piston-data.mojang.com/v1/objects/005484d56aaceedc42ac0a0f6daa2fc8b68cd2e8/javaw.exe" }
              }
            },
            "release": {
              "type": "file",
              "downloads": {
                "raw": { "sha1": "c1339b6ffb06c4e52ab1c9e8bce8cee4ca025f82", "size": 1092, "url": "https://piston-data.mojang.com/v1/objects/c1339b6ffb06c4e52ab1c9e8bce8cee4ca025f82/release" }
              }
            }
          }
        }
        """;

    private static string DeltaManifest() => BetaManifest()
        .Replace("503664a377eb2e63a5f74b273299f622fca2bf95", "47961a864810ae918b3d3bedb58fbdcdfaad6bdb", StringComparison.Ordinal)
        .Replace("005484d56aaceedc42ac0a0f6daa2fc8b68cd2e8", "047c086019a8e8c622752ebbddf8f5181ad53498", StringComparison.Ordinal)
        .Replace("c1339b6ffb06c4e52ab1c9e8bce8cee4ca025f82", "c677dfdb8e7ac26ad06c5d224d52c325f51ceb66", StringComparison.Ordinal)
        .Replace("1092", "1069", StringComparison.Ordinal);
}
