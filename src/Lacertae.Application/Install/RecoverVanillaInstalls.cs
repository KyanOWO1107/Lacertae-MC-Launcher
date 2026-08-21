using Lacertae.Application.Storage;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Install;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Install;

public sealed class RecoverVanillaInstalls(
    IInstallJournalRepository journalRepository,
    IGameFileVerifier verifier,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<Result<Unit>> ExecuteAsync(
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journalRepository);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(progress);

        Result<IReadOnlyList<InstallJournalRecord>> records = await journalRepository.GetRecoverableAsync(cancellationToken);
        if (!records.IsSuccess)
        {
            return Result<Unit>.Failure(records.Problem!);
        }

        foreach (InstallJournalRecord record in records.Value.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Result<Unit> recovered = await RecoverOneAsync(record, progress, cancellationToken);
            if (!recovered.IsSuccess)
            {
                return recovered;
            }
        }

        return Result.Success();
    }

    private async Task<Result<Unit>> RecoverOneAsync(
        InstallJournalRecord record,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(record, out string? root))
        {
            return Result<Unit>.Failure(Problem("INSTALL_RECOVERY_CONFLICT"));
        }

        SemaphoreSlim rootLock = InstallRootLocks.Get(root!);
        await rootLock.WaitAsync(cancellationToken);
        try
        {
            return await RecoverLockedAsync(record, root!, progress, cancellationToken);
        }
        finally
        {
            rootLock.Release();
        }
    }

    private async Task<Result<Unit>> RecoverLockedAsync(
        InstallJournalRecord record,
        string root,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        using IDisposable rootLease = SecureFileSystem.OpenDirectoryLease(root);
        Dictionary<string, DownloadArtifact> artifacts = record.Plan.Artifacts.ToDictionary(
            static artifact => artifact.RelativeDestinationPath,
            StringComparer.OrdinalIgnoreCase);
        List<InstallMove> moves = record.Journal.Moves.ToList();
        bool hasStaged = false;
        for (int index = 0; index < moves.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallMove move = moves[index];
            if (!artifacts.TryGetValue(move.FinalRelativePath, out DownloadArtifact? artifact))
            {
                return Result<Unit>.Failure(Problem("INSTALL_RECOVERY_CONFLICT"));
            }

            string finalPath = ResolvePath(root, move.FinalRelativePath);
            string stagedPath = ResolvePath(root, move.StagedRelativePath);
            string? quarantinePath = move.QuarantineRelativePath is null ? null : ResolvePath(root, move.QuarantineRelativePath);
            bool finalExists = File.Exists(finalPath);
            bool stagedExists = File.Exists(stagedPath);
            Result<bool> finalValid = await verifier.VerifyAsync(artifact, finalPath, cancellationToken);
            if (!finalValid.IsSuccess)
            {
                return Result<Unit>.Failure(finalValid.Problem!);
            }

            Result<bool> stagedValid = stagedExists
                ? await verifier.VerifyAsync(artifact, stagedPath, cancellationToken)
                : Result<bool>.Success(false);
            if (!stagedValid.IsSuccess)
            {
                return Result<Unit>.Failure(stagedValid.Problem!);
            }

            if (finalValid.Value)
            {
                if (stagedExists && stagedValid.Value)
                {
                    // Both copies are byte-identical to the frozen artifact. Keep the
                    // committed final copy and remove only this operation's duplicate.
                    TryDeleteFile(stagedPath);
                }

                moves[index] = move with { Applied = true };
                continue;
            }

            if (stagedValid.Value)
            {
                hasStaged = true;
                if (finalExists || Directory.Exists(finalPath))
                {
                    // A non-final file appeared where the plan expected no file. Do not
                    // delete or overwrite it during unattended recovery.
                    return Result<Unit>.Failure(Problem("INSTALL_RECOVERY_CONFLICT"));
                }

                SecureFileSystem.EnsureDirectory(Path.GetDirectoryName(finalPath)!, root);
                try
                {
                    SecureFileSystem.MoveCreate(stagedPath, finalPath, root);
                }
                catch (IOException)
                {
                    return Result<Unit>.Failure(Problem("INSTALL_RECOVERY_CONFLICT"));
                }

                moves[index] = move with { Applied = true };
                InstallJournal updated = record.Journal with
                {
                    State = InstallJournalState.Committing,
                    Moves = moves.ToArray(),
                    UpdatedUtc = timeProvider.GetUtcNow(),
                };
                Result<Unit> saved = await journalRepository.SaveAsync(record.Plan, updated, CancellationToken.None);
                if (!saved.IsSuccess)
                {
                    return saved;
                }
            }
            else if (record.Journal.State == InstallJournalState.RollbackRequired &&
                     move.Applied && quarantinePath is not null && File.Exists(quarantinePath))
            {
                if (finalExists || Directory.Exists(finalPath))
                {
                    return Result<Unit>.Failure(Problem("INSTALL_RECOVERY_CONFLICT"));
                }

                SecureFileSystem.EnsureDirectory(Path.GetDirectoryName(finalPath)!, root);
                try
                {
                    SecureFileSystem.MoveCreate(quarantinePath, finalPath, root);
                }
                catch (IOException)
                {
                    return Result<Unit>.Failure(Problem("INSTALL_RECOVERY_CONFLICT"));
                }

                moves[index] = move with { Applied = false };
            }
        }

        InstallJournal finalJournal = record.Journal with
        {
            State = hasStaged || moves.Any(static move => move.Applied)
                ? InstallJournalState.Completed
                : InstallJournalState.RollbackRequired,
            Moves = moves.ToArray(),
            UpdatedUtc = timeProvider.GetUtcNow(),
        };

        if (finalJournal.State == InstallJournalState.RollbackRequired)
        {
            // Nothing was committed and no unambiguous staged copy remains. Removing
            // only this operation's staging evidence is a safe rollback.
            CleanupOperationPaths(root, record.Plan.OperationId);
            return await journalRepository.RemoveAsync(record.Journal.OperationId, CancellationToken.None);
        }

        foreach (DownloadArtifact artifact in record.Plan.Artifacts)
        {
            Result<bool> valid = await verifier.VerifyAsync(
                artifact,
                ResolvePath(root, artifact.RelativeDestinationPath),
                cancellationToken);
            if (!valid.IsSuccess || !valid.Value)
            {
                return Result<Unit>.Failure(Problem("INSTALL_RECOVERY_CONFLICT"));
            }
        }

        Result<Unit> completed = await journalRepository.SaveAsync(record.Plan, finalJournal, CancellationToken.None);
        if (!completed.IsSuccess)
        {
            return completed;
        }

        CleanupOperationPaths(root, record.Plan.OperationId);
        progress.Report(new OperationProgress("commit", 1, 1, 0, 0));
        return await journalRepository.RemoveAsync(record.Journal.OperationId, CancellationToken.None);
    }

    private static bool TryNormalize(InstallJournalRecord record, out string? root)
    {
        root = null;
        if (record.Plan is null || record.Journal is null ||
            !string.Equals(record.Plan.OperationId, record.Journal.OperationId, StringComparison.Ordinal) ||
            !string.Equals(record.Plan.GameRootId, record.Journal.GameRootId, StringComparison.Ordinal) ||
            !string.Equals(record.Plan.VersionId, record.Journal.VersionId, StringComparison.Ordinal) ||
            !IsSafeSegment(record.Plan.OperationId) || !IsSafeSegment(record.Plan.GameRootId) || !VersionFolderPolicy.IsSafe(record.Plan.VersionId))
        {
            return false;
        }

        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(record.Plan.GameRootPath));
            string normalizedRoot = root;
            if (!record.Plan.Artifacts.All(artifact =>
                    artifact is not null && IsUnderRoot(ResolvePath(normalizedRoot, artifact.RelativeDestinationPath), normalizedRoot)))
            {
                return false;
            }

            HashSet<string> artifactPaths = record.Plan.Artifacts
                .Select(static artifact => artifact.RelativeDestinationPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return record.Journal.Moves.All(move =>
                artifactPaths.Contains(move.FinalRelativePath) &&
                string.Equals(move.StagedRelativePath, $".lacertae/staging/{record.Plan.OperationId}/{move.FinalRelativePath}", StringComparison.OrdinalIgnoreCase) &&
                (move.QuarantineRelativePath is null || string.Equals(move.QuarantineRelativePath, $".lacertae/quarantine/{record.Plan.OperationId}/{move.FinalRelativePath}", StringComparison.OrdinalIgnoreCase)));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string ResolvePath(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(fullPath, fullRoot))
        {
            throw new ArgumentException("Recovery path escapes the game root.", nameof(path));
        }

        return fullPath;
    }

    private static bool IsUnderRoot(string path, string root) =>
        string.Equals(Path.GetFullPath(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeSegment(string value) =>
        value.Length <= 128 && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static void CleanupOperationPaths(string root, string operationId)
    {
        TryDeleteDirectory(ResolvePath(root, $".lacertae/staging/{operationId}"));
        TryDeleteDirectory(ResolvePath(root, $".lacertae/quarantine/{operationId}"));
    }

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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                SecureFileSystem.DeleteFile(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.Installation,
        code == "INSTALL_RECOVERY_CONFLICT"
            ? "problem.install.recovery_conflict"
            : "problem.install.recovery_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.install.review_recovery"]);
}
