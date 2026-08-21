using System.Text.Json;
using System.Text.Json.Serialization;
using Lacertae.Application.Storage;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Updates;

namespace Lacertae.Application.Updates;

public sealed record LaunchStagedUpdaterRequest(
    string UpdaterExecutablePath,
    string UpdatesPath,
    string InstallDirectory,
    string StagingDirectory,
    string BackupDirectory,
    string NewExecutableRelativePath,
    string HealthNonce,
    TimeSpan HealthTimeout,
    IReadOnlyList<string> OldManifestFiles,
    IReadOnlyList<string> NewManifestFiles,
    bool Confirmed,
    bool GameRunning,
    bool InstallRunning,
    string CorrelationId);

public interface IUpdaterProcessStarter
{
    Result<Unit> Start(string updaterExecutablePath, string workingDirectory, string planPath);
}

/// <summary>
/// Converts a confirmed staged update into a strict plan and starts the
/// standalone updater. It never mutates installed files itself.
/// </summary>
public sealed class LaunchStagedUpdater
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly IUpdaterProcessStarter processStarter;

    public LaunchStagedUpdater(IUpdaterProcessStarter processStarter)
    {
        this.processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
    }

    public async Task<Result<string>> ExecuteAsync(
        LaunchStagedUpdaterRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Confirmed)
        {
            return Failure<string>("UPDATE_CONFIRMATION_REQUIRED", request.CorrelationId);
        }

        if (request.GameRunning || request.InstallRunning)
        {
            return Failure<string>("UPDATE_ACTIVE_OPERATION", request.CorrelationId);
        }

        string? invalid = ValidateRequest(request);
        if (invalid is not null)
        {
            return Failure<string>(invalid, request.CorrelationId);
        }

        try
        {
            string updatesPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.UpdatesPath));
            string planPath = Path.Combine(updatesPath, "apply-plan.json");
            string healthPath = ConfirmUpdateHealth.GetHealthFilePath(updatesPath, request.HealthNonce);
            SecureFileSystem.EnsureDirectory(updatesPath);
            SecureFileSystem.EnsureDirectory(Path.GetDirectoryName(healthPath)!, updatesPath);
            UpdateApplyPlan plan = new(
                Environment.ProcessId,
                Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Launcher path is unavailable.")),
                Path.GetFullPath(request.InstallDirectory),
                Path.GetFullPath(request.StagingDirectory),
                Path.GetFullPath(request.BackupDirectory),
                request.NewExecutableRelativePath,
                healthPath,
                request.HealthNonce,
                request.HealthTimeout,
                request.OldManifestFiles.ToArray(),
                request.NewManifestFiles.ToArray());
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions);
            await SecureFileSystem.WriteAtomicallyAsync(planPath, bytes, cancellationToken, updatesPath);
            Result<Unit> started = processStarter.Start(
                Path.GetFullPath(request.UpdaterExecutablePath),
                Path.GetDirectoryName(Path.GetFullPath(request.UpdaterExecutablePath))!,
                planPath);
            if (!started.IsSuccess)
            {
                return Failure<string>(started.Problem?.Code ?? "UPDATE_UPDATER_START_FAILED", request.CorrelationId);
            }

            return Result<string>.Success(planPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return Failure<string>("UPDATE_PLAN_WRITE_FAILED", request.CorrelationId);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<string>("UPDATE_PLAN_WRITE_FAILED", request.CorrelationId);
        }
        catch (ArgumentException)
        {
            return Failure<string>("UPDATE_PLAN_INVALID", request.CorrelationId);
        }
        catch (InvalidOperationException)
        {
            return Failure<string>("UPDATE_PLAN_INVALID", request.CorrelationId);
        }
        catch (NotSupportedException)
        {
            return Failure<string>("UPDATE_PLAN_INVALID", request.CorrelationId);
        }
    }

    private static string? ValidateRequest(LaunchStagedUpdaterRequest request)
    {
        if (!IsAbsolutePath(request.UpdaterExecutablePath) ||
            !IsAbsolutePath(request.UpdatesPath) ||
            !IsAbsolutePath(request.InstallDirectory) ||
            !IsAbsolutePath(request.StagingDirectory) ||
            !IsAbsolutePath(request.BackupDirectory) ||
            request.HealthTimeout <= TimeSpan.Zero || request.HealthTimeout > TimeSpan.FromMinutes(5) ||
            !IsNonce(request.HealthNonce) ||
            !IsSafeRelativePath(request.NewExecutableRelativePath) ||
            request.NewManifestFiles is null || request.OldManifestFiles is null ||
            request.NewManifestFiles.Count == 0 ||
            !request.NewManifestFiles.Contains(request.NewExecutableRelativePath, StringComparer.Ordinal) ||
            request.NewManifestFiles.Any(path => !IsSafeRelativePath(path)) ||
            request.OldManifestFiles.Any(path => !IsSafeRelativePath(path)) ||
            request.NewManifestFiles.Distinct(StringComparer.Ordinal).Count() != request.NewManifestFiles.Count ||
            request.OldManifestFiles.Distinct(StringComparer.Ordinal).Count() != request.OldManifestFiles.Count)
        {
            return "UPDATE_PLAN_INVALID";
        }

        if (!SecureFileSystem.IsSafeFile(request.UpdaterExecutablePath, request.InstallDirectory) ||
            !SecureFileSystem.IsSafeDirectory(request.InstallDirectory) ||
            !SecureFileSystem.IsSafeDirectory(request.StagingDirectory, request.UpdatesPath) ||
            !IsUnderRoot(request.BackupDirectory, request.UpdatesPath))
        {
            return "UPDATE_PLAN_ROOT_INVALID";
        }

        if (!IsUnderRoot(request.UpdaterExecutablePath, request.InstallDirectory))
        {
            return "UPDATE_UPDATER_PATH_INVALID";
        }

        return null;
    }

    private static bool IsAbsolutePath(string value) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) &&
        string.Equals(Path.GetFullPath(value), value, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsUnderRoot(string path, string root)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IsNonce(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 16 and <= 128 &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsSafeRelativePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains('\0') &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".." &&
            !segment.EndsWith('.') && !segment.EndsWith(' ') &&
            !segment.Any(character => char.IsControl(character) || character is '<' or '>' or ':' or '"' or '|' or '?' or '*'));

    private static Result<T> Failure<T>(string code, string correlationId) => Result<T>.Failure(new Problem(
        code,
        ProblemStage.Update,
        "problem.update.apply_failed",
        code is "UPDATE_PLAN_WRITE_FAILED" or "UPDATE_UPDATER_START_FAILED",
        string.IsNullOrWhiteSpace(correlationId) ? "update-launch" : correlationId,
        ["action.update.retry"]));
}
