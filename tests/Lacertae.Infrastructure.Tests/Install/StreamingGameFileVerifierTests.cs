using System.Security.Cryptography;
using Lacertae.Domain.Downloads;
using Lacertae.Infrastructure.Install;

namespace Lacertae.Infrastructure.Tests.Install;

public sealed class StreamingGameFileVerifierTests
{
    [Fact]
    public async Task VerifyAsyncAcceptsMatchingSizeAndHash()
    {
        using TestRoot root = new();
        byte[] content = [1, 2, 3, 4];
        DownloadArtifact artifact = Artifact(content);
        string path = root.Write("file.bin", content);

        var result = await new StreamingGameFileVerifier().VerifyAsync(
            artifact,
            path,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task VerifyAsyncReturnsFalseForMissingSizeOrHashMismatch()
    {
        using TestRoot root = new();
        byte[] content = [1, 2, 3, 4];
        DownloadArtifact artifact = Artifact(content);
        string path = root.Write("file.bin", [1, 2, 3]);

        var wrongSize = await new StreamingGameFileVerifier().VerifyAsync(artifact, path, TestContext.Current.CancellationToken);
        Assert.True(wrongSize.IsSuccess);
        Assert.False(wrongSize.Value);

        await File.WriteAllBytesAsync(path, [9, 9, 9, 9], TestContext.Current.CancellationToken);
        var wrongHash = await new StreamingGameFileVerifier().VerifyAsync(artifact, path, TestContext.Current.CancellationToken);
        Assert.True(wrongHash.IsSuccess);
        Assert.False(wrongHash.Value);
    }

    private static DownloadArtifact Artifact(byte[] content) =>
        DownloadArtifact.Create(
            ArtifactKind.ClientJar,
            new Uri("https://official.example.test/file.bin"),
            "file.bin",
            content.Length,
            [new ArtifactHash("sha256", Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant())]);

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lacertae-verifier-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, byte[] content)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
