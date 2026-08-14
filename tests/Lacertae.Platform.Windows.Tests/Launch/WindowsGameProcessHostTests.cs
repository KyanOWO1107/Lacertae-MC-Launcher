using Lacertae.Application.Games;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Launch;
using Lacertae.Platform.Windows.Launch;

namespace Lacertae.Platform.Windows.Tests.Launch;

public sealed class WindowsGameProcessHostTests
{
    [Fact]
    public async Task RunAsyncUsesArgumentListAndSanitizesBothOutputStreams()
    {
        FakeSanitizer sanitizer = new();
        WindowsGameProcessHost host = new(sanitizer);
        List<GameLogLine> logs = [];
        string workDirectory = CreateTemporaryDirectory();
        GameProcessSpec spec = new(
            ComSpec(),
            [new SensitiveString("/c"), new SensitiveString("echo access-token-secret & echo error-secret 1>&2")],
            workDirectory,
            new Dictionary<string, SensitiveString>(StringComparer.Ordinal),
            "corr-host");

        var result = await host.RunAsync(
            spec,
            new Progress<GameLogLine>(logs.Add),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(GameProcessState.Exited, result.Value.State);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.Contains(logs, line => line.SanitizedText.Contains("[REDACTED]", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.SanitizedText.Contains("access-token-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.SanitizedText.Contains("error-secret", StringComparison.Ordinal));
        Assert.Contains(sanitizer.Inputs, value => value.Contains("access-token-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsyncMapsStartFailureWithoutPublishingRawPath()
    {
        string missing = Path.Combine(Path.GetTempPath(), "missing-java-" + Guid.NewGuid().ToString("N") + ".exe");
        WindowsGameProcessHost host = new(new FakeSanitizer());
        GameProcessSpec spec = new(
            missing,
            [],
            CreateTemporaryDirectory(),
            new Dictionary<string, SensitiveString>(StringComparer.Ordinal),
            "corr-host");

        var result = await host.RunAsync(
            spec,
            new Progress<GameLogLine>(_ => { }),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("PROCESS_START_FAILED", result.Problem?.Code);
        Assert.DoesNotContain(missing, result.Problem?.SafeContext.Values ?? []);
    }

    [Fact]
    public async Task CancellationAfterStartDetachesWaitAndStopTerminatesTrackedTree()
    {
        WindowsGameProcessHost host = new(new FakeSanitizer());
        TaskCompletionSource<int> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ProcessStarted += processId => started.TrySetResult(processId);
        using CancellationTokenSource waitCancellation = new();
        GameProcessSpec spec = new(
            ComSpec(),
            [new SensitiveString("/c"), new SensitiveString("ping 127.0.0.1 -n 20 > nul")],
            CreateTemporaryDirectory(),
            new Dictionary<string, SensitiveString>(StringComparer.Ordinal),
            "corr-host");

        Task<Lacertae.Domain.Results.Result<GameExitResult>> running = host.RunAsync(
            spec,
            new Progress<GameLogLine>(_ => { }),
            waitCancellation.Token);
        int processId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        waitCancellation.Cancel();
        var detached = await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(detached.IsSuccess);
        Assert.Equal("PROCESS_WAIT_CANCELLED", detached.Problem?.Code);

        var stopped = await host.StopAsync(processId, TestContext.Current.CancellationToken);
        Assert.True(stopped.IsSuccess, stopped.Problem?.Code);
    }

    private static string ComSpec() => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lacertae-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeSanitizer : Lacertae.Application.Diagnostics.ILogSanitizer
    {
        public List<string> Inputs { get; } = [];

        public string Sanitize(string value)
        {
            Inputs.Add(value);
            return value
                .Replace("access-token-secret", "[REDACTED]", StringComparison.Ordinal)
                .Replace("error-secret", "[REDACTED]", StringComparison.Ordinal);
        }
    }
}
