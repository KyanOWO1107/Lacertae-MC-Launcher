using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lacertae.Application.Downloads;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Downloads;

public sealed class HttpArtifactDownloader : IArtifactDownloader
{
    private const int BufferSize = 128 * 1024;
    private static readonly JsonSerializerOptions PartMetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly HttpClient httpClient;
    private readonly DownloadSourceSelector sourceSelector;
    private readonly IArtifactCache? cache;
    private readonly DownloadAttemptPolicy attemptPolicy;
    private readonly Action<DownloadAttemptEvent>? audit;

    public HttpArtifactDownloader(
        HttpClient httpClient,
        DownloadSourceSelector sourceSelector,
        IArtifactCache? cache = null,
        DownloadAttemptPolicy? attemptPolicy = null,
        Action<DownloadAttemptEvent>? audit = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.sourceSelector = sourceSelector ?? throw new ArgumentNullException(nameof(sourceSelector));
        this.cache = cache;
        this.attemptPolicy = attemptPolicy ?? new DownloadAttemptPolicy();
        this.audit = audit;
        ValidatePolicy(this.attemptPolicy);

        // A downloader never carries caller credentials or filesystem context to a source.
        this.httpClient.DefaultRequestHeaders.Remove("Authorization");
        this.httpClient.DefaultRequestHeaders.Remove("Cookie");
        this.httpClient.DefaultRequestHeaders.Remove("X-User-Path");
        this.httpClient.DefaultRequestHeaders.Remove("X-Path");
    }

    public HttpArtifactDownloader(
        DownloadSourceSelector sourceSelector,
        IArtifactCache? cache = null,
        DownloadAttemptPolicy? attemptPolicy = null,
        Action<DownloadAttemptEvent>? audit = null)
        : this(CreateDefaultHttpClient(), sourceSelector, cache, attemptPolicy, audit)
    {
    }

