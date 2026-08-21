using Lacertae.Application.Downloads;
using Lacertae.Application.Storage;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Install;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Install;

public sealed class ExecuteVanillaInstall
{
    private readonly IArtifactDownloader downloader;
    private readonly IGameFileVerifier verifier;
    private readonly IInstallJournalRepository journalRepository;
    private readonly IInstallEnvironment environment;
    private readonly TimeProvider timeProvider;

    public ExecuteVanillaInstall(
        IArtifactDownloader downloader,
        IGameFileVerifier verifier,
        IInstallJournalRepository journalRepository,
        IInstallEnvironment? environment = null,
        TimeProvider? timeProvider = null)
    {
        this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        this.journalRepository = journalRepository ?? throw new ArgumentNullException(nameof(journalRepository));
        this.environment = environment ?? new SystemInstallEnvironment();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<Unit>> ExecuteAsync(
        VanillaInstallPlan plan,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(progress);
        if (!TryNormalizePlan(plan, out string? root, out Problem? invalidPlan))
        {
            return Result<Unit>.Failure(invalidPlan!);
        }

        SemaphoreSlim rootLock = InstallRootLocks.Get(root!);
        await rootLock.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteLockedAsync(plan, root!, progress, cancellationToken);
        }
        finally
        {
            rootLock.Release();
        }
    }

