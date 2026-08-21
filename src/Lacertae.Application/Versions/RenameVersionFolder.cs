using System.Text.Json;
using Lacertae.Application.Storage;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Versions;

public sealed class RenameVersionFolder(
    IVersionOverrideRepository overrideRepository,
    IVersionRenameJournal journal)
{
    public async Task<Result<VersionRenamePlan>> ExecuteAsync(
        string gameRootId,
        string gameRootPath,
        string sourceFolder,
        string targetFolder,
        bool hasActiveBackgroundTask,
        CancellationToken cancellationToken)
    {
        Result<VersionRenamePlan> preflight = await PrepareAsync(
            gameRootId,
            gameRootPath,
            sourceFolder,
            targetFolder,
            hasActiveBackgroundTask,
            cancellationToken);
        if (!preflight.IsSuccess)
        {
            return preflight;
        }

        VersionRenamePlan plan = preflight.Value;
        Result<Unit> prepared = await journal.WriteAsync(
            new VersionRenameJournalEntry(plan, VersionRenameJournalState.Prepared),
            cancellationToken);
        if (!prepared.IsSuccess)
        {
            return Result<VersionRenamePlan>.Failure(prepared.Problem!);
        }

        try
        {
            SecureFileSystem.MoveDirectoryCreate(
                plan.SourcePath,
                plan.TargetPath,
                Path.GetDirectoryName(plan.SourcePath)!);
            // Bind the moved directory before touching any files inside it.
            // If a local process substituted a reparse point during the
            // rename window, this fails closed before JSON/JAR writes occur.
            using IDisposable targetLease = SecureFileSystem.OpenDirectoryLease(
                plan.TargetPath,
                Path.GetDirectoryName(plan.SourcePath)!);
            Result<Unit> moved = await journal.WriteAsync(
                new VersionRenameJournalEntry(plan, VersionRenameJournalState.DirectoryMoved),
                cancellationToken);
            if (!moved.IsSuccess)
            {
                return Result<VersionRenamePlan>.Failure(moved.Problem!);
            }

            RenameFilesAndJson(plan);
            Result<Unit> databaseUpdated = await MigrateOverrideAsync(plan, cancellationToken);
            if (!databaseUpdated.IsSuccess)
            {
                await journal.WriteAsync(
                    new VersionRenameJournalEntry(plan, VersionRenameJournalState.RollbackRequired),
                    CancellationToken.None);
                return Result<VersionRenamePlan>.Failure(databaseUpdated.Problem!);
            }

            Result<Unit> updated = await journal.WriteAsync(
                new VersionRenameJournalEntry(plan, VersionRenameJournalState.DatabaseUpdated),
                cancellationToken);
            if (!updated.IsSuccess)
            {
                return Result<VersionRenamePlan>.Failure(updated.Problem!);
            }

            Result<Unit> completed = await journal.WriteAsync(
                new VersionRenameJournalEntry(plan, VersionRenameJournalState.Completed),
                cancellationToken);
            if (!completed.IsSuccess)
            {
                return Result<VersionRenamePlan>.Failure(completed.Problem!);
            }

            Result<Unit> deleted = await journal.DeleteAsync(cancellationToken);
            return deleted.IsSuccess
                ? Result<VersionRenamePlan>.Success(plan)
                : Result<VersionRenamePlan>.Failure(deleted.Problem!);
        }
        catch (IOException)
        {
            await journal.WriteAsync(
                new VersionRenameJournalEntry(plan, VersionRenameJournalState.RollbackRequired),
                CancellationToken.None);
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_FAILED"));
        }
        catch (JsonException)
        {
            await journal.WriteAsync(
                new VersionRenameJournalEntry(plan, VersionRenameJournalState.RollbackRequired),
                CancellationToken.None);
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_INVALID_JSON"));
        }
    }

    public async Task<Result<Unit>> RecoverAsync(CancellationToken cancellationToken)
    {
        Result<VersionRenameJournalEntry?> read = await journal.ReadAsync(cancellationToken);
        if (!read.IsSuccess)
        {
            return Result.Failure(read.Problem!);
        }

        if (read.Value is null)
        {
            return Result.Success();
        }

        if (read.Value.State is VersionRenameJournalState.Completed)
        {
            return await journal.DeleteAsync(cancellationToken);
        }

        VersionRenamePlan plan = read.Value.Plan;
        bool sourceExists = Directory.Exists(plan.SourcePath);
        bool targetExists = Directory.Exists(plan.TargetPath);
        if ((sourceExists && !SecureFileSystem.IsSafeDirectory(plan.SourcePath, Path.GetDirectoryName(plan.SourcePath)!)) ||
            (targetExists && !SecureFileSystem.IsSafeDirectory(plan.TargetPath, Path.GetDirectoryName(plan.TargetPath)!)))
        {
            return Result.Failure(Problem("VERSION_RENAME_FAILED"));
        }
        if (sourceExists && targetExists)
        {
            return Result.Failure(Problem("VERSION_RENAME_CONFLICT"));
        }

        if (read.Value.State is VersionRenameJournalState.Prepared && !targetExists)
        {
            return await journal.DeleteAsync(cancellationToken);
        }

        if (targetExists && !sourceExists)
        {
            if (read.Value.State is VersionRenameJournalState.RollbackRequired)
            {
                return await RollbackAsync(plan, cancellationToken);
            }

            using IDisposable targetLease = SecureFileSystem.OpenDirectoryLease(
                plan.TargetPath,
                Path.GetDirectoryName(plan.SourcePath)!);
            if (read.Value.State is VersionRenameJournalState.DirectoryMoved)
            {
                try
                {
                    RenameFilesAndJson(plan);
                }
                catch (IOException)
                {
                    return Result.Failure(Problem("VERSION_RENAME_FAILED"));
                }
                catch (JsonException)
                {
                    return Result.Failure(Problem("VERSION_RENAME_INVALID_JSON"));
                }
            }

            Result<Unit> migrated = await MigrateOverrideAsync(plan, cancellationToken);
            if (!migrated.IsSuccess)
            {
                return migrated;
            }

            await journal.WriteAsync(
                new VersionRenameJournalEntry(plan, VersionRenameJournalState.DatabaseUpdated),
                cancellationToken);
            await journal.WriteAsync(
                new VersionRenameJournalEntry(plan, VersionRenameJournalState.Completed),
                cancellationToken);
            return await journal.DeleteAsync(cancellationToken);
        }

        if (!targetExists && sourceExists)
        {
            return Result.Success();
        }

        return Result.Failure(Problem("VERSION_RENAME_CONFLICT"));
    }

    private async Task<Result<Unit>> RollbackAsync(
        VersionRenamePlan plan,
        CancellationToken cancellationToken)
    {
        bool sourceExists = Directory.Exists(plan.SourcePath);
        bool targetExists = Directory.Exists(plan.TargetPath);
        if ((sourceExists && !SecureFileSystem.IsSafeDirectory(plan.SourcePath, Path.GetDirectoryName(plan.SourcePath)!)) ||
            (targetExists && !SecureFileSystem.IsSafeDirectory(plan.TargetPath, Path.GetDirectoryName(plan.TargetPath)!)))
        {
            return Result.Failure(Problem("VERSION_RENAME_ROLLBACK_FAILED"));
        }
        if (sourceExists && targetExists)
        {
            return Result.Failure(Problem("VERSION_RENAME_CONFLICT"));
        }

        if (targetExists)
        {
            try
            {
                using (SecureFileSystem.OpenDirectoryLease(
                           plan.TargetPath,
                           Path.GetDirectoryName(plan.SourcePath)!))
                {
                    RestoreFilesAndJson(plan);
                }

                SecureFileSystem.MoveDirectoryCreate(
                    plan.TargetPath,
                    plan.SourcePath,
                    Path.GetDirectoryName(plan.SourcePath)!);
            }
            catch (IOException)
            {
                return Result.Failure(Problem("VERSION_RENAME_ROLLBACK_FAILED"));
            }
            catch (JsonException)
            {
                return Result.Failure(Problem("VERSION_RENAME_INVALID_JSON"));
            }
        }

        return await journal.DeleteAsync(cancellationToken);
    }

    public static async Task<Result<VersionRenamePlan>> PrepareAsync(
        string gameRootId,
        string gameRootPath,
        string sourceFolder,
        string targetFolder,
        bool hasActiveBackgroundTask,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PreflightAsync(
                gameRootId,
                gameRootPath,
                sourceFolder,
                targetFolder,
                hasActiveBackgroundTask,
                cancellationToken);
        }
        catch (IOException)
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_FAILED"));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_FAILED"));
        }
        catch (NotSupportedException)
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_INVALID_NAME"));
        }
        catch (ArgumentException)
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_INVALID_NAME"));
        }
    }

    private static async Task<Result<VersionRenamePlan>> PreflightAsync(
        string gameRootId,
        string gameRootPath,
        string sourceFolder,
        string targetFolder,
        bool hasActiveBackgroundTask,
        CancellationToken cancellationToken)
    {
        if (hasActiveBackgroundTask)
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_ACTIVE_TASK"));
        }

        if (string.IsNullOrWhiteSpace(gameRootId) || string.IsNullOrWhiteSpace(gameRootPath) ||
            !IsValidFolder(sourceFolder) || !IsValidFolder(targetFolder) ||
            string.Equals(sourceFolder, targetFolder, StringComparison.Ordinal))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_INVALID_NAME"));
        }

        string versionsPath = Path.GetFullPath(Path.Combine(gameRootPath, "versions"));
        string sourcePath = Path.GetFullPath(Path.Combine(versionsPath, sourceFolder));
        string targetPath = Path.GetFullPath(Path.Combine(versionsPath, targetFolder));
        if (!IsUnderRoot(sourcePath, versionsPath) || !IsUnderRoot(targetPath, versionsPath))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_INVALID_NAME"));
        }

        if (!Directory.Exists(versionsPath) ||
            !SecureFileSystem.IsSafeDirectory(versionsPath) ||
            !SecureFileSystem.IsSafeDirectory(sourcePath, versionsPath))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_SOURCE_MISSING"));
        }

        if (Directory.Exists(targetPath) || File.Exists(targetPath))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_TARGET_EXISTS"));
        }

        string sourceJsonPath = Path.Combine(sourcePath, sourceFolder + ".json");
        string sourceJarPath = Path.Combine(sourcePath, sourceFolder + ".jar");
        if (!SecureFileSystem.IsSafeFile(sourceJsonPath, sourcePath))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_JSON_MISSING"));
        }

        string? actualJarPath = SecureFileSystem.IsSafeFile(sourceJarPath, sourcePath) ? sourceJarPath : null;
        if (File.Exists(sourceJarPath) && actualJarPath is null)
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_JAR_BASENAME_MISMATCH"));
        }

        if (Directory.EnumerateFileSystemEntries(sourcePath)
            .Any(path => Directory.Exists(path)
                ? !SecureFileSystem.IsSafeDirectory(path, sourcePath)
                : !SecureFileSystem.IsSafeFile(path, sourcePath)))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_FAILED"));
        }
        if (EnumerateFilesSecure(sourcePath, "*.json")
                .Any(path => !string.Equals(Path.GetFileName(path), sourceFolder + ".json", StringComparison.OrdinalIgnoreCase)))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_JSON_BASENAME_MISMATCH"));
        }

        if (EnumerateFilesSecure(sourcePath, "*.jar")
                .Any(path => !string.Equals(Path.GetFileName(path), sourceFolder + ".jar", StringComparison.OrdinalIgnoreCase)))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_JAR_BASENAME_MISMATCH"));
        }

        string sourceJson = await ReadTextSecureAsync(sourceJsonPath, sourcePath, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(sourceJson);
        if (document.RootElement.TryGetProperty("id", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String &&
            !string.Equals(id.GetString(), sourceFolder, StringComparison.Ordinal))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_JSON_BASENAME_MISMATCH"));
        }

        List<string> referringFolders = [];
        foreach (string siblingJsonPath in EnumerateJsonFiles(versionsPath))
        {
            if (string.Equals(siblingJsonPath, sourceJsonPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using JsonDocument siblingDocument = JsonDocument.Parse(
                    await ReadTextSecureAsync(siblingJsonPath, versionsPath, cancellationToken));
                if (siblingDocument.RootElement.TryGetProperty("inheritsFrom", out JsonElement inheritsFrom) &&
                    inheritsFrom.ValueKind == JsonValueKind.String &&
                    string.Equals(inheritsFrom.GetString(), sourceFolder, StringComparison.Ordinal))
                {
                    referringFolders.Add(Path.GetFileNameWithoutExtension(siblingJsonPath));
                }
            }
            catch (JsonException)
            {
                return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_INVALID_JSON"));
            }
        }

        if (referringFolders.Count > 0)
        {
            return Result<VersionRenamePlan>.Failure(Problem(
                "VERSION_RENAME_REFERENCED",
                new Dictionary<string, string> { ["referringFolders"] = string.Join(",", referringFolders.Order(StringComparer.Ordinal)) }));
        }

        string[] topLevelDirectories = Directory.EnumerateDirectories(sourcePath, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (topLevelDirectories.Any(path => !SecureFileSystem.IsSafeDirectory(path, sourcePath)))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_FAILED"));
        }

        bool containsIsolatedGameData = topLevelDirectories
            .Any(static path => Path.GetFileName(path) is "mods" or "config" or "saves" or "resourcepacks" or "shaderpacks");
        return Result<VersionRenamePlan>.Success(new VersionRenamePlan(
            Guid.NewGuid().ToString("N"),
            gameRootId,
            sourceFolder,
            targetFolder,
            sourcePath,
            targetPath,
            sourceJsonPath,
            Path.Combine(targetPath, targetFolder + ".json"),
            actualJarPath,
            actualJarPath is null ? null : Path.Combine(targetPath, targetFolder + ".jar"),
            containsIsolatedGameData));
    }

    private static void RenameFilesAndJson(VersionRenamePlan plan)
    {
        string targetJsonPath = Path.Combine(plan.TargetPath, plan.SourceFolder + ".json");
        if (SecureFileSystem.IsSafeFile(targetJsonPath, plan.TargetPath))
        {
            SecureFileSystem.MoveCreate(targetJsonPath, plan.TargetJsonPath, plan.TargetPath);
        }
        else if (!SecureFileSystem.IsSafeFile(plan.TargetJsonPath, plan.TargetPath))
        {
            throw new IOException("Version JSON is missing during rename.");
        }

        if (plan.SourceJarPath is not null && plan.TargetJarPath is not null)
        {
            string sourceJarPath = Path.Combine(plan.TargetPath, plan.SourceFolder + ".jar");
            if (SecureFileSystem.IsSafeFile(sourceJarPath, plan.TargetPath))
            {
                SecureFileSystem.MoveCreate(sourceJarPath, plan.TargetJarPath, plan.TargetPath);
            }
            else if (!SecureFileSystem.IsSafeFile(plan.TargetJarPath, plan.TargetPath))
            {
                throw new IOException("Version JAR is missing during rename.");
            }
        }

        string json = ReadTextSecure(plan.TargetJsonPath, plan.TargetPath);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("id", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String &&
            string.Equals(id.GetString(), plan.SourceFolder, StringComparison.Ordinal))
        {
            using JsonDocument updatedDocument = JsonDocument.Parse(json);
            Dictionary<string, JsonElement> properties = updatedDocument.RootElement.EnumerateObject()
                .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);
            properties["id"] = JsonDocument.Parse(JsonSerializer.Serialize(plan.TargetFolder)).RootElement.Clone();
            WriteTextSecure(plan.TargetJsonPath, JsonSerializer.Serialize(properties), plan.TargetPath);
        }
    }

    private static void RestoreFilesAndJson(VersionRenamePlan plan)
    {
        string targetJsonPath = Path.Combine(plan.TargetPath, plan.TargetFolder + ".json");
        string legacyJsonPath = Path.Combine(plan.TargetPath, plan.SourceFolder + ".json");
        string sourceJsonPath = Path.Combine(plan.TargetPath, plan.SourceFolder + ".json");
        if (SecureFileSystem.IsSafeFile(targetJsonPath, plan.TargetPath))
        {
            string json = ReadTextSecure(targetJsonPath, plan.TargetPath);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("id", out JsonElement id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), plan.TargetFolder, StringComparison.Ordinal))
            {
                Dictionary<string, JsonElement> properties = document.RootElement.EnumerateObject()
                    .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);
                properties["id"] = JsonDocument.Parse(JsonSerializer.Serialize(plan.SourceFolder)).RootElement.Clone();
                WriteTextSecure(targetJsonPath, JsonSerializer.Serialize(properties), plan.TargetPath);
            }

            SecureFileSystem.MoveCreate(targetJsonPath, sourceJsonPath, plan.TargetPath);
        }
        else if (!SecureFileSystem.IsSafeFile(legacyJsonPath, plan.TargetPath))
        {
            throw new IOException("Renamed version JSON is missing during rollback.");
        }

        string targetJarPath = Path.Combine(plan.TargetPath, plan.TargetFolder + ".jar");
        if (SecureFileSystem.IsSafeFile(targetJarPath, plan.TargetPath))
        {
            SecureFileSystem.MoveCreate(
                targetJarPath,
                Path.Combine(plan.TargetPath, plan.SourceFolder + ".jar"),
                plan.TargetPath);
        }
    }

    private static IEnumerable<string> EnumerateFilesSecure(string directory, string pattern)
    {
        foreach (string path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            if (!SecureFileSystem.IsSafeFile(path, directory))
            {
                throw new IOException("Version directory contains an unsafe file.");
            }

            yield return path;
        }
    }

    private static IEnumerable<string> EnumerateJsonFiles(string root)
    {
        Stack<string> pending = new([root]);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if (!SecureFileSystem.IsSafeDirectory(current, root))
            {
                throw new IOException("Version directory contains an unsafe directory.");
            }

            foreach (string file in EnumerateFilesSecure(current, "*.json"))
            {
                yield return file;
            }

            foreach (string child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                pending.Push(child);
            }
        }
    }

    private static async Task<string> ReadTextSecureAsync(
        string path,
        string root,
        CancellationToken cancellationToken)
    {
        await using Stream stream = SecureFileSystem.OpenRead(path, root);
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string ReadTextSecure(string path, string root)
    {
        using Stream stream = SecureFileSystem.OpenRead(path, root);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static void WriteTextSecure(string path, string content, string root) =>
        SecureFileSystem.WriteAtomically(
            path,
            System.Text.Encoding.UTF8.GetBytes(content),
            root);

    private async Task<Result<Unit>> MigrateOverrideAsync(VersionRenamePlan plan, CancellationToken cancellationToken)
    {
        return await overrideRepository.RenameAsync(
            plan.GameRootId,
            plan.SourceFolder,
            plan.TargetFolder,
            cancellationToken);
    }

    private static bool IsValidFolder(string folder) =>
        !string.IsNullOrWhiteSpace(folder) &&
        folder is not "." and not ".." &&
        folder.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) < 0;

    private static bool IsUnderRoot(string path, string root)
    {
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static Problem Problem(string code, IReadOnlyDictionary<string, string>? safeContext = null) => new(
        code,
        ProblemStage.Storage,
        "problem.version.rename_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.version.review_rename"],
        safeContext);
}
