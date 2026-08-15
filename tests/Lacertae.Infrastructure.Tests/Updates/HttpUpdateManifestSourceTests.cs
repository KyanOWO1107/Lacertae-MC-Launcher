using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Lacertae.Domain.Updates;
using Lacertae.Infrastructure.Updates;

namespace Lacertae.Infrastructure.Tests.Updates;

public sealed class HttpUpdateManifestSourceTests
{
    [Fact]
    public async Task FetchAsyncParsesStrictManifestAndDetachedSignature()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "keyId": "test-key",
              "channel": "stable",
              "version": "1.2.0",
              "publishedUtc": "2026-08-14T12:00:00Z",
              "minimumLauncherVersion": "1.0.0",
              "releaseNotes": { "en-US": "Notes", "zh-CN": "说明" },
              "releaseNotesUrl": "https://updates.example.test/releases/1.2.0",
              "package": {
                "runtime": "win-x64",
                "url": "https://updates.example.test/packages/1.2.0.zip",
                "size": 128,
                "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "fileManifestSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
              }
            }
            """;
        HttpClient client = Client(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["https://updates.example.test/manifest.json"] = Encoding.UTF8.GetBytes(json),
            ["https://updates.example.test/manifest.sig"] = [1, 2, 3],
        });
        HttpUpdateManifestSource source = new(
            new HttpUpdateManifestSourceOptions(
                new Uri("https://updates.example.test/manifest.json"),
                new Uri("https://updates.example.test/manifest.sig")),
            client);

        var result = await source.FetchAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("1.2.0", result.Value.Manifest.Version);
        Assert.Equal([1, 2, 3], result.Value.Signature);
        Assert.Equal(Encoding.UTF8.GetBytes(json), result.Value.SourceBytes);
    }

    [Fact]
    public async Task FetchAsyncRejectsUnknownFieldsAndOversizedResponses()
    {
        const string json = "{\"schemaVersion\":1,\"unknown\":true}";
        HttpClient client = Client(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["https://updates.example.test/manifest.json"] = Encoding.UTF8.GetBytes(json),
            ["https://updates.example.test/manifest.sig"] = [1],
        });
        HttpUpdateManifestSource source = new(
            new HttpUpdateManifestSourceOptions(
                new Uri("https://updates.example.test/manifest.json"),
                new Uri("https://updates.example.test/manifest.sig"),
                MaximumManifestBytes: 32),
            client);

        var result = await source.FetchAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("UPDATE_MANIFEST_INVALID", result.Problem?.Code);
    }

    [Fact]
    public async Task FetchAsyncAcceptsExplicitTestChannel()
    {
        const string json = "{\"schemaVersion\":1,\"keyId\":\"test-key\",\"channel\":\"test\",\"version\":\"1.2.0\",\"publishedUtc\":\"2026-08-14T12:00:00Z\",\"minimumLauncherVersion\":\"1.0.0\",\"releaseNotes\":{\"en-US\":\"Notes\"},\"releaseNotesUrl\":\"https://updates.example.test/releases/1.2.0\",\"package\":{\"runtime\":\"win-x64\",\"url\":\"https://updates.example.test/packages/1.2.0.zip\",\"size\":128,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"fileManifestSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}}";
        HttpClient client = Client(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["https://updates.example.test/manifest.json"] = Encoding.UTF8.GetBytes(json),
            ["https://updates.example.test/manifest.sig"] = [1],
        });
        HttpUpdateManifestSource source = new(
            new HttpUpdateManifestSourceOptions(
                new Uri("https://updates.example.test/manifest.json"),
                new Uri("https://updates.example.test/manifest.sig")),
            client);

        var result = await source.FetchAsync(UpdateChannel.Test, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(UpdateChannel.Test, result.Value.Manifest.Channel);
    }

    private static HttpClient Client(IReadOnlyDictionary<string, byte[]> responses) => new(
        new StubHandler(responses));

    private sealed class StubHandler(IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!responses.TryGetValue(request.RequestUri!.AbsoluteUri, out byte[]? bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }
}