    public async Task<Result<DownloadReceipt>> DownloadAsync(
        DownloadRequest request,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        if (!IsValidRequest(request))
        {
            return Result<DownloadReceipt>.Failure(Problem("DOWNLOAD_REQUEST_INVALID", request.CorrelationId, request.Artifact));
        }

        string stagingRoot;
        string finalPath;
        try
        {
            stagingRoot = Path.GetFullPath(request.StagingDirectory);
            finalPath = Path.GetFullPath(Path.Combine(
                stagingRoot,
                request.Artifact.RelativeDestinationPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnderRoot(finalPath, stagingRoot))
            {
                return Result<DownloadReceipt>.Failure(Problem("DOWNLOAD_PATH_INVALID", request.CorrelationId, request.Artifact));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (HasReparsePointBetween(finalPath, stagingRoot))
            {
                return Result<DownloadReceipt>.Failure(Problem("DOWNLOAD_PATH_INVALID", request.CorrelationId, request.Artifact));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return Result<DownloadReceipt>.Failure(Problem("DOWNLOAD_PATH_INVALID", request.CorrelationId, request.Artifact));
        }

        if (cache is not null)
        {
            Result<string?> cached = await cache.GetAsync(request.Artifact, cancellationToken);
            if (!cached.IsSuccess)
            {
                return Result<DownloadReceipt>.Failure(cached.Problem!);
            }

            if (cached.Value is not null)
            {
                Result<DownloadReceipt> cachedResult = await MaterializeCachedAsync(
                    cached.Value,
                    finalPath,
                    request,
                    progress,
                    cancellationToken);
                if (cachedResult.IsSuccess)
                {
                    return cachedResult;
                }
            }
        }

        if (File.Exists(finalPath))
        {
            if (await VerifyFileAsync(finalPath, request.Artifact, cancellationToken))
            {
                return Result<DownloadReceipt>.Success(new DownloadReceipt(
                    finalPath,
                    new DownloadSourceId("local"),
                    0,
                    WasResumed: false,
                    PreferredHash(request.Artifact)));
            }

            Quarantine(finalPath, stagingRoot, new DownloadSourceId("local"), request.Artifact, "existing-file-mismatch", 0);
        }

        Result<IReadOnlyList<DownloadCandidate>> candidates = sourceSelector.Select(
            request.Artifact,
            request.SourcePreference,
            request.TemporaryFallbackApproved,
            request.CorrelationId);
        if (!candidates.IsSuccess)
        {
            return Result<DownloadReceipt>.Failure(candidates.Problem!);
        }

        Problem? lastProblem = null;
        foreach (DownloadCandidate candidate in candidates.Value)
        {
            CandidateResult candidateResult = await DownloadFromCandidateAsync(
                candidate,
                finalPath,
                stagingRoot,
                request,
                progress,
                cancellationToken);
            if (candidateResult.Receipt is not null)
            {
                if (cache is not null)
                {
                    _ = await cache.PutAsync(request.Artifact, finalPath, cancellationToken);
                }

                return Result<DownloadReceipt>.Success(candidateResult.Receipt);
            }

            lastProblem = candidateResult.Problem;
        }

        if (request.SourcePreference.PinnedSourceId is not null && !request.TemporaryFallbackApproved)
        {
            return Result<DownloadReceipt>.Failure(ConsentRequired(request, lastProblem));
        }

        return Result<DownloadReceipt>.Failure(lastProblem ?? Problem(
            "DOWNLOAD_UNAVAILABLE",
            request.CorrelationId,
            request.Artifact));
    }

    private async Task<CandidateResult> DownloadFromCandidateAsync(
        DownloadCandidate candidate,
        string finalPath,
        string stagingRoot,
        DownloadRequest request,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        Problem? lastProblem = null;
        for (int attempt = 1; attempt <= attemptPolicy.MaximumTransientRetries + 1; attempt++)
        {
            AttemptResult result = await DownloadAttemptAsync(
                candidate,
                finalPath,
                stagingRoot,
                request,
                progress,
                cancellationToken);
            if (result.Receipt is not null)
            {
                audit?.Invoke(new DownloadAttemptEvent(
                    request.CorrelationId,
                    candidate.SourceId.Value,
                    attempt,
                    result.StatusCode,
                    "success"));
                return CandidateResult.Success(result.Receipt);
            }

            lastProblem = result.Problem;
            audit?.Invoke(new DownloadAttemptEvent(
                request.CorrelationId,
                candidate.SourceId.Value,
                attempt,
                result.StatusCode,
                result.IsTransient ? "retryable-failure" : "failure"));
            if (!result.IsTransient || attempt > attemptPolicy.MaximumTransientRetries)
            {
                break;
            }

            TimeSpan delay = attemptPolicy.RetryDelay(attempt, result.RetryAfter);
            await attemptPolicy.DelayAsync(delay, cancellationToken);
        }

        return CandidateResult.Failure(lastProblem ?? Problem(
            "DOWNLOAD_UNAVAILABLE",
            request.CorrelationId,
            request.Artifact,
            candidate.SourceId));
    }

    private async Task<AttemptResult> DownloadAttemptAsync(
        DownloadCandidate candidate,
        string finalPath,
        string stagingRoot,
        DownloadRequest request,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        string partPath = finalPath + ".part";
        string metadataPath = partPath + ".meta.json";
        ResumeState resume = LoadResumeState(partPath, metadataPath, candidate, request.Artifact);

        try
        {
            HttpResponseMessage response = await SendFollowingRedirectsAsync(
                candidate.Uri,
                candidate.SourceId,
                resume,
                request.CorrelationId,
                cancellationToken);
            if (IsTransientStatus(response.StatusCode))
            {
                TimeSpan? retryAfter = GetRetryAfter(response);
                int status = (int)response.StatusCode;
                response.Dispose();
                return AttemptResult.TransientFailure(Problem(
                    "DOWNLOAD_UNAVAILABLE",
                    request.CorrelationId,
                    request.Artifact,
                    candidate.SourceId,
                    status), status, retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                response.Dispose();
                return AttemptResult.TerminalFailure(Problem(
                    "DOWNLOAD_HTTP_STATUS",
                    request.CorrelationId,
                    request.Artifact,
                    candidate.SourceId,
                    status), status);
            }

            if (HasContentEncoding(response))
            {
                int status = (int)response.StatusCode;
                response.Dispose();
                return AttemptResult.TerminalFailure(Problem(
                    "DOWNLOAD_RESPONSE_INVALID",
                    request.CorrelationId,
                    request.Artifact,
                    candidate.SourceId,
                    status), status);
            }

            bool resumed = resume.IsUsable;
            if (resumed && response.StatusCode == HttpStatusCode.PartialContent &&
                !IsMatchingPartialResponse(response, resume, request.Artifact))
            {
                response.Dispose();
                ResetPartial(partPath, metadataPath);
                resume = ResumeState.None;
                response = await SendFollowingRedirectsAsync(
                    candidate.Uri,
                    candidate.SourceId,
                    resume,
                    request.CorrelationId,
                    cancellationToken);
                resumed = false;
                if (IsTransientStatus(response.StatusCode))
                {
                    TimeSpan? retryAfter = GetRetryAfter(response);
                    int status = (int)response.StatusCode;
                    response.Dispose();
                    return AttemptResult.TransientFailure(Problem(
                        "DOWNLOAD_UNAVAILABLE",
                        request.CorrelationId,
                        request.Artifact,
                        candidate.SourceId,
                        status), status, retryAfter);
                }

                if (!response.IsSuccessStatusCode || HasContentEncoding(response))
                {
                    int status = (int)response.StatusCode;
                    response.Dispose();
                    return AttemptResult.TerminalFailure(Problem(
                        "DOWNLOAD_RESPONSE_INVALID",
                        request.CorrelationId,
                        request.Artifact,
                        candidate.SourceId,
                        status), status);
                }
            }
            else if (resumed && response.StatusCode == HttpStatusCode.OK)
            {
                response.Dispose();
                ResetPartial(partPath, metadataPath);
                resume = ResumeState.None;
                response = await SendFollowingRedirectsAsync(
                    candidate.Uri,
                    candidate.SourceId,
                    resume,
                    request.CorrelationId,
                    cancellationToken);
                resumed = false;
                if (IsTransientStatus(response.StatusCode) || !response.IsSuccessStatusCode || HasContentEncoding(response))
                {
                    int status = (int)response.StatusCode;
                    TimeSpan? retryAfter = GetRetryAfter(response);
                    response.Dispose();
                    return IsTransientStatus((HttpStatusCode)status)
                        ? AttemptResult.TransientFailure(Problem("DOWNLOAD_UNAVAILABLE", request.CorrelationId, request.Artifact, candidate.SourceId, status), status, retryAfter)
                        : AttemptResult.TerminalFailure(Problem("DOWNLOAD_RESPONSE_INVALID", request.CorrelationId, request.Artifact, candidate.SourceId, status), status);
                }
            }
            else if (resumed && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose();
                ResetPartial(partPath, metadataPath);
                resume = ResumeState.None;
                response = await SendFollowingRedirectsAsync(
                    candidate.Uri,
                    candidate.SourceId,
                    resume,
                    request.CorrelationId,
                    cancellationToken);
                resumed = false;
                if (!response.IsSuccessStatusCode || HasContentEncoding(response))
                {
                    int status = (int)response.StatusCode;
                    response.Dispose();
                    return AttemptResult.TerminalFailure(Problem("DOWNLOAD_RESPONSE_INVALID", request.CorrelationId, request.Artifact, candidate.SourceId, status), status);
                }
            }
            else if (!resumed && response.StatusCode == HttpStatusCode.PartialContent &&
                     !IsFullPartialResponse(response, request.Artifact))
            {
                int status = (int)response.StatusCode;
                response.Dispose();
                return AttemptResult.TerminalFailure(Problem("DOWNLOAD_RESPONSE_INVALID", request.CorrelationId, request.Artifact, candidate.SourceId, status), status);
            }

            try
            {
                return await ConsumeResponseAsync(
                    response,
                    candidate,
                    finalPath,
                    stagingRoot,
                    partPath,
                    metadataPath,
                    request,
                    resume,
                    resumed,
                    progress,
                    cancellationToken);
            }
            catch (SlowDownloadException)
            {
                return AttemptResult.TransientFailure(Problem(
                    "DOWNLOAD_LOW_SPEED",
                    request.CorrelationId,
                    request.Artifact,
                    candidate.SourceId), null, null);
            }
            catch (HashMismatchException mismatch)
            {
                Quarantine(partPath, stagingRoot, candidate.SourceId, request.Artifact, mismatch.Reason, mismatch.ActualBytes);
                return AttemptResult.TerminalFailure(Problem(
                    "DOWNLOAD_HASH_MISMATCH",
                    request.CorrelationId,
                    request.Artifact,
                    candidate.SourceId), null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AttemptResult.TransientFailure(Problem(
                "DOWNLOAD_UNAVAILABLE",
                request.CorrelationId,
                request.Artifact,
                candidate.SourceId), null, null);
        }
        catch (HttpRequestException)
        {
            return AttemptResult.TransientFailure(Problem(
                "DOWNLOAD_UNAVAILABLE",
                request.CorrelationId,
                request.Artifact,
                candidate.SourceId), null, null);
        }
        catch (RedirectRejectedException)
        {
            return AttemptResult.TerminalFailure(Problem(
                "DOWNLOAD_REDIRECT_REJECTED",
                request.CorrelationId,
                request.Artifact,
                candidate.SourceId), null);
        }
        catch (InvalidDataException)
        {
            return AttemptResult.TerminalFailure(Problem(
                "DOWNLOAD_RESPONSE_INVALID",
                request.CorrelationId,
                request.Artifact,
                candidate.SourceId), null);
        }
    }

    private async Task<AttemptResult> ConsumeResponseAsync(
        HttpResponseMessage response,
        DownloadCandidate candidate,
        string finalPath,
        string stagingRoot,
        string partPath,
        string metadataPath,
        DownloadRequest request,
        ResumeState resume,
        bool resumed,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        using (response)
        {
            long start = resumed ? resume.BytesPresent : 0;
            HashAccumulator hashes = new(request.Artifact.Hashes);
            try
            {
                if (start > 0)
                {
                    await hashes.AppendFilePrefixAsync(partPath, start, cancellationToken);
                }

                await using FileStream part = new(
                    partPath,
                    resumed ? FileMode.OpenOrCreate : FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (resumed)
                {
                    part.Position = start;
                }
                else
                {
                    part.SetLength(0);
                }

                PartMetadata metadata = new(
                    candidate.SourceId.Value,
                    StrongEtag(response.Headers.ETag) ?? resume.ETag,
                    response.Content.Headers.LastModified?.ToString("R"),
                    request.Artifact.ExpectedSize,
                    start);
                await WriteMetadataAsync(metadataPath, metadata, cancellationToken);

                await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    long total = start;
                    long transferred = 0;
                    Stopwatch speedClock = Stopwatch.StartNew();
                    long speedWindowBytes = total;
                    TimeSpan speedWindowStart = speedClock.Elapsed;
                    while (true)
                    {
                        int read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        if (total > request.Artifact.ExpectedSize - read)
                        {
                            throw new HashMismatchException("too-many-bytes", total + read);
                        }

                        await part.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hashes.Append(buffer, 0, read);
                        total += read;
                        transferred += read;
                        await WriteMetadataAsync(metadataPath, metadata with { BytesPresent = total }, cancellationToken);
                        progress.Report(new OperationProgress(
                            "download",
                            0,
                            0,
                            transferred,
                            request.Artifact.ExpectedSize));

                        if (total >= attemptPolicy.LowSpeedGraceBytes)
                        {
                            TimeSpan elapsed = speedClock.Elapsed - speedWindowStart;
                            if (elapsed >= attemptPolicy.LowSpeedWindow)
                            {
                                long windowBytes = total - speedWindowBytes;
                                double bytesPerSecond = windowBytes / Math.Max(elapsed.TotalSeconds, 0.001);
                                if (bytesPerSecond < attemptPolicy.LowSpeedThresholdBytesPerSecond)
                                {
                                    throw new SlowDownloadException();
                                }

                                speedWindowBytes = total;
                                speedWindowStart = speedClock.Elapsed;
                            }
                        }
                    }

                    await part.FlushAsync(cancellationToken);
                    part.Flush(true);
                    if (total != request.Artifact.ExpectedSize || !hashes.Matches(request.Artifact))
                    {
                        throw new HashMismatchException(total < request.Artifact.ExpectedSize ? "too-few-bytes" : "hash-mismatch", total);
                    }

                    // Windows keeps an open FileStream as an exclusive handle even
                    // after Flush. Release it before the atomic promotion below.
                    await part.DisposeAsync();
                    File.Move(partPath, finalPath, true);
                    TryDeleteFile(metadataPath);
                    return AttemptResult.Success(new DownloadReceipt(
                        finalPath,
                        candidate.SourceId,
                        transferred,
                        resumed,
                        PreferredHash(request.Artifact)),
                        response.StatusCode);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            finally
            {
                hashes.Dispose();
            }
        }
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        Uri initialUri,
        DownloadSourceId sourceId,
        ResumeState resume,
        string correlationId,
        CancellationToken cancellationToken)
    {
        Uri current = initialUri;
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { current.AbsoluteUri };
        for (int redirect = 0; ; redirect++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (resume.IsUsable)
            {
                request.Headers.Range = new RangeHeaderValue(resume.BytesPresent, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(resume.ETag!));
            }

            HttpResponseMessage response;
            using (CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(attemptPolicy.ConnectTimeout);
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token);
            }

            if ((int)response.StatusCode is < 300 or >= 400)
            {
                return response;
            }

            if (redirect >= attemptPolicy.MaximumRedirects || response.Headers.Location is null)
            {
                response.Dispose();
                throw new RedirectRejectedException();
            }

            Uri next;
            try
            {
                next = new Uri(current, response.Headers.Location);
            }
            catch (UriFormatException)
            {
                response.Dispose();
                throw new RedirectRejectedException();
            }

            if (!next.IsAbsoluteUri || next.Scheme != Uri.UriSchemeHttps || next.UserInfo.Length > 0 || next.Fragment.Length > 0 ||
                !visited.Add(next.AbsoluteUri))
            {
                response.Dispose();
                throw new RedirectRejectedException();
            }

            response.Dispose();
            current = next;
            _ = sourceId;
            _ = correlationId;
        }
    }

    private static bool IsMatchingPartialResponse(
        HttpResponseMessage response,
        ResumeState resume,
        DownloadArtifact artifact) =>
        response.Content.Headers.ContentRange is { } range &&
        range.From == resume.BytesPresent && range.To is not null && range.Length == artifact.ExpectedSize &&
        IsStrongEtag(response.Headers.ETag) &&
        string.Equals(response.Headers.ETag!.Tag, resume.ETag, StringComparison.Ordinal);

    private static bool IsFullPartialResponse(HttpResponseMessage response, DownloadArtifact artifact) =>
        response.Content.Headers.ContentRange is { } range && range.From == 0 && range.Length == artifact.ExpectedSize;

    private static bool HasContentEncoding(HttpResponseMessage response) =>
        response.Content.Headers.ContentEncoding.Any(encoding =>
            !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase));

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout || statusCode == (HttpStatusCode)429 ||
        (int)statusCode is >= 500 and <= 599;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            TimeSpan delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private static ResumeState LoadResumeState(
        string partPath,
        string metadataPath,
        DownloadCandidate candidate,
        DownloadArtifact artifact)
    {
        if (!File.Exists(partPath) || !File.Exists(metadataPath))
        {
            return ResumeState.None;
        }

        try
        {
            PartMetadata? metadata = JsonSerializer.Deserialize<PartMetadata>(File.ReadAllText(metadataPath), PartMetadataJsonOptions);
            long bytesPresent = new FileInfo(partPath).Length;
            if (metadata is null || !string.Equals(metadata.SourceId, candidate.SourceId.Value, StringComparison.Ordinal) ||
                metadata.ExpectedSize != artifact.ExpectedSize || metadata.BytesPresent != bytesPresent ||
                bytesPresent <= 0 || bytesPresent >= artifact.ExpectedSize || !IsStrongEtag(metadata.Etag))
            {
                ResetPartial(partPath, metadataPath);
                return ResumeState.None;
            }

            return new ResumeState(true, bytesPresent, metadata.Etag, metadata.LastModified);
        }
        catch (JsonException)
        {
            ResetPartial(partPath, metadataPath);
            return ResumeState.None;
        }
        catch (IOException)
        {
            ResetPartial(partPath, metadataPath);
            return ResumeState.None;
        }
    }

    private static async Task WriteMetadataAsync(string path, PartMetadata metadata, CancellationToken cancellationToken)
    {
        string temporaryPath = path + ".tmp";
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, PartMetadataJsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void ResetPartial(string partPath, string metadataPath)
    {
        TryDeleteFile(partPath);
        TryDeleteFile(metadataPath);
    }

    private static void Quarantine(
        string path,
        string stagingRoot,
        DownloadSourceId sourceId,
        DownloadArtifact artifact,
        string reason,
        long actualBytes)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string quarantineDirectory = Path.Combine(stagingRoot, ".quarantine");
        Directory.CreateDirectory(quarantineDirectory);
        string badPath = Path.Combine(quarantineDirectory, artifact.ArtifactId + "." + Guid.NewGuid().ToString("N") + ".bad");
        File.Move(path, badPath, true);
        string metadataPath = badPath + ".json";
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
        {
            sourceId = sourceId.Value,
            artifactId = artifact.ArtifactId,
            reason,
            expectedSize = artifact.ExpectedSize,
            actualBytes,
        }, PartMetadataJsonOptions));
        TryDeleteFile(path + ".meta.json");
    }

    private static async Task<Result<DownloadReceipt>> MaterializeCachedAsync(
        string cachedPath,
        string finalPath,
        DownloadRequest request,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream source = new(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using FileStream target = new(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await source.CopyToAsync(target, BufferSize, cancellationToken);
            await target.FlushAsync(cancellationToken);
            target.Flush(true);
            if (!await VerifyFileAsync(finalPath, request.Artifact, cancellationToken))
            {
                TryDeleteFile(finalPath);
                return Result<DownloadReceipt>.Failure(Problem("DOWNLOAD_HASH_MISMATCH", request.CorrelationId, request.Artifact));
            }

            progress.Report(new OperationProgress("download", 1, 1, request.Artifact.ExpectedSize, request.Artifact.ExpectedSize));
            return Result<DownloadReceipt>.Success(new DownloadReceipt(
                finalPath,
                new DownloadSourceId("cache"),
                0,
                WasResumed: false,
                PreferredHash(request.Artifact)));
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(finalPath);
            throw;
        }
        catch (IOException)
        {
            TryDeleteFile(finalPath);
            return Result<DownloadReceipt>.Failure(Problem("DOWNLOAD_CACHE_UNAVAILABLE", request.CorrelationId, request.Artifact));
        }
    }

    private static async Task<bool> VerifyFileAsync(
        string path,
        DownloadArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.ExpectedSize)
        {
            return false;
        }

        HashAccumulator hashes = new(artifact.Hashes);
        try
        {
            await hashes.AppendFilePrefixAsync(path, artifact.ExpectedSize, cancellationToken);
            return hashes.Matches(artifact);
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            hashes.Dispose();
        }
    }

    private static ArtifactHash PreferredHash(DownloadArtifact artifact) =>
        artifact.Hashes.FirstOrDefault(static hash => hash.NormalizedAlgorithm == "sha256") ?? artifact.Hashes[0];

    private static EntityTagHeaderValue? ParseStrongEtag(string? value) =>
        value is not null && EntityTagHeaderValue.TryParse(value, out EntityTagHeaderValue? parsed) && IsStrongEtag(parsed)
            ? parsed
            : null;

    private static string? StrongEtag(EntityTagHeaderValue? etag) => IsStrongEtag(etag) ? etag!.Tag : null;

    private static bool IsStrongEtag(EntityTagHeaderValue? etag) => etag is not null && !etag.IsWeak && !string.IsNullOrWhiteSpace(etag.Tag);

    private static bool IsStrongEtag(string? etag) => ParseStrongEtag(etag) is not null;

    private static bool IsValidRequest(DownloadRequest request) =>
        request.Artifact is not null &&
        !string.IsNullOrWhiteSpace(request.StagingDirectory) &&
        request.SourcePreference is not null &&
        !string.IsNullOrWhiteSpace(request.CorrelationId);

    private static void ValidatePolicy(DownloadAttemptPolicy policy)
    {
        if (policy.MaximumTransientRetries < 0 || policy.ConnectTimeout <= TimeSpan.Zero || policy.MaximumRedirects < 0 ||
            policy.LowSpeedThresholdBytesPerSecond < 0 || policy.LowSpeedGraceBytes < 0 || policy.LowSpeedWindow <= TimeSpan.Zero ||
            policy.RetryAfterMaximum < TimeSpan.Zero || policy.DelayAsync is null)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Download attempt limits are invalid.");
        }
    }

    private static HttpClient CreateDefaultHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static bool IsUnderRoot(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePointBetween(string path, string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(path)
            : Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }

        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Problem ConsentRequired(DownloadRequest request, Problem? lastProblem) => new(
        "DOWNLOAD_FALLBACK_CONSENT_REQUIRED",
        ProblemStage.Download,
        "problem.download.fallback_consent_required",
        false,
        request.CorrelationId,
        ["action.download.approve_fallback"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sourceId"] = request.SourcePreference.PinnedSourceId?.Value ?? string.Empty,
            ["lastFailure"] = lastProblem?.Code ?? "DOWNLOAD_UNAVAILABLE",
        });

    private static Problem Problem(
        string code,
        string correlationId,
        DownloadArtifact? artifact,
        DownloadSourceId? sourceId = null,
        int? statusCode = null) => new(
        code,
        ProblemStage.Download,
        code switch
        {
            "DOWNLOAD_HASH_MISMATCH" => "problem.download.hash_mismatch",
            "DOWNLOAD_FALLBACK_CONSENT_REQUIRED" => "problem.download.fallback_consent_required",
            "DOWNLOAD_REDIRECT_REJECTED" => "problem.download.redirect_rejected",
            "DOWNLOAD_RESPONSE_INVALID" => "problem.download.response_invalid",
            "DOWNLOAD_PATH_INVALID" => "problem.download.path_invalid",
            "DOWNLOAD_REQUEST_INVALID" => "problem.download.request_invalid",
            _ => "problem.download.unavailable",
        },
        code is "DOWNLOAD_UNAVAILABLE" or "DOWNLOAD_LOW_SPEED",
        correlationId,
        ["action.download.retry"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["artifactId"] = artifact?.ArtifactId ?? string.Empty,
            ["sourceId"] = sourceId?.Value ?? string.Empty,
            ["statusCode"] = statusCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        });

    private sealed record CandidateResult(DownloadReceipt? Receipt, Problem? Problem)
    {
        public static CandidateResult Success(DownloadReceipt receipt) => new(receipt, null);

        public static CandidateResult Failure(Problem problem) => new(null, problem);
    }

    private sealed record AttemptResult(
        DownloadReceipt? Receipt,
        Problem? Problem,
        bool IsTransient,
        int? StatusCode,
        TimeSpan? RetryAfter)
    {
        public static AttemptResult Success(DownloadReceipt receipt, HttpStatusCode statusCode) =>
            new(receipt, null, false, (int)statusCode, null);

        public static AttemptResult TerminalFailure(Problem problem, int? statusCode) =>
            new(null, problem, false, statusCode, null);

        public static AttemptResult TransientFailure(Problem problem, int? statusCode, TimeSpan? retryAfter) =>
            new(null, problem, true, statusCode, retryAfter);
    }

    private sealed record ResumeState(bool IsUsable, long BytesPresent, string? ETag, string? LastModified)
    {
        public static ResumeState None { get; } = new(false, 0, null, null);
    }

    private sealed record PartMetadata(
        string SourceId,
        string? Etag,
        string? LastModified,
        long ExpectedSize,
        long BytesPresent);

    private sealed class HashAccumulator : IDisposable
    {
        private readonly Dictionary<string, IncrementalHash> hashes;

        public HashAccumulator(IReadOnlyList<ArtifactHash> expectedHashes)
        {
            hashes = expectedHashes
                .Select(static hash => hash.NormalizedAlgorithm)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(
                    static algorithm => algorithm,
                    static algorithm => IncrementalHash.CreateHash(ToHashAlgorithm(algorithm)),
                    StringComparer.Ordinal);
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            foreach (IncrementalHash hash in hashes.Values)
            {
                hash.AppendData(buffer, offset, count);
            }
        }

        public async Task AppendFilePrefixAsync(string path, long length, CancellationToken cancellationToken)
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long remaining = length;
                while (remaining > 0)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, remaining)), cancellationToken);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    Append(buffer, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public bool Matches(DownloadArtifact artifact) => artifact.Hashes.All(expected =>
            hashes.TryGetValue(expected.NormalizedAlgorithm, out IncrementalHash? hash) &&
            string.Equals(
                Convert.ToHexString(hash.GetHashAndReset()),
                expected.NormalizedHexDigest,
                StringComparison.OrdinalIgnoreCase));

        public void Dispose()
        {
            foreach (IncrementalHash hash in hashes.Values)
            {
                hash.Dispose();
            }
        }

        private static HashAlgorithmName ToHashAlgorithm(string algorithm) => algorithm switch
        {
            "sha1" => HashAlgorithmName.SHA1,
            "sha256" => HashAlgorithmName.SHA256,
            _ => throw new InvalidOperationException("Unsupported artifact hash algorithm."),
        };
    }

    private sealed class SlowDownloadException : Exception;

    private sealed class RedirectRejectedException : Exception;

    private sealed class HashMismatchException(string reason, long actualBytes) : Exception
    {
        public string Reason { get; } = reason;
        public long ActualBytes { get; } = actualBytes;
    }
}
