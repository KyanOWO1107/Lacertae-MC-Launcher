using Lacertae.Infrastructure.Install.Mojang;

namespace Lacertae.Infrastructure.Tests.Install.Mojang;

public sealed class MojangVanillaVersionCatalogTests
{
    [Fact]
    public async Task ListAsyncReadsOnlyValidatedOfficialManifestEntries()
    {
        const string manifest = """
            {
              "latest": { "release": "1.21.1", "snapshot": "24w01a" },
              "versions": [
                {
                  "id": "1.21.1",
                  "type": "release",
                  "url": "https://piston-meta.mojang.com/v1/packages/release.json",
                  "time": "2024-09-19T00:00:00Z",
                  "releaseTime": "2024-09-19T00:00:00Z",
                  "sha1": "0123456789abcdef0123456789abcdef01234567"
                },
                {
                  "id": "24w01a",
                  "type": "snapshot",
                  "url": "https://piston-meta.mojang.com/v1/packages/snapshot.json",
                  "time": "2024-01-04T00:00:00Z",
                  "releaseTime": "2024-01-04T00:00:00Z",
                  "sha1": "abcdef0123456789abcdef0123456789abcdef01"
                }
              ]
            }
            """;

        MojangVanillaMetadataSource source = new(new MojangVanillaMetadataSourceOptions
        {
            VersionManifestJson = manifest,
        });

        var result = await source.ListAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(["1.21.1", "24w01a"], result.Value.Select(static version => version.Id));
        Assert.Equal(["release", "snapshot"], result.Value.Select(static version => version.Type));
        Assert.All(result.Value, version => Assert.Equal("piston-meta.mojang.com", version.MetadataUri.Host));
    }

    [Fact]
    public async Task ListAsyncAcceptsOfficialVersionIdsContainingSpaces()
    {
        const string manifest = """
            {
              "versions": [
                {
                  "id": "1.14.2 Pre-Release 4",
                  "type": "snapshot",
                  "url": "https://piston-meta.mojang.com/v1/packages/release.json",
                  "releaseTime": "2019-05-27T00:21:11-07:00",
                  "sha1": "0123456789abcdef0123456789abcdef01234567"
                }
              ]
            }
            """;

        MojangVanillaMetadataSource source = new(new MojangVanillaMetadataSourceOptions
        {
            VersionManifestJson = manifest,
        });

        var result = await source.ListAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("1.14.2 Pre-Release 4", Assert.Single(result.Value).Id);
    }

    [Fact]
    public async Task ListAsyncRejectsUntrustedManifestUriAndMalformedHash()
    {
        const string manifest = """
            {
              "versions": [
                {
                  "id": "1.21.1",
                  "type": "release",
                  "url": "https://example.test/version.json",
                  "time": "2024-09-19T00:00:00Z",
                  "releaseTime": "2024-09-19T00:00:00Z",
                  "sha1": "not-a-sha1"
                }
              ]
            }
            """;

        MojangVanillaMetadataSource source = new(new MojangVanillaMetadataSourceOptions
        {
            VersionManifestJson = manifest,
        });

        var result = await source.ListAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("VERSION_METADATA_INVALID", result.Problem?.Code);
    }
}
