using System.ComponentModel;
using System.Diagnostics;
using Lacertae.Application.Updates;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Updates;

/// <summary>
/// Starts the standalone updater without shell parsing. The updater receives
/// only the absolute plan path and is intentionally not awaited by the UI.
/// </summary>
public sealed class SystemUpdaterProcessStarter : IUpdaterProcessStarter
{
    public Result<Unit> Start(string updaterExecutablePath, string workingDirectory, string planPath)
    {
        if (string.IsNullOrWhiteSpace(updaterExecutablePath) ||
            !Path.IsPathFullyQualified(updaterExecutablePath) ||
            string.IsNullOrWhiteSpace(workingDirectory) ||
            !Path.IsPathFullyQualified(workingDirectory) ||
            string.IsNullOrWhiteSpace(planPath) ||
            !Path.IsPathFullyQualified(planPath) ||
            !File.Exists(updaterExecutablePath) ||
            !Directory.Exists(workingDirectory) ||
            !File.Exists(planPath))
        {
            return Result<Unit>.Failure(Problem("UPDATE_UPDATER_START_INVALID", false));
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = updaterExecutablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--plan");
        startInfo.ArgumentList.Add(planPath);
        try
        {
            using Process? process = Process.Start(startInfo);
            return process is null
                ? Result<Unit>.Failure(Problem("UPDATE_UPDATER_START_FAILED", true))
                : Result.Success();
        }
        catch (Win32Exception)
        {
            return Result<Unit>.Failure(Problem("UPDATE_UPDATER_START_FAILED", true));
        }
        catch (InvalidOperationException)
        {
            return Result<Unit>.Failure(Problem("UPDATE_UPDATER_START_FAILED", true));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<Unit>.Failure(Problem("UPDATE_UPDATER_START_FAILED", true));
        }
        catch (IOException)
        {
            return Result<Unit>.Failure(Problem("UPDATE_UPDATER_START_FAILED", true));
        }
    }

    private static Problem Problem(string code, bool retryable) => new(
        code,
        ProblemStage.Update,
        "problem.update.apply_failed",
        retryable,
        Guid.NewGuid().ToString("N"),
        ["action.update.retry"]);
}
