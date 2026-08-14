using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Lacertae.Application.Diagnostics;
using Lacertae.Application.Games;
using Lacertae.Application.Launch;
using Lacertae.Domain.Common;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Platform.Windows.Launch;

public sealed class WindowsGameProcessHost : IGameProcessHost
{
    private readonly ILogSanitizer sanitizer;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<int, TrackedProcess> trackedProcesses = new();
    private readonly ConcurrentDictionary<int, byte> userTerminated = new();

    public WindowsGameProcessHost(
        ILogSanitizer sanitizer,
        TimeProvider? timeProvider = null)
    {
        this.sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<int>? ProcessStarted;

    public IReadOnlyCollection<int> ActiveProcessIds => trackedProcesses.Keys.ToArray();

    public async Task<Result<GameExitResult>> RunAsync(
        GameProcessSpec spec,
        IProgress<GameLogLine> log,
        CancellationToken waitCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(log);
        waitCancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset startedUtc = timeProvider.GetUtcNow();
        ProcessStartInfo startInfo = new()
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = false,
        };

        string[] revealedArguments = spec.ArgumentList.Select(static argument => argument.Reveal()).ToArray();
        Dictionary<string, string> revealedEnvironment = spec.Environment.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Reveal(),
            StringComparer.Ordinal);
        foreach (string argument in revealedArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string key, string value) in revealedEnvironment)
        {
            startInfo.Environment[key] = value;
        }

        Process? process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        int processId;
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return Result<GameExitResult>.Failure(StartProblem(spec));
            }

            processId = process.Id;
            trackedProcesses[processId] = new TrackedProcess(process, spec.CorrelationId, startedUtc);
            try
            {
                ProcessStarted?.Invoke(processId);
            }
            catch
            {
                // Observers must not be able to terminate a successfully started game.
            }
        }
        catch (Win32Exception)
        {
            process.Dispose();
            return Result<GameExitResult>.Failure(StartProblem(spec));
        }
        catch (InvalidOperationException)
        {
            process.Dispose();
            return Result<GameExitResult>.Failure(StartProblem(spec));
        }
        catch (UnauthorizedAccessException)
        {
            process.Dispose();
            return Result<GameExitResult>.Failure(StartProblem(spec));
        }
        catch (IOException)
        {
            process.Dispose();
            return Result<GameExitResult>.Failure(StartProblem(spec));
        }
        finally
        {
            Array.Clear(revealedArguments);
            revealedEnvironment.Clear();
            startInfo.ArgumentList.Clear();
            startInfo.Environment.Clear();
        }

        Task drainStandardOutput = DrainAsync(
            process.StandardOutput,
            false,
            log,
            spec.CorrelationId);
        Task drainStandardError = DrainAsync(
            process.StandardError,
            true,
            log,
            spec.CorrelationId);
        Task waitForExit = process.WaitForExitAsync(CancellationToken.None);
        Task waitCancellation = WaitForCancellationAsync(waitCancellationToken);
        Task completed = await Task.WhenAny(waitForExit, waitCancellation);
        if (completed == waitCancellation && !waitForExit.IsCompleted)
        {
            _ = DetachAndCleanupAsync(processId, process, drainStandardOutput, drainStandardError, waitForExit);
            return Result<GameExitResult>.Failure(new Problem(
                "PROCESS_WAIT_CANCELLED",
                ProblemStage.Process,
                "problem.process.wait_cancelled",
                false,
                spec.CorrelationId,
                ["action.process.keep_running"]));
        }

        await waitForExit;
        await Task.WhenAll(drainStandardOutput, drainStandardError);
        return CompleteProcess(processId, process, spec.CorrelationId, startedUtc);
    }

    public Task<Result<Unit>> StopAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!trackedProcesses.TryGetValue(processId, out TrackedProcess? tracked))
        {
            return Task.FromResult(Result<Unit>.Failure(new Problem(
                "PROCESS_NOT_RUNNING",
                ProblemStage.Process,
                "problem.process.not_running",
                false,
                Guid.NewGuid().ToString("N"),
                ["action.process.refresh"])));
        }

        try
        {
            if (tracked.Process.HasExited)
            {
                return Task.FromResult(Result<Unit>.Failure(new Problem(
                    "PROCESS_NOT_RUNNING",
                    ProblemStage.Process,
                    "problem.process.not_running",
                    false,
                    tracked.CorrelationId,
                    ["action.process.refresh"])));
            }

            userTerminated[processId] = 0;
            tracked.Process.Kill(entireProcessTree: true);
            return Task.FromResult(Result.Success());
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(Result<Unit>.Failure(StopProblem(tracked.CorrelationId)));
        }
        catch (NotSupportedException)
        {
            return Task.FromResult(Result<Unit>.Failure(StopProblem(tracked.CorrelationId)));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Result<Unit>.Failure(StopProblem(tracked.CorrelationId)));
        }
        catch (Win32Exception)
        {
            return Task.FromResult(Result<Unit>.Failure(StopProblem(tracked.CorrelationId)));
        }
    }

    private async Task DrainAsync(
        StreamReader reader,
        bool standardError,
        IProgress<GameLogLine> log,
        string correlationId)
    {
        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
        {
            string sanitized = sanitizer.Sanitize(line);
            log.Report(new GameLogLine(timeProvider.GetUtcNow(), standardError, sanitized));
        }
    }

    private async Task DetachAndCleanupAsync(
        int processId,
        Process process,
        Task standardOutput,
        Task standardError,
        Task waitForExit)
    {
        try
        {
            await waitForExit;
            await Task.WhenAll(standardOutput, standardError);
        }
        catch
        {
            // The caller has intentionally detached; cleanup must not become an
            // unobserved task exception or affect the desktop operation.
        }
        finally
        {
            trackedProcesses.TryRemove(processId, out _);
            userTerminated.TryRemove(processId, out _);
            process.Dispose();
        }
    }

    private Result<GameExitResult> CompleteProcess(
        int processId,
        Process process,
        string correlationId,
        DateTimeOffset startedUtc)
    {
        try
        {
            int exitCode = process.ExitCode;
            bool terminatedByUser = userTerminated.TryRemove(processId, out _);
            trackedProcesses.TryRemove(processId, out _);
            process.Dispose();
            return Result<GameExitResult>.Success(new GameExitResult(
                processId,
                exitCode,
                terminatedByUser ? GameProcessState.UserTerminated : GameProcessState.Exited,
                startedUtc,
                timeProvider.GetUtcNow(),
                correlationId));
        }
        catch (InvalidOperationException)
        {
            trackedProcesses.TryRemove(processId, out _);
            process.Dispose();
            return Result<GameExitResult>.Failure(new Problem(
                "PROCESS_EXIT_STATE_UNAVAILABLE",
                ProblemStage.Process,
                "problem.process.exit_state_unavailable",
                false,
                correlationId,
                ["action.process.refresh"]));
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static Problem StartProblem(GameProcessSpec spec) => new(
        "PROCESS_START_FAILED",
        ProblemStage.Process,
        "problem.process.start_failed",
        false,
        spec.CorrelationId,
        ["action.process.check_executable"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["executable"] = Path.GetFileName(spec.FileName),
        });

    private static Problem StopProblem(string correlationId) => new(
        "PROCESS_STOP_FAILED",
        ProblemStage.Process,
        "problem.process.stop_failed",
        false,
        correlationId,
        ["action.process.stop_again"]);

    private sealed record TrackedProcess(
        Process Process,
        string CorrelationId,
        DateTimeOffset StartedUtc);
}
