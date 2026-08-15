using System.Diagnostics;

namespace Lacertae.Updater;

public sealed record ProcessWaitResult(bool Exited, string? FailureCode)
{
    public static ProcessWaitResult Success() => new(true, null);

    public static ProcessWaitResult Failure(string code) => new(false, code);
}

public interface IUpdateParentWaiter
{
    Task<ProcessWaitResult> WaitForExitAsync(
        int processId,
        string expectedExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Waits for the exact process that started the updater. The executable path
/// is checked before waiting, preventing PID reuse from turning an unrelated
/// process into permission to replace launcher files.
/// </summary>
public sealed class ProcessWaiter : IUpdateParentWaiter
{
    private readonly TimeSpan pollInterval;

    public ProcessWaiter(TimeSpan? pollInterval = null)
    {
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        if (this.pollInterval <= TimeSpan.Zero || this.pollInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
    }

    public async Task<ProcessWaitResult> WaitForExitAsync(
        int processId,
        string expectedExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (processId <= 0 || string.IsNullOrWhiteSpace(expectedExecutablePath) ||
            !Path.IsPathFullyQualified(expectedExecutablePath) || timeout <= TimeSpan.Zero)
        {
            return ProcessWaitResult.Failure("UPDATE_PARENT_INVALID");
        }

        string expected = Path.GetFullPath(expectedExecutablePath);
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        bool observedParent = false;
        try
        {
            while (true)
            {
                Process? process = TryGetProcess(processId);
                if (process is null)
                {
                    return observedParent
                        ? ProcessWaitResult.Success()
                        : ProcessWaitResult.Failure("UPDATE_PARENT_UNAVAILABLE");
                }

                using (process)
                {
                    observedParent = true;
                    if (!IsExpectedExecutable(process, expected))
                    {
                        return ProcessWaitResult.Failure("UPDATE_PARENT_PATH_MISMATCH");
                    }

                    if (process.HasExited)
                    {
                        return ProcessWaitResult.Success();
                    }

                    try
                    {
                        await Task.Delay(pollInterval, timeoutSource.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return ProcessWaitResult.Failure("UPDATE_PARENT_TIMEOUT");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return ProcessWaitResult.Failure("UPDATE_PARENT_UNAVAILABLE");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ProcessWaitResult.Failure("UPDATE_PARENT_UNAVAILABLE");
        }
        catch (NotSupportedException)
        {
            return ProcessWaitResult.Failure("UPDATE_PARENT_UNAVAILABLE");
        }
        catch (UnauthorizedAccessException)
        {
            return ProcessWaitResult.Failure("UPDATE_PARENT_UNAVAILABLE");
        }
    }

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsExpectedExecutable(Process process, string expectedPath)
    {
        string? actualPath = process.MainModule?.FileName;
        return actualPath is not null &&
            string.Equals(
                Path.GetFullPath(actualPath),
                expectedPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
