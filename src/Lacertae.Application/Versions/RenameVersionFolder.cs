using System.Text.Json;
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
        Result<VersionRenamePlan> preflight = await PreflightAsync(
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
            Directory.Move(plan.SourcePath, plan.TargetPath);
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
        if (sourceExists && targetExists)
        {
            return Result.Failure(Problem("VERSION_RENAME_CONFLICT"));
        }

        if (targetExists)
        {
            try
            {
                RestoreFilesAndJson(plan);
                Directory.Move(plan.TargetPath, plan.SourcePath);
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
        return await PreflightAsync(
            gameRootId,
            gameRootPath,
            sourceFolder,
            targetFolder,
            hasActiveBackgroundTask,
            cancellationToken);
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

        if (!Directory.Exists(sourcePath))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_SOURCE_MISSING"));
        }

        if (Directory.Exists(targetPath) || File.Exists(targetPath))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_TARGET_EXISTS"));
        }

        string sourceJsonPath = Path.Combine(sourcePath, sourceFolder + ".json");
        string sourceJarPath = Path.Combine(sourcePath, sourceFolder + ".jar");
        if (!File.Exists(sourceJsonPath))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_JSON_MISSING"));
        }

        string? actualJarPath = File.Exists(sourceJarPath) ? sourceJarPath : null;
        if (Directory.EnumerateFiles(sourcePath, "*.json", SearchOption.TopDirectoryOnly)
                .Any(path => !string.Equals(Path.GetFileName(path), sourceFolder + ".json", StringComparison.OrdinalIgnoreCase)))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_JSON_BASENAME_MISMATCH"));
        }

        if (Directory.EnumerateFiles(sourcePath, "*.jar", SearchOption.TopDirectoryOnly)
                .Any(path => !string.Equals(Path.GetFileName(path), sourceFolder + ".jar", StringComparison.OrdinalIgnoreCase)))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_JAR_BASENAME_MISMATCH"));
        }

        string sourceJson = await File.ReadAllTextAsync(sourceJsonPath, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(sourceJson);
        if (document.RootElement.TryGetProperty("id", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String &&
            !string.Equals(id.GetString(), sourceFolder, StringComparison.Ordinal))
        {
            return Result<VersionRenamePlan>.Failure(Problem("VERSION_RENAME_JSON_BASENAME_MISMATCH"));
        }

        List<string> referringFolders = [];
        foreach (string siblingJsonPath in Directory.EnumerateFiles(versionsPath, "*.json", SearchOption.AllDirectories))
        {
            if (string.Equals(siblingJsonPath, sourceJsonPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using JsonDocument siblingDocument = JsonDocument.Parse(await File.ReadAllTextAsync(siblingJsonPath, cancellationToken));
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

        bool containsIsolatedGameData = Directory.EnumerateDirectories(sourcePath, "*", SearchOption.TopDirectoryOnly)
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
        if (File.Exists(targetJsonPath))
        {
            File.Move(targetJsonPath, plan.TargetJsonPath);
        }
        else if (!File.Exists(plan.TargetJsonPath))
        {
            throw new IOException("Version JSON is missing during rename.");
        }

        if (plan.SourceJarPath is not null && plan.TargetJarPath is not null)
        {
            string sourceJarPath = Path.Combine(plan.TargetPath, plan.SourceFolder + ".jar");
            if (File.Exists(sourceJarPath))
            {
                File.Move(sourceJarPath, plan.TargetJarPath);
            }
            else if (!File.Exists(plan.TargetJarPath))
            {
                throw new IOException("Version JAR is missing during rename.");
            }
        }

        string json = File.ReadAllText(plan.TargetJsonPath);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("id", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String &&
            string.Equals(id.GetString(), plan.SourceFolder, StringComparison.Ordinal))
        {
            using JsonDocument updatedDocument = JsonDocument.Parse(json);
            Dictionary<string, JsonElement> properties = updatedDocument.RootElement.EnumerateObject()
                .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);
            properties["id"] = JsonDocument.Parse(JsonSerializer.Serialize(plan.TargetFolder)).RootElement.Clone();
            File.WriteAllText(plan.TargetJsonPath, JsonSerializer.Serialize(properties));
        }
    }

    private static void RestoreFilesAndJson(VersionRenamePlan plan)
    {
        string targetJsonPath = Path.Combine(plan.TargetPath, plan.TargetFolder + ".json");
        string legacyJsonPath = Path.Combine(plan.TargetPath, plan.SourceFolder + ".json");
        string sourceJsonPath = Path.Combine(plan.TargetPath, plan.SourceFolder + ".json");
        if (File.Exists(targetJsonPath))
        {
            string json = File.ReadAllText(targetJsonPath);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("id", out JsonElement id) &&
                id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), plan.TargetFolder, StringComparison.Ordinal))
            {
                Dictionary<string, JsonElement> properties = document.RootElement.EnumerateObject()
                    .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);
                properties["id"] = JsonDocument.Parse(JsonSerializer.Serialize(plan.SourceFolder)).RootElement.Clone();
                File.WriteAllText(targetJsonPath, JsonSerializer.Serialize(properties));
            }

            File.Move(targetJsonPath, sourceJsonPath);
        }
        else if (!File.Exists(legacyJsonPath))
        {
            throw new IOException("Renamed version JSON is missing during rollback.");
        }

        string targetJarPath = Path.Combine(plan.TargetPath, plan.TargetFolder + ".jar");
        if (File.Exists(targetJarPath))
        {
            File.Move(targetJarPath, Path.Combine(plan.TargetPath, plan.SourceFolder + ".jar"));
        }
    }

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
