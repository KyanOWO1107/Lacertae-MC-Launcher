using System.ComponentModel;
using System.Diagnostics;
using Lacertae.Application.Storage;
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
            !SecureFileSystem.IsSafeDirectory(workingDirectory) ||
            !SecureFileSystem.IsSafeFile(updaterExecutablePath, workingDirectory) ||
            !SecureFileSystem.IsSafeFile(planPath, Path.GetDirectoryName(planPath)!))
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
            // Keep the executable and both parent chains bound to the objects
            // that were validated until CreateProcess has consumed the paths.
            using IDisposable updaterLease = SecureFileSystem.OpenDirectoryLease(workingDirectory);
            using Stream executableLease = SecureFileSystem.OpenReadExclusive(updaterExecutablePath, workingDirectory);
            using IDisposable planLease = SecureFileSystem.OpenDirectoryLease(Path.GetDirectoryName(planPath)!);
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
