using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using Lacertae.Application.Downloads;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Operations;

namespace Lacertae.Infrastructure.Tests.Downloads;

internal static class DownloadTestSupport
{
    public static DownloadArtifact Artifact(
        byte[] content,
        string path = "artifact.bin",
        string? uri = null,
        ArtifactKind kind = ArtifactKind.ClientJar)
    {
        string digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return DownloadArtifact.Create(
            kind,
            new Uri(uri ?? "https://piston-data.mojang.com/v1/objects/fixture/" + path.Replace('/', '_')),
            path,
            content.Length,
            [new ArtifactHash("sha256", digest)]);
    }

    public static DownloadRequest Request(
        DownloadArtifact artifact,
        string stagingDirectory,
        DownloadSourcePreference? preference = null,
        bool temporaryFallbackApproved = false,
        string correlationId = "download-test") =>
        new(
            artifact,
            stagingDirectory,
            preference ?? DownloadSourcePreference.Automatic,
            temporaryFallbackApproved,
            correlationId);

    public static HttpResponseMessage Ok(
        byte[] content,
        string? etag = null,
        string? lastModified = null)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        if (lastModified is not null)
        {
            response.Content.Headers.LastModified = DateTimeOffset.Parse(lastModified, CultureInfo.InvariantCulture);
        }

        return response;
    }

    public static HttpContent Chunked(
        byte[] content,
        int chunkSize,
        TimeSpan? delay = null) =>
        new ChunkedContent(content, chunkSize, delay ?? TimeSpan.Zero);

    public static HttpResponseMessage Partial(
        byte[] content,
        int start,
        int totalLength,
        string etag)
    {
        HttpResponseMessage response = new(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(content),
        };
        response.Headers.ETag = new EntityTagHeaderValue(etag);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, totalLength - 1, totalLength);
        return response;
    }

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found) =>
        new(status)
        {
            Headers = { Location = new Uri(location) },
        };

    public static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    internal sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "lacertae-download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    internal sealed class ScriptedHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private int requestNumber;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            int number = Interlocked.Increment(ref requestNumber);
            return responder(request, number, cancellationToken);
        }
    }

    internal sealed class GuardedContent(byte[] content) : HttpContent
    {
        public bool SerializeWasCalled { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new InvalidOperationException("The downloader must not buffer response content."));

        protected override bool TryComputeLength(out long length)
        {
            length = content.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(new TrackingReadStream(content, () => SerializeWasCalled = true));
        }
    }

    private sealed class TrackingReadStream(byte[] content, Action markRead) : MemoryStream(content, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            markRead();
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            markRead();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ChunkedContent(byte[] content, int chunkSize, TimeSpan delay) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new InvalidOperationException("The downloader must stream response content."));

        protected override bool TryComputeLength(out long length)
        {
            length = content.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ChunkedReadStream(content, chunkSize, delay));
    }

    private sealed class ChunkedReadStream(byte[] content, int chunkSize, TimeSpan delay)
        : MemoryStream(content, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, chunkSize));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.ReadAsync(buffer.AsMemory(offset, Math.Min(count, chunkSize)), cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
        }
    }
}
