namespace Lacertae.Infrastructure.Downloads;

public sealed class DownloadAttemptPolicy
{
    public int MaximumTransientRetries { get; init; } = 2;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan SecondRetryDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RetryAfterMaximum { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumRedirects { get; init; } = 3;
    public long LowSpeedThresholdBytesPerSecond { get; init; } = 64 * 1024;
    public long LowSpeedGraceBytes { get; init; } = 5L * 1024 * 1024;
    public TimeSpan LowSpeedWindow { get; init; } = TimeSpan.FromSeconds(30);
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    public TimeSpan RetryDelay(int retryNumber, TimeSpan? retryAfter)
    {
        if (retryAfter is { } serverDelay)
        {
            return serverDelay <= RetryAfterMaximum ? serverDelay : RetryAfterMaximum;
        }

        TimeSpan baseDelay = retryNumber switch
        {
            1 => FirstRetryDelay,
            2 => SecondRetryDelay,
            _ => SecondRetryDelay,
        };
        if (baseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        double jitter = Random.Shared.NextDouble() * 0.25;
        return baseDelay + TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * jitter);
    }
}

public sealed record DownloadAttemptEvent(
    string CorrelationId,
    string SourceId,
    int Attempt,
    int? StatusCode,
    string Outcome);
