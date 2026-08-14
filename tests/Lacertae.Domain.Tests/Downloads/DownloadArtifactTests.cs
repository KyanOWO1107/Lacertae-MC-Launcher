using Lacertae.Domain.Downloads;

namespace Lacertae.Domain.Tests.Downloads;

public sealed class DownloadArtifactTests
{
    private static readonly Uri OfficialUri = new("https://resources.download.minecraft.net/artifact");

    [Fact]
    public void ArtifactRejectsRelativeOrNonHttpsOfficialUri()
    {
        Assert.Throws<ArgumentException>(() => DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            new Uri("artifact", UriKind.Relative),
            "client.jar",
            1,
            [Sha256()]));

        Assert.Throws<ArgumentException>(() => DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            new Uri("http://example.test/artifact"),
            "client.jar",
            1,
            [Sha256()]));
    }

    [Theory]
    [InlineData("/client.jar")]
    [InlineData("..\\client.jar")]
    [InlineData("libraries/../client.jar")]
    [InlineData("libraries//client.jar")]
    public void ArtifactRejectsRootedOrParentTraversingRelativePath(string path)
    {
        Assert.Throws<ArgumentException>(() => DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            OfficialUri,
            path,
            1,
            [Sha256()]));
    }

    [Fact]
    public void ArtifactRequiresPositiveSizeAndASupportedHash()
    {
        Assert.Throws<ArgumentException>(() => DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            OfficialUri,
            "client.jar",
            0,
            [Sha256()]));

        Assert.Throws<ArgumentException>(() => DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            OfficialUri,
            "client.jar",
            1,
            [new ArtifactHash("md5", new string('a', 32))]));

        Assert.Throws<ArgumentException>(() => DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            OfficialUri,
            "client.jar",
            1,
            [new ArtifactHash("sha256", "not-a-digest")]));
    }

    [Fact]
    public void ArtifactIdIsStableForKindPathSizeAndHash()
    {
        DownloadArtifact first = DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            OfficialUri,
            @"libraries\example\client.jar",
            12,
            [new ArtifactHash("SHA256", new string('B', 64)), new ArtifactHash("SHA1", new string('A', 40))]);
        DownloadArtifact second = DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            new Uri("https://mirror.example.test/client.jar"),
            "libraries/example/client.jar",
            12,
            [new ArtifactHash("sha1", new string('a', 40)), new ArtifactHash("sha256", new string('b', 64))]);

        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.Equal("libraries/example/client.jar", first.RelativeDestinationPath);
        Assert.Equal(["sha1", "sha256"], first.Hashes.Select(static hash => hash.NormalizedAlgorithm));
    }

    private static ArtifactHash Sha256() => new("sha256", new string('a', 64));
}