    private async Task<Result<Unit>> ExecuteLockedAsync(
        VanillaInstallPlan plan,
        string root,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        Result<Unit> preflight = Preflight(plan, root);
        if (!preflight.IsSuccess)
        {
            return preflight;
        }

        using IDisposable rootLease = SecureFileSystem.OpenDirectoryLease(root);
        List<InstallMove> moves = [];
        Dictionary<string, DownloadArtifact> artifactsByPath = plan.Artifacts.ToDictionary(
            static artifact => artifact.RelativeDestinationPath,
            StringComparer.OrdinalIgnoreCase);
        foreach (DownloadArtifact artifact in plan.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string finalPath = ResolvePath(root, artifact.RelativeDestinationPath);
            Result<bool> verified = await verifier.VerifyAsync(artifact, finalPath, cancellationToken);
            if (!verified.IsSuccess)
            {
                return Result<Unit>.Failure(verified.Problem!);
            }

            if (verified.Value)
            {
                continue;
            }

            if (Directory.Exists(finalPath) && !File.Exists(finalPath))
            {
                return Result<Unit>.Failure(Problem("INSTALL_COMMIT_CONFLICT"));
            }

            string stagedRelativePath = StagedRelativePath(plan.OperationId, artifact.RelativeDestinationPath);
            string? quarantineRelativePath = File.Exists(finalPath)
                ? QuarantineRelativePath(plan.OperationId, artifact.RelativeDestinationPath)
                : null;
            moves.Add(new InstallMove(
                stagedRelativePath,
                artifact.RelativeDestinationPath,
                quarantineRelativePath,
                Applied: false));
        }

        InstallJournal journal = new(
            plan.OperationId,
            plan.GameRootId,
            plan.VersionId,
            InstallJournalState.Planned,
            moves,
            timeProvider.GetUtcNow());
        Result<Unit> saved = await journalRepository.SaveAsync(plan, journal, cancellationToken);
        if (!saved.IsSuccess)
        {
            return saved;
        }

        if (moves.Count == 0)
        {
            journal = journal with { State = InstallJournalState.Completed, UpdatedUtc = timeProvider.GetUtcNow() };
            Result<Unit> completed = await journalRepository.SaveAsync(plan, journal, cancellationToken);
            if (!completed.IsSuccess)
            {
                return completed;
            }

            return await CompleteAndRemoveAsync(plan, journal);
        }

        journal = journal with { State = InstallJournalState.Staging, UpdatedUtc = timeProvider.GetUtcNow() };
        saved = await journalRepository.SaveAsync(plan, journal, cancellationToken);
        if (!saved.IsSuccess)
        {
            return saved;
        }

        string stagingRoot = ResolvePath(root, $".lacertae/staging/{plan.OperationId}");
        SecureFileSystem.EnsureDirectory(stagingRoot, root);
        for (int index = 0; index < moves.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallMove move = moves[index];
            DownloadArtifact artifact = artifactsByPath[move.FinalRelativePath];
            Result<DownloadReceipt> downloaded = await downloader.DownloadAsync(
                new DownloadRequest(
                    artifact,
                    stagingRoot,
                    DownloadSourcePreference.Automatic,
                    TemporaryFallbackApproved: false,
                    plan.OperationId),
                progress,
                cancellationToken);
            if (!downloaded.IsSuccess)
            {
                return Result<Unit>.Failure(downloaded.Problem!);
            }

            string expectedStagedPath = ResolvePath(root, move.StagedRelativePath);
            if (!PathsEqual(downloaded.Value.VerifiedFilePath, expectedStagedPath))
            {
                return Result<Unit>.Failure(Problem("INSTALL_DOWNLOAD_PATH_INVALID"));
            }

            Result<bool> stagedVerified = await verifier.VerifyAsync(artifact, expectedStagedPath, cancellationToken);
            if (!stagedVerified.IsSuccess)
            {
                return Result<Unit>.Failure(stagedVerified.Problem!);
            }

            if (!stagedVerified.Value)
            {
                return Result<Unit>.Failure(Problem("INSTALL_STAGED_FILE_INVALID"));
            }

            progress.Report(new OperationProgress(
                "verify",
                index + 1,
                moves.Count,
                artifact.ExpectedSize,
                plan.RequiredDownloadBytes));
        }

        journal = journal with { State = InstallJournalState.Verified, UpdatedUtc = timeProvider.GetUtcNow() };
        saved = await journalRepository.SaveAsync(plan, journal, cancellationToken);
        if (!saved.IsSuccess)
        {
            return saved;
        }

        Result<Unit> commitSpace = CheckFreeSpace(plan, root);
        if (!commitSpace.IsSuccess)
        {
            return commitSpace;
        }

        for (int moveIndex = 0; moveIndex < moves.Count; moveIndex++)
        {
            InstallMove move = moves[moveIndex];
            string finalPath = ResolvePath(root, move.FinalRelativePath);
            string stagedPath = ResolvePath(root, move.StagedRelativePath);
            if (File.Exists(finalPath))
            {
                if (move.QuarantineRelativePath is null)
                {
                    return await MarkRollbackRequiredAsync(plan, journal, "INSTALL_COMMIT_CONFLICT");
                }

                Result<bool> current = await verifier.VerifyAsync(artifactsByPath[move.FinalRelativePath], finalPath, cancellationToken);
                if (!current.IsSuccess)
                {
                    return Result<Unit>.Failure(current.Problem!);
                }

                if (current.Value)
                {
                    return await MarkRollbackRequiredAsync(plan, journal, "INSTALL_COMMIT_CONFLICT");
                }
            }
            else if (Directory.Exists(finalPath))
            {
                return await MarkRollbackRequiredAsync(plan, journal, "INSTALL_COMMIT_CONFLICT");
            }

            if (!File.Exists(stagedPath))
            {
                return await MarkRollbackRequiredAsync(plan, journal, "INSTALL_STAGED_FILE_MISSING");
            }

            journal = journal with { State = InstallJournalState.Committing, UpdatedUtc = timeProvider.GetUtcNow() };
            saved = await journalRepository.SaveAsync(plan, journal, cancellationToken);
            if (!saved.IsSuccess)
            {
                return saved;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (move.QuarantineRelativePath is not null && File.Exists(finalPath))
                {
                    string quarantinePath = ResolvePath(root, move.QuarantineRelativePath);
                    SecureFileSystem.EnsureDirectory(Path.GetDirectoryName(quarantinePath)!, root);
                    if (File.Exists(quarantinePath) || Directory.Exists(quarantinePath))
                    {
                        return await MarkRollbackRequiredAsync(plan, journal, "INSTALL_COMMIT_CONFLICT");
                    }

                    SecureFileSystem.MoveCreate(finalPath, quarantinePath, root);
                }

                SecureFileSystem.EnsureDirectory(Path.GetDirectoryName(finalPath)!, root);
                SecureFileSystem.MoveCreate(stagedPath, finalPath, root);
                moves[moveIndex] = move with { Applied = true };
                journal = journal with { Moves = moves.ToArray(), UpdatedUtc = timeProvider.GetUtcNow() };
                saved = await journalRepository.SaveAsync(plan, journal, CancellationToken.None);
                if (!saved.IsSuccess)
                {
                    return await MarkRollbackRequiredAsync(plan, journal, saved.Problem!.Code);
                }
            }
            catch (OperationCanceledException)
            {
                journal = journal with { State = InstallJournalState.RollbackRequired, UpdatedUtc = timeProvider.GetUtcNow() };
                _ = await journalRepository.SaveAsync(plan, journal, CancellationToken.None);
                throw;
            }
            catch (IOException)
            {
                return await MarkRollbackRequiredAsync(plan, journal, "INSTALL_COMMIT_FAILED");
            }
            catch (UnauthorizedAccessException)
            {
                return await MarkRollbackRequiredAsync(plan, journal, "INSTALL_COMMIT_FAILED");
            }
        }

        journal = journal with { State = InstallJournalState.Completed, UpdatedUtc = timeProvider.GetUtcNow() };
        saved = await journalRepository.SaveAsync(plan, journal, CancellationToken.None);
        if (!saved.IsSuccess)
        {
            return saved;
        }

        return await CompleteAndRemoveAsync(plan, journal);
    }

