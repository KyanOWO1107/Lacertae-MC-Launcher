using System.ComponentModel;
using System.Diagnostics;
using Lacertae.Application.Processes;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Processes;

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<Result<ProcessResult>> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new ArgumentException("Process file name cannot be blank.", nameof(request));
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Process timeout must be positive.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = request.CreateNoWindow,
        };

        foreach (string argument in request.ArgumentList)
        {
            if (argument is null)
            {
                throw new ArgumentException("Process arguments cannot contain null values.", nameof(request));
            }

            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string key, string value) in request.Environment)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                throw new ArgumentException("Process environment entries must have a key and value.", nameof(request));
            }

            startInfo.Environment[key] = value;
        }

        using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                return Result<ProcessResult>.Failure(ProcessStartProblem(request.FileName));
            }
        }
        catch (Win32Exception)
        {
            return Result<ProcessResult>.Failure(ProcessStartProblem(request.FileName));
        }
        catch (InvalidOperationException)
        {
            return Result<ProcessResult>.Failure(ProcessStartProblem(request.FileName));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<ProcessResult>.Failure(ProcessStartProblem(request.FileName));
        }
        catch (IOException)
        {
            return Result<ProcessResult>.Failure(ProcessStartProblem(request.FileName));
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(request.Timeout);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            KillProcessTree(process);
            await WaitForExitAfterKillAsync(process);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await WaitForExitAfterKillAsync(process);
            throw;
        }

        string output = await standardOutput;
        string error = await standardError;
        return Result<ProcessResult>.Success(new ProcessResult(
            process.ExitCode,
            output,
            error,
            timedOut));
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Problem ProcessStartProblem(string fileName) => new(
        "PROCESS_START_FAILED",
        ProblemStage.Process,
        "problem.process.start_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.process.check_executable"],
        new Dictionary<string, string> { ["executable"] = GetSafeBasename(fileName) });

    internal static string GetSafeBasename(string path)
    {
        string normalized = path.Replace('\\', '/');
        int separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }
}
