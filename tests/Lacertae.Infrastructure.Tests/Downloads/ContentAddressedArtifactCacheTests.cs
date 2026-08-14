using Lacertae.Application.Downloads;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Downloads;

namespace Lacertae.Infrastructure.Tests.Downloads;

public sealed class ContentAddressedArtifactCacheTests
{
    [Fact]
    public async Task PutAndGetVerifiesTrustedContentBeforeReuse()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] content = [1, 2, 3, 4, 5];
        DownloadArtifact artifact = DownloadTestSupport.Artifact(content);
        string source = Path.Combine(root.Path, "source.bin");
        await File.WriteAllBytesAsync(source, content, TestContext.Current.CancellationToken);
        ContentAddressedArtifactCache cache = new(Path.Combine(root.Path, "cache"));

        Result<Unit> put = await cache.PutAsync(artifact, source, TestContext.Current.CancellationToken);
        Result<string?> hit = await cache.GetAsync(artifact, TestContext.Current.CancellationToken);

        Assert.True(put.IsSuccess, put.Problem?.Code);
        Assert.True(hit.IsSuccess, hit.Problem?.Code);
        Assert.NotNull(hit.Value);
        Assert.NotEqual(source, hit.Value);
        Assert.Equal(content, await File.ReadAllBytesAsync(hit.Value!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetDoesNotReuseTamperedCachedContent()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] content = [6, 7, 8];
        DownloadArtifact artifact = DownloadTestSupport.Artifact(content);
        string source = Path.Combine(root.Path, "source.bin");
        await File.WriteAllBytesAsync(source, content, TestContext.Current.CancellationToken);
        ContentAddressedArtifactCache cache = new(Path.Combine(root.Path, "cache"));
        Assert.True((await cache.PutAsync(artifact, source, TestContext.Current.CancellationToken)).IsSuccess);
        string cachePath = (await cache.GetAsync(artifact, TestContext.Current.CancellationToken)).Value!;
        await File.WriteAllBytesAsync(cachePath, [9, 9, 9], TestContext.Current.CancellationToken);

        Result<string?> result = await cache.GetAsync(artifact, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Null(result.Value);
    }
}