    private async Task<Result<Unit>> CompleteAndRemoveAsync(VanillaInstallPlan plan, InstallJournal journal)
    {
        string root = Path.GetFullPath(plan.GameRootPath);
        TryDeleteDirectory(ResolvePath(root, $".lacertae/staging/{plan.OperationId}"));
        TryDeleteDirectory(ResolvePath(root, $".lacertae/quarantine/{plan.OperationId}"));
        return await journalRepository.RemoveAsync(journal.OperationId, CancellationToken.None);
    }

    private async Task<Result<Unit>> MarkRollbackRequiredAsync(
        VanillaInstallPlan plan,
        InstallJournal journal,
        string code)
    {
        InstallJournal rollback = journal with
        {
            State = InstallJournalState.RollbackRequired,
            UpdatedUtc = timeProvider.GetUtcNow(),
        };
        _ = await journalRepository.SaveAsync(plan, rollback, CancellationToken.None);
        return Result<Unit>.Failure(Problem(code));
    }

    private Result<Unit> Preflight(VanillaInstallPlan plan, string root)
    {
        if (!environment.DirectoryExists(root))
        {
            return Result<Unit>.Failure(Problem("INSTALL_ROOT_UNAVAILABLE"));
        }

        if (!environment.IsDirectoryWritable(root))
        {
            return Result<Unit>.Failure(Problem("INSTALL_ROOT_UNWRITABLE"));
        }

        return CheckFreeSpace(plan, root);
    }

    private Result<Unit> CheckFreeSpace(VanillaInstallPlan plan, string root)
    {
        try
        {
            if (environment.GetAvailableFreeBytes(root) < plan.RequiredWorkingBytes)
            {
                return Result<Unit>.Failure(Problem("INSTALL_DISK_SPACE_INSUFFICIENT"));
            }
        }
        catch (IOException)
        {
            return Result<Unit>.Failure(Problem("INSTALL_DISK_SPACE_UNKNOWN", retryable: true));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<Unit>.Failure(Problem("INSTALL_DISK_SPACE_UNKNOWN", retryable: true));
        }

        return Result.Success();
    }

