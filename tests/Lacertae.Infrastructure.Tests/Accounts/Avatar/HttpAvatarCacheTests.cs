using System.Net;
using System.Net.Http.Headers;
using Lacertae.Infrastructure.Accounts.Avatar;

namespace Lacertae.Infrastructure.Tests.Accounts.Avatar;

public sealed class HttpAvatarCacheTests
{
    [Fact]
    public async Task CachesTrustedPngByLowercaseSha256Key()
    {
        using TestDirectory directory = new();
        byte[] png = PngFixtureBuilder.Create(64, 64);
        using HttpClient client = CreateClient(png, "image/png");
        using HttpAvatarCache cache = new(directory.Path, client, new FixedTimeProvider());

        var result = await cache.RefreshAsync(
            new Uri("https://textures.minecraft.net/texture/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.False(result.Value.UsesPlaceholder);
        Assert.Matches("^[a-f0-9]{64}$", result.Value.CacheKey!);
        Assert.True(File.Exists(Path.Combine(directory.Path, "cache", "avatars", result.Value.CacheKey + ".png")));
        Assert.Equal(
            Path.Combine(directory.Path, "cache", "avatars", result.Value.CacheKey + ".png"),
            cache.ResolvePath(result.Value.CacheKey));
    }

    [Theory]
    [InlineData("http://textures.minecraft.net/texture/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://textures.minecraft.net/texture/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("https://textures.minecraft.net/texture/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://example.invalid/texture/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task UntrustedSkinUriReturnsPlaceholderWithoutRequest(string uri)
    {
        using TestDirectory directory = new();
        RecordingHandler handler = new(_ => throw new InvalidOperationException("request must not be sent"));
        using HttpClient client = new(handler);
        using HttpAvatarCache cache = new(directory.Path, client, new FixedTimeProvider());

        var result = await cache.RefreshAsync(new Uri(uri), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.UsesPlaceholder);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RejectsRedirectResponses()
    {
        using TestDirectory directory = new();
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://example.invalid/redirected") },
        });
        using HttpClient client = new(handler);
        using HttpAvatarCache cache = new(directory.Path, client, new FixedTimeProvider());

        var result = await cache.RefreshAsync(TrustedUri(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.UsesPlaceholder);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task StopsOversizedResponseBeforeCaching()
    {
        using TestDirectory directory = new();
        byte[] oversized = new byte[(1 * 1024 * 1024) + 1];
        using HttpClient client = CreateClient(oversized, "image/png");
        using HttpAvatarCache cache = new(directory.Path, client, new FixedTimeProvider());

        var result = await cache.RefreshAsync(TrustedUri(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.UsesPlaceholder);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "cache", "avatars")) &&
            Directory.EnumerateFiles(Path.Combine(directory.Path, "cache", "avatars"), "*.png", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task InvalidPngAndContentTypeReturnPlaceholder()
    {
        using TestDirectory directory = new();
        using HttpClient client = CreateClient("not-an-image"u8.ToArray(), "text/plain");
        using HttpAvatarCache cache = new(directory.Path, client, new FixedTimeProvider());

        var result = await cache.RefreshAsync(TrustedUri(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.UsesPlaceholder);
    }

    private static Uri TrustedUri() => new(
        "https://textures.minecraft.net/texture/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    private static HttpClient CreateClient(byte[] content, string mediaType) =>
        new(new RecordingHandler(_ =>
        {
            ByteArrayContent body = new(content);
            body.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
        }));

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Value = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Value;
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = global::System.IO.Path.Combine(
                global::System.IO.Path.GetTempPath(),
                "lacertae-avatar-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
