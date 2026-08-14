using System.Net;
using Lacertae.Application.Downloads;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Downloads;

namespace Lacertae.Infrastructure.Tests.Downloads;

public sealed class DownloadSourceContractTests
{
    [Fact]
    public void OfficialSourceReturnsTheArtifactUriUnchanged()
    {
        DownloadArtifact artifact = DownloadTestSupport.Artifact(
            [1, 2, 3],
            uri: "https://piston-data.mojang.com/v1/objects/abc/client.jar");
        OfficialDownloadSource source = new();

        Result<DownloadCandidate> result = source.Map(artifact, "corr-official");

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(artifact.OfficialUri, result.Value.Uri);
        Assert.True(result.Value.IsOfficial);
        Assert.True(result.Value.SupportsRanges);
    }

    [Theory]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json")]
    [InlineData("https://piston-data.mojang.com/v1/objects/abc/client.jar", "https://bmclapi2.bangbang93.com/v1/objects/abc/client.jar")]
    [InlineData("https://resources.download.minecraft.net/ab/0123456789012345678901234567890123456789", "https://bmclapi2.bangbang93.com/assets/ab/0123456789012345678901234567890123456789")]
    [InlineData("https://libraries.minecraft.net/com/mojang/authlib/6.0.58/authlib-6.0.58.jar", "https://bmclapi2.bangbang93.com/maven/com/mojang/authlib/6.0.58/authlib-6.0.58.jar")]
    public void BmclApiSourceMapsDocumentedOfficialPaths(string officialUri, string expectedUri)
    {
        DownloadArtifact artifact = DownloadTestSupport.Artifact([1], uri: officialUri);
        BmclApiDownloadSource source = new();

        Result<DownloadCandidate> result = source.Map(artifact, "corr-bmcl");

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(expectedUri, result.Value.Uri.AbsoluteUri);
        Assert.False(result.Value.IsOfficial);
        Assert.True(result.Value.SupportsRanges);
    }

    [Theory]
    [InlineData("http://piston-data.mojang.com/v1/objects/abc/client.jar")]
    [InlineData("https://user@piston-data.mojang.com/v1/objects/abc/client.jar")]
    [InlineData("https://piston-data.mojang.com:8443/v1/objects/abc/client.jar")]
    [InlineData("https://piston-data.mojang.com/v1/objects/abc/client.jar?token=secret")]
    [InlineData("https://piston-data.mojang.com/v1/objects/abc/client.jar#fragment")]
    [InlineData("https://example.test/v1/objects/abc/client.jar")]
    public void BmclApiSourceRejectsUntrustedOrUnmappedUris(string uri)
    {
        DownloadArtifact artifact = uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? new(
                "fixture",
                ArtifactKind.ClientJar,
                new Uri(uri),
                "artifact.bin",
                1,
                [new ArtifactHash("sha256", new string('a', 64))])
            : DownloadTestSupport.Artifact([1], uri: uri);
        BmclApiDownloadSource source = new();

        Assert.False(source.CanMap(artifact));
        Result<DownloadCandidate> result = source.Map(artifact, "corr-reject");

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_SOURCE_UNAVAILABLE", result.Problem?.Code);
    }
}