    private static bool TryNormalizePlan(VanillaInstallPlan plan, out string? root, out Problem? problem)
    {
        root = null;
        problem = null;
        if (string.IsNullOrWhiteSpace(plan.OperationId) || string.IsNullOrWhiteSpace(plan.GameRootId) ||
            string.IsNullOrWhiteSpace(plan.VersionId) || !IsSafeSegment(plan.OperationId) || !IsSafeSegment(plan.GameRootId) ||
            !IsSafeSegment(plan.VersionId) || plan.Artifacts is null || plan.Artifacts.Any(static artifact => artifact is null))
        {
            problem = Problem("INSTALL_PLAN_INVALID");
            return false;
        }

        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.GameRootPath));
            if (!IsUnderRoot(Path.GetFullPath(plan.VersionDirectory), root))
            {
                problem = Problem("INSTALL_PATH_INVALID");
                return false;
            }

            HashSet<string> destinations = new(StringComparer.OrdinalIgnoreCase);
            foreach (DownloadArtifact artifact in plan.Artifacts)
            {
                if (string.IsNullOrWhiteSpace(artifact.RelativeDestinationPath) || artifact.RelativeDestinationPath.Contains('\0') ||
                    !IsSafeRelativePath(artifact.RelativeDestinationPath) || !destinations.Add(artifact.RelativeDestinationPath))
                {
                    problem = Problem("INSTALL_PATH_INVALID");
                    return false;
                }

                _ = ResolvePath(root, artifact.RelativeDestinationPath);
                if (Directory.Exists(root) && HasReparsePointBetween(ResolvePath(root, artifact.RelativeDestinationPath), root))
                {
                    problem = Problem("INSTALL_PATH_INVALID");
                    return false;
                }
            }

            return true;
        }
        catch (ArgumentException)
        {
            problem = Problem("INSTALL_PATH_INVALID");
            return false;
        }
        catch (NotSupportedException)
        {
            problem = Problem("INSTALL_PATH_INVALID");
            return false;
        }
    }

    private static bool IsSafeRelativePath(string path) =>
        !Path.IsPathRooted(path) && !path.StartsWith('/') && !path.Contains(':') &&
        path.Replace('\\', '/').Split('/').All(static segment => segment is not ("" or "." or ".."));

    private static bool IsSafeSegment(string value) =>
        value.Length <= 128 && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static string ResolvePath(string root, string relativePath)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string target = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(target, normalizedRoot))
        {
            throw new ArgumentException("Path escapes the game root.", nameof(relativePath));
        }

        return target;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePointBetween(string path, string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? current = File.Exists(path) || Directory.Exists(path)
            ? Path.GetFullPath(path)
            : Path.GetDirectoryName(Path.GetFullPath(path));
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            current = string.Equals(parent, current, StringComparison.OrdinalIgnoreCase) ? null : parent;
        }

        return true;
    }

    private static string StagedRelativePath(string operationId, string relativePath) =>
        $".lacertae/staging/{operationId}/{relativePath.Replace('\\', '/')}";

    private static string QuarantineRelativePath(string operationId, string relativePath) =>
        $".lacertae/quarantine/{operationId}/{relativePath.Replace('\\', '/')}";

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                SecureFileSystem.DeleteDirectory(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Problem Problem(string code, bool retryable = false) => new(
        code,
        ProblemStage.Installation,
        code switch
        {
            "INSTALL_ROOT_UNAVAILABLE" => "problem.install.root_unavailable",
            "INSTALL_ROOT_UNWRITABLE" => "problem.install.root_unwritable",
            "INSTALL_DISK_SPACE_INSUFFICIENT" => "problem.install.disk_space_insufficient",
            "INSTALL_DISK_SPACE_UNKNOWN" => "problem.install.disk_space_unknown",
            "INSTALL_COMMIT_CONFLICT" => "problem.install.commit_conflict",
            "INSTALL_STAGED_FILE_INVALID" => "problem.install.staged_file_invalid",
            "INSTALL_STAGED_FILE_MISSING" => "problem.install.staged_file_missing",
            "INSTALL_DOWNLOAD_PATH_INVALID" => "problem.install.download_path_invalid",
            "INSTALL_COMMIT_FAILED" => "problem.install.commit_failed",
            _ => "problem.install.plan_invalid",
        },
        retryable,
        Guid.NewGuid().ToString("N"),
        ["action.install.retry"]);
}
