using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lacertae.Application.Downloads;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Downloads;

namespace Lacertae.Infrastructure.Tests.Downloads;

public sealed class HttpArtifactDownloaderTests
{
    [Fact]
    public async Task DownloadStreamsResponseToVerifiedFinalFile()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] content = Enumerable.Range(0, 64 * 1024).Select(static value => (byte)(value % 251)).ToArray();
        DownloadArtifact artifact = DownloadTestSupport.Artifact(content);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new DownloadTestSupport.GuardedContent(content),
            };
            return Task.FromResult(response);
        });
        HttpArtifactDownloader downloader = CreateDownloader(handler);

        Result<DownloadReceipt> result = await downloader.DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(content, await File.ReadAllBytesAsync(result.Value.VerifiedFilePath, TestContext.Current.CancellationToken));
        Assert.Equal(artifact.ExpectedSize, result.Value.BytesTransferred);
        Assert.False(result.Value.WasResumed);
        Assert.False(File.Exists(result.Value.VerifiedFilePath + ".part"));
    }

    [Fact]
    public async Task DownloadResumesOnlyWithMatchingStrongEtagAnd206Range()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] content = [1, 2, 3, 4, 5, 6];
        byte[] prefix = content[..2];
        byte[] suffix = content[2..];
        DownloadArtifact artifact = DownloadTestSupport.Artifact(content);
        string finalPath = Path.Combine(root.Path, artifact.RelativeDestinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await File.WriteAllBytesAsync(finalPath + ".part", prefix, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(finalPath + ".part.meta.json", JsonSerializer.Serialize(new
        {
            sourceId = "official",
            etag = "\"strong\"",
            lastModified = (string?)null,
            expectedSize = content.Length,
            bytesPresent = prefix.Length,
        }), TestContext.Current.CancellationToken);
        DownloadTestSupport.ScriptedHandler handler = new((request, _, _) =>
        {
            Assert.Equal("bytes=2-", request.Headers.Range?.ToString());
            Assert.Equal("\"strong\"", request.Headers.IfRange?.EntityTag?.Tag);
            return Task.FromResult(DownloadTestSupport.Partial(suffix, prefix.Length, content.Length, "\"strong\""));
        });
        HttpArtifactDownloader downloader = CreateDownloader(handler);

        Result<DownloadReceipt> result = await downloader.DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.True(result.Value.WasResumed);
        Assert.Equal(content, await File.ReadAllBytesAsync(result.Value.VerifiedFilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadRestartsWhenResumeValidatorChangesOrServerReturns200()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] content = [9, 8, 7, 6];
        DownloadArtifact artifact = DownloadTestSupport.Artifact(content);
        string finalPath = Path.Combine(root.Path, artifact.RelativeDestinationPath);
        await File.WriteAllBytesAsync(finalPath + ".part", [1, 2], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(finalPath + ".part.meta.json", JsonSerializer.Serialize(new
        {
            sourceId = "official",
            etag = "\"old\"",
            expectedSize = content.Length,
            bytesPresent = 2,
        }), TestContext.Current.CancellationToken);
        DownloadTestSupport.ScriptedHandler handler = new((request, number, _) =>
        {
            if (number == 1)
            {
                Assert.NotNull(request.Headers.Range);
            }
            else
            {
                Assert.Null(request.Headers.Range);
            }

            return Task.FromResult(DownloadTestSupport.Ok(content, "\"new\""));
        });

        Result<DownloadReceipt> result = await CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.False(result.Value.WasResumed);
        Assert.Equal(content, await File.ReadAllBytesAsync(result.Value.VerifiedFilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadRejectsWrongByteCountAndQuarantinesEvidence()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] expected = [1, 2, 3];
        DownloadArtifact artifact = DownloadTestSupport.Artifact(expected);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
            Task.FromResult(DownloadTestSupport.Ok([1, 2, 4])));

        Result<DownloadReceipt> result = await CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_HASH_MISMATCH", result.Problem?.Code);
        Assert.False(File.Exists(Path.Combine(root.Path, artifact.RelativeDestinationPath)));
        string[] evidence = Directory.GetFiles(root.Path, "*.bad", SearchOption.AllDirectories);
        Assert.NotEmpty(evidence);
        string metadata = File.ReadAllText(evidence[0] + ".json");
        Assert.Contains("official", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(artifact.OfficialUri.AbsoluteUri, metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadRejectsTooManyBytesAndQuarantinesEvidence()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        DownloadArtifact artifact = DownloadTestSupport.Artifact([1, 2, 3]);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
            Task.FromResult(DownloadTestSupport.Ok([1, 2, 3, 4])));

        Result<DownloadReceipt> result = await CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_HASH_MISMATCH", result.Problem?.Code);
        string evidence = Directory.GetFiles(Path.Combine(root.Path, ".quarantine"), "*.bad").Single();
        Assert.Empty(await File.ReadAllBytesAsync(evidence, TestContext.Current.CancellationToken));
        Assert.Contains("too-many-bytes", File.ReadAllText(evidence + ".json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadRejectsTooFewBytesAndQuarantinesEvidence()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] expected = [1, 2, 3];
        DownloadArtifact artifact = DownloadTestSupport.Artifact(expected);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
            Task.FromResult(DownloadTestSupport.Ok([1, 2])));

        Result<DownloadReceipt> result = await CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_HASH_MISMATCH", result.Problem?.Code);
        string evidence = Directory.GetFiles(Path.Combine(root.Path, ".quarantine"), "*.bad").Single();
        Assert.Equal([1, 2], await File.ReadAllBytesAsync(evidence, TestContext.Current.CancellationToken));
        Assert.Contains("too-few-bytes", File.ReadAllText(evidence + ".json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadFollowsAtMostThreeHttpsRedirectsAndRejectsUnsafeRedirects()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] content = [3, 1, 4];
        DownloadArtifact artifact = DownloadTestSupport.Artifact(content);
        DownloadTestSupport.ScriptedHandler handler = new((request, number, _) => number switch
        {
            1 => Task.FromResult(DownloadTestSupport.Redirect("https://piston-data.mojang.com/one")),
            2 => Task.FromResult(DownloadTestSupport.Redirect("https://piston-data.mojang.com/two")),
            3 => Task.FromResult(DownloadTestSupport.Redirect("https://piston-data.mojang.com/three")),
            _ => Task.FromResult(DownloadTestSupport.Ok(content)),
        });

        Result<DownloadReceipt> result = await CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task DownloadRejectsCrossHostHttpsRedirect()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        DownloadArtifact artifact = DownloadTestSupport.Artifact([3, 1, 4]);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
            Task.FromResult(DownloadTestSupport.Redirect("https://cdn.example.test/object")));

        Result<DownloadReceipt> result = await CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_REDIRECT_REJECTED", result.Problem?.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DownloadRejectsHttpRedirect()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        DownloadArtifact artifact = DownloadTestSupport.Artifact([3, 1, 4]);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
            Task.FromResult(DownloadTestSupport.Redirect("http://cdn.example.test/object")));

        Result<DownloadReceipt> result = await CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_REDIRECT_REJECTED", result.Problem?.Code);
    }

    [Fact]
    public async Task DownloadRejectsRedirectWithUserInfo()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        DownloadArtifact artifact = DownloadTestSupport.Artifact([3, 1, 4]);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
            Task.FromResult(DownloadTestSupport.Redirect("https://user:secret@cdn.example.test/object")));

        Result<DownloadReceipt> result = await CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_REDIRECT_REJECTED", result.Problem?.Code);
    }

    [Fact]
    public async Task DownloadRejectsRedirectLoop()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        DownloadArtifact artifact = DownloadTestSupport.Artifact([3, 1, 4]);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
            Task.FromResult(DownloadTestSupport.Redirect("https://piston-data.mojang.com/v1/objects/fixture")));

        Result<DownloadReceipt> result = await CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_REDIRECT_REJECTED", result.Problem?.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DownloadRejectsRedirectWithoutLocation()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        DownloadArtifact artifact = DownloadTestSupport.Artifact([3, 1, 4]);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)));

        Result<DownloadReceipt> result = await CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_REDIRECT_REJECTED", result.Problem?.Code);
    }

    [Fact]
    public async Task AutomaticSourceRetriesTransientErrorsThenFallsBackWithoutRacing()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] content = Enumerable.Repeat((byte)5, 64 * 1024).ToArray();
        DownloadArtifact artifact = DownloadTestSupport.Artifact(content);
        DownloadSourceId officialId = new("official");
        DownloadSourceId mirrorId = new("bmclapi");
        DownloadSourceSelector selector = new([
            new FixedSource(officialId, true, "https://piston-data.mojang.com/official"),
            new FixedSource(mirrorId, false, "https://bmclapi2.bangbang93.com/mirror"),
        ]);
        DownloadTestSupport.ScriptedHandler handler = new((request, _, _) =>
            request.RequestUri!.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : Task.FromResult(DownloadTestSupport.Ok(content)));
        HttpArtifactDownloader downloader = CreateDownloader(handler, selector);

        Result<DownloadReceipt> result = await downloader.DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path, temporaryFallbackApproved: true),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(mirrorId, result.Value.SourceId);
        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests.Take(3), request => Assert.Equal("piston-data.mojang.com", request.RequestUri!.Host));
        Assert.Equal("bmclapi2.bangbang93.com", handler.Requests[3].RequestUri!.Host);
    }

    [Fact]
    public async Task PinnedSourceRequiresConsentInsteadOfFallingBack()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] content = [2, 7, 1];
        DownloadArtifact artifact = DownloadTestSupport.Artifact(content);
        DownloadSourceId mirrorId = new("bmclapi");
        DownloadSourceId officialId = new("official");
        DownloadSourceSelector selector = new([
            new FixedSource(mirrorId, false, "https://bmclapi2.bangbang93.com/mirror"),
            new FixedSource(officialId, true, "https://piston-data.mojang.com/official"),
        ]);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout)));

        Result<DownloadReceipt> result = await CreateDownloader(handler, selector).DownloadAsync(
            DownloadTestSupport.Request(
                artifact,
                root.Path,
                DownloadSourcePreference.Pinned(mirrorId),
                temporaryFallbackApproved: false),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DOWNLOAD_FALLBACK_CONSENT_REQUIRED", result.Problem?.Code);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task AutomaticSourceFallsBackAfterMeasuredLowSpeedWindow()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] content = Enumerable.Repeat((byte)5, 64 * 1024).ToArray();
        DownloadArtifact artifact = DownloadTestSupport.Artifact(content);
        DownloadSourceId officialId = new("official");
        DownloadSourceId mirrorId = new("bmclapi");
        DownloadSourceSelector selector = new([
            new FixedSource(officialId, true, "https://piston-data.mojang.com/slow"),
            new FixedSource(mirrorId, false, "https://bmclapi2.bangbang93.com/fast"),
        ]);
        DownloadTestSupport.ScriptedHandler handler = new((request, _, _) =>
        {
            if (request.RequestUri!.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                HttpResponseMessage response = new(HttpStatusCode.OK)
                {
                    Content = DownloadTestSupport.Chunked(content, 1, TimeSpan.FromMilliseconds(20)),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"slow\"");
                return Task.FromResult(response);
            }

            return Task.FromResult(DownloadTestSupport.Ok(content));
        });
        DownloadAttemptPolicy policy = new()
        {
            MaximumTransientRetries = 0,
            FirstRetryDelay = TimeSpan.Zero,
            SecondRetryDelay = TimeSpan.Zero,
            RetryAfterMaximum = TimeSpan.Zero,
            LowSpeedGraceBytes = 0,
            LowSpeedWindow = TimeSpan.FromMilliseconds(1),
            LowSpeedThresholdBytesPerSecond = 100_000,
        };

        Result<DownloadReceipt> result = await CreateDownloader(handler, selector, policy).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path, temporaryFallbackApproved: true),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(mirrorId, result.Value.SourceId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CancellationKeepsResumablePartAndRemovesUnvalidatedFinalFile()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        byte[] content = [9, 8, 7, 6];
        DownloadArtifact artifact = DownloadTestSupport.Artifact(content);
        string finalPath = Path.Combine(root.Path, artifact.RelativeDestinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await File.WriteAllBytesAsync(finalPath, [0, 0, 0, 0], TestContext.Current.CancellationToken);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = DownloadTestSupport.Chunked(content, 1),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"resume\"");
            return Task.FromResult(response);
        });
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateDownloader(handler).DownloadAsync(
            DownloadTestSupport.Request(artifact, root.Path),
            new CancellingProgress(cancellation),
            cancellation.Token));

        Assert.False(File.Exists(finalPath));
        Assert.True(File.Exists(finalPath + ".part"));
        Assert.True(File.Exists(finalPath + ".part.meta.json"));
        Assert.Single(Directory.GetFiles(Path.Combine(root.Path, ".quarantine"), "*.bad"));
    }

    [Fact]
    public async Task DownloaderNeverSendsCookieAuthorizationOrUserPathHeaders()
    {
        using DownloadTestSupport.TemporaryDirectory root = new();
        DownloadArtifact artifact = DownloadTestSupport.Artifact([1, 2, 3]);
        DownloadTestSupport.ScriptedHandler handler = new((_, _, _) =>
            Task.FromResult(DownloadTestSupport.Ok([1, 2, 3])));
        using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "session=secret");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Path", "C:\\Users\\Player");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Path", "C:\\Users\\Player\\.minecraft");

        Result<DownloadReceipt> result = await new HttpArtifactDownloader(
            client,
            new DownloadSourceSelector([new FixedSource(
                new DownloadSourceId("official"),
                true,
                "https://piston-data.mojang.com/v1/objects/fixture")]),
            attemptPolicy: new DownloadAttemptPolicy
            {
                FirstRetryDelay = TimeSpan.Zero,
                SecondRetryDelay = TimeSpan.Zero,
                RetryAfterMaximum = TimeSpan.Zero,
            }).DownloadAsync(
                DownloadTestSupport.Request(artifact, root.Path),
                new Progress<OperationProgress>(),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("X-User-Path"));
        Assert.False(request.Headers.Contains("X-Path"));
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
        {
            if (value.CompletedBytes > 0)
            {
                cancellation.Cancel();
            }
        }
    }

    private static HttpArtifactDownloader CreateDownloader(
        HttpMessageHandler handler,
        DownloadSourceSelector? selector = null,
        DownloadAttemptPolicy? policy = null) =>
        new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            selector ?? new DownloadSourceSelector([new FixedSource(
                new DownloadSourceId("official"),
                true,
                "https://piston-data.mojang.com/v1/objects/fixture")]),
            attemptPolicy: policy ?? new DownloadAttemptPolicy
            {
                FirstRetryDelay = TimeSpan.Zero,
                SecondRetryDelay = TimeSpan.Zero,
                RetryAfterMaximum = TimeSpan.Zero,
            });

    private sealed class FixedSource(DownloadSourceId id, bool official, string mappedUri) : IDownloadSource
    {
        public DownloadSourceId Id { get; } = id;
        public bool IsOfficial { get; } = official;

        public bool CanMap(DownloadArtifact artifact) => true;

        public Result<DownloadCandidate> Map(DownloadArtifact artifact, string correlationId) =>
            Result<DownloadCandidate>.Success(new DownloadCandidate(Id, new Uri(mappedUri), IsOfficial, true));
    }
}
