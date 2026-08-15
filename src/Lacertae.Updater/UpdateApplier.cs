using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lacertae.Domain.Updates;

namespace Lacertae.Updater;

public sealed record UpdateApplyResult(
    bool Succeeded,
    bool RolledBack,
    string? FailureCode,
    string JournalPath)
{
    public static UpdateApplyResult Success(string journalPath) => new(true, false, null, journalPath);

    public static UpdateApplyResult Failure(string code, string journalPath, bool rolledBack) =>
        new(false, rolledBack, code, journalPath);
}

public interface IUpdateProcessLauncher
{
    IUpdateProcess Start(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments);
}

public interface IUpdateProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    void Kill();
}

public sealed class SystemUpdateProcessLauncher : IUpdateProcessLauncher
{
    public IUpdateProcess Start(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            if (argument is null)
            {
                throw new ArgumentException("Update process arguments cannot contain null values.", nameof(arguments));
            }

            startInfo.ArgumentList.Add(argument);
        }

        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("The updated launcher could not be started.");
        return new SystemUpdateProcess(process);
    }

    private sealed class SystemUpdateProcess(Process process) : IUpdateProcess
    {
        public int Id => process.Id;

        public bool HasExited => process.HasExited;

        public void Kill()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        public void Dispose() => process.Dispose();
    }
}

/// <summary>
/// Applies only the explicit files in a verified staged package. It creates a
/// durable backup before touching the installed files, launches the new build
/// with a nonce, and restores the old build if startup does not confirm health.
/// </summary>
public sealed class UpdateApplier
{
    private const int MaximumManifestEntries = 20_000;
    private const long MaximumManifestBytes = 10L * 1024 * 1024;
    private const long MaximumHealthBytes = 4096;
    private readonly IUpdateParentWaiter processWaiter;
    private readonly IUpdateProcessLauncher processLauncher;

    public UpdateApplier(
        IUpdateParentWaiter? processWaiter = null,
        IUpdateProcessLauncher? processLauncher = null)
    {
        this.processWaiter = processWaiter ?? new ProcessWaiter();
        this.processLauncher = processLauncher ?? new SystemUpdateProcessLauncher();
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        UpdateApplyPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string journalPath = GetJournalPath(plan);
        string? validationFailure = ValidatePlan(plan);
        if (validationFailure is not null)
        {
            return UpdateApplyResult.Failure(validationFailure, journalPath, rolledBack: false);
        }

        ProcessWaitResult parent = await processWaiter.WaitForExitAsync(
            plan.ParentProcessId,
            plan.ParentExecutablePath,
            TimeSpan.FromSeconds(60),
            cancellationToken);
        if (!parent.Exited)
        {
            return UpdateApplyResult.Failure(parent.FailureCode ?? "UPDATE_PARENT_UNAVAILABLE", journalPath, rolledBack: false);
        }

        UpdateApplyJournal? journal = null;
        IUpdateProcess? child = null;
        bool rollbackNeeded = false;
        try
        {
            EnsureNoReparsePath(plan.InstallDirectory, mustExist: true);
            EnsureNoReparsePath(plan.StagingDirectory, mustExist: true);
            EnsureNoReparsePath(plan.BackupDirectory, mustExist: false);
            EnsureNoReparsePath(Path.GetDirectoryName(plan.HealthFilePath)!, mustExist: false);
            Directory.CreateDirectory(plan.BackupDirectory);

            journal = new UpdateApplyJournal(journalPath);
            journal.SetState("applying");
            PackageManifest newManifest = ReadPackageManifest(plan.StagingDirectory);
            PackageManifest? oldManifest = ReadOptionalPackageManifest(plan.InstallDirectory);
            ValidateManifestFileList(plan.NewManifestFiles, newManifest, requireManifestFile: true);
            if (oldManifest is not null)
            {
                ValidateManifestFileList(plan.OldManifestFiles, oldManifest, requireManifestFile: true);
            }
            else if (plan.OldManifestFiles.Count != 0)
            {
                throw new UpdateApplyException("UPDATE_OLD_MANIFEST_MISSING");
            }

            IReadOnlyDictionary<string, PackageFile> oldFiles = oldManifest is null
                ? new Dictionary<string, PackageFile>(StringComparer.Ordinal)
                : AddPackageManifestHash(plan.InstallDirectory, oldManifest.Files);
            IReadOnlyDictionary<string, PackageFile> newFiles = AddPackageManifestHash(plan.StagingDirectory, newManifest.Files);
            IReadOnlyList<string> oldInstalledFiles = AddPackageManifest(plan.OldManifestFiles, oldManifest is not null);
            IReadOnlyList<string> newInstalledFiles = AddPackageManifest(plan.NewManifestFiles, includeManifest: true);

            DeleteHealthFile(plan.HealthFilePath);
            await BackupFilesAsync(plan, oldInstalledFiles, oldFiles, journal, cancellationToken);
            await RemoveOldFilesAsync(plan, oldInstalledFiles, journal, cancellationToken);
            await InstallNewFilesAsync(plan, newInstalledFiles, newFiles, journal, cancellationToken);

            string executablePath = ResolveUnderRoot(plan.InstallDirectory, plan.NewExecutableRelativePath);
            if (!File.Exists(executablePath))
            {
                throw new UpdateApplyException("UPDATE_EXECUTABLE_MISSING");
            }

            child = processLauncher.Start(
                executablePath,
                plan.InstallDirectory,
                ["--update-health", plan.HealthNonce]);
            rollbackNeeded = true;
            bool healthy = await WaitForHealthAsync(plan, child, cancellationToken);
            if (!healthy)
            {
                throw new UpdateApplyException("UPDATE_HEALTH_FAILED");
            }

            journal.SetState("succeeded");
            TryDeleteStaging(plan.StagingDirectory);
            TryDeleteHealthFile(plan.HealthFilePath);
            return UpdateApplyResult.Success(journal.Path);
        }
        catch (OperationCanceledException)
        {
            if (journal is not null)
            {
                await RollbackAsync(plan, journal, child, CancellationToken.None);
            }

            throw;
        }
        catch (UpdateApplyException exception)
        {
            bool rolledBack = journal is not null && await RollbackAsync(plan, journal, child, CancellationToken.None);
            return UpdateApplyResult.Failure(exception.Code, journal?.Path ?? journalPath, rolledBack);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            bool rolledBack = journal is not null && await RollbackAsync(plan, journal, child, CancellationToken.None);
            return UpdateApplyResult.Failure(
                exception switch
                {
                    JsonException or InvalidDataException => "UPDATE_PACKAGE_MANIFEST_INVALID",
                    UnauthorizedAccessException => "UPDATE_ACCESS_DENIED",
                    _ => "UPDATE_APPLY_FAILED",
                },
                journal?.Path ?? journalPath,
                rolledBack);
        }
        finally
        {
            child?.Dispose();
            _ = rollbackNeeded;
        }
    }

    private static async Task BackupFilesAsync(
        UpdateApplyPlan plan,
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, PackageFile> manifest,
        UpdateApplyJournal journal,
        CancellationToken cancellationToken)
    {
        foreach (string relativePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = ResolveUnderRoot(plan.InstallDirectory, relativePath);
            if (!File.Exists(source))
            {
                if (relativePath == "package-manifest.json")
                {
                    continue;
                }

                throw new UpdateApplyException("UPDATE_OLD_FILE_MISSING");
            }

            if (!manifest.TryGetValue(relativePath, out PackageFile? expected) ||
                !HasExpectedFile(source, expected))
            {
                throw new UpdateApplyException("UPDATE_OLD_FILE_HASH_MISMATCH");
            }

            string destination = ResolveUnderRoot(plan.BackupDirectory, relativePath);
            EnsureParentDirectory(destination, plan.BackupDirectory);
            int entry = journal.AddPending(new UpdateJournalEntry(
                UpdateJournalOperationKind.Backup,
                relativePath,
                source,
                destination,
                expected.Sha256,
                expected.Sha256,
                Applied: false));
            CopyDurable(source, destination, expected);
            journal.MarkApplied(entry);
        }
    }

    private static async Task RemoveOldFilesAsync(
        UpdateApplyPlan plan,
        IReadOnlyList<string> files,
        UpdateApplyJournal journal,
        CancellationToken cancellationToken)
    {
        foreach (string relativePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = ResolveUnderRoot(plan.InstallDirectory, relativePath);
            if (!File.Exists(source))
            {
                continue;
            }

            int entry = journal.AddPending(new UpdateJournalEntry(
                UpdateJournalOperationKind.DeleteObsolete,
                relativePath,
                source,
                null,
                UpdateApplyJournal.Sha256(source),
                null,
                Applied: false));
            File.Delete(source);
            journal.MarkApplied(entry);
        }

        await Task.CompletedTask;
    }

    private static async Task InstallNewFilesAsync(
        UpdateApplyPlan plan,
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, PackageFile> manifest,
        UpdateApplyJournal journal,
        CancellationToken cancellationToken)
    {
        foreach (string relativePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = ResolveUnderRoot(plan.StagingDirectory, relativePath);
            string destination = ResolveUnderRoot(plan.InstallDirectory, relativePath);
            if (!manifest.TryGetValue(relativePath, out PackageFile? expected) ||
                !File.Exists(source) || !HasExpectedFile(source, expected))
            {
                throw new UpdateApplyException("UPDATE_NEW_FILE_HASH_MISMATCH");
            }

            if (File.Exists(destination))
            {
                throw new UpdateApplyException("UPDATE_INSTALL_DESTINATION_NOT_EMPTY");
            }

            EnsureParentDirectory(destination, plan.InstallDirectory);
            int entry = journal.AddPending(new UpdateJournalEntry(
                UpdateJournalOperationKind.InstallNew,
                relativePath,
                source,
                destination,
                null,
                expected.Sha256,
                Applied: false));
            MoveDurable(source, destination, expected);
            journal.MarkApplied(entry);
        }

        await Task.CompletedTask;
    }

    private static async Task<bool> WaitForHealthAsync(
        UpdateApplyPlan plan,
        IUpdateProcess child,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(plan.HealthTimeout);
        while (true)
        {
            if (File.Exists(plan.HealthFilePath))
            {
                FileInfo info = new(plan.HealthFilePath);
                if (info.Length > MaximumHealthBytes)
                {
                    return false;
                }

                string contents = await File.ReadAllTextAsync(plan.HealthFilePath, timeoutSource.Token);
                if (IsValidHealth(contents, plan.HealthNonce, child.Id))
                {
                    return true;
                }

                return false;
            }

            if (child.HasExited)
            {
                return false;
            }

            try
            {
                await Task.Delay(100, timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private async Task<bool> RollbackAsync(
        UpdateApplyPlan plan,
        UpdateApplyJournal journal,
        IUpdateProcess? child,
        CancellationToken cancellationToken)
    {
        try
        {
            if (child is not null && !child.HasExited)
            {
                child.Kill();
            }

            journal.SetState("rolling-back");
            foreach (UpdateJournalEntry entry in journal.Entries.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.Applied)
                {
                    continue;
                }

                switch (entry.Kind)
                {
                    case UpdateJournalOperationKind.InstallNew:
                        if (entry.DestinationPath is not null && File.Exists(entry.DestinationPath))
                        {
                            File.Delete(entry.DestinationPath);
                        }

                        break;
                    case UpdateJournalOperationKind.DeleteObsolete:
                        // The backup copy is restored by the Backup entry below.
                        break;
                    case UpdateJournalOperationKind.Backup:
                        if (entry.SourcePath is null || entry.DestinationPath is null || !File.Exists(entry.DestinationPath))
                        {
                            return false;
                        }

                        EnsureParentDirectory(entry.SourcePath, plan.InstallDirectory);
                        CopyDurable(entry.DestinationPath, entry.SourcePath, new PackageFile(
                            new FileInfo(entry.DestinationPath).Length,
                            entry.OldSha256 ?? UpdateApplyJournal.Sha256(entry.DestinationPath)));
                        break;
                }
            }

            journal.SetState("rolled-back");
            string oldExecutable = ResolveUnderRoot(plan.InstallDirectory, plan.NewExecutableRelativePath);
            if (File.Exists(oldExecutable))
            {
                using IUpdateProcess rollbackProcess = processLauncher.Start(
                    oldExecutable,
                    plan.InstallDirectory,
                    ["--update-rollback"]);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            try
            {
                journal.SetState("rollback-failed");
            }
            catch
            {
                // The original failure is more useful than a second journal error.
            }

            return false;
        }
    }

    private static PackageManifest ReadPackageManifest(string stagingDirectory)
    {
        string path = ResolveUnderRoot(stagingDirectory, "package-manifest.json");
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumManifestBytes)
        {
            throw new UpdateApplyException("UPDATE_PACKAGE_MANIFEST_INVALID");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return ParsePackageManifest(document.RootElement);
    }

    private static PackageManifest? ReadOptionalPackageManifest(string installDirectory)
    {
        string path = ResolveUnderRoot(installDirectory, "package-manifest.json");
        if (!File.Exists(path))
        {
            return null;
        }

        if (new FileInfo(path).Length > MaximumManifestBytes)
        {
            throw new UpdateApplyException("UPDATE_OLD_MANIFEST_INVALID");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return ParsePackageManifest(document.RootElement);
    }

    private static PackageManifest ParsePackageManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Select(static property => property.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
        {
            throw new UpdateApplyException("UPDATE_PACKAGE_MANIFEST_INVALID");
        }

        HashSet<string> properties = ["schemaVersion", "files"];
        if (root.EnumerateObject().Any(property => !properties.Contains(property.Name)) ||
            !root.TryGetProperty("schemaVersion", out JsonElement schema) || schema.GetInt32() != 1 ||
            !root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array ||
            files.GetArrayLength() > MaximumManifestEntries)
        {
            throw new UpdateApplyException("UPDATE_PACKAGE_MANIFEST_INVALID");
        }

        Dictionary<string, PackageFile> parsed = new(StringComparer.Ordinal);
        foreach (JsonElement file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object ||
                file.EnumerateObject().Select(static property => property.Name).Distinct(StringComparer.Ordinal).Count() != file.EnumerateObject().Count())
            {
                throw new UpdateApplyException("UPDATE_PACKAGE_MANIFEST_INVALID");
            }

            HashSet<string> fileProperties = ["path", "size", "sha256"];
            if (file.EnumerateObject().Any(property => !fileProperties.Contains(property.Name)) ||
                !file.TryGetProperty("path", out JsonElement pathElement) || pathElement.ValueKind != JsonValueKind.String ||
                !file.TryGetProperty("size", out JsonElement sizeElement) || !sizeElement.TryGetInt64(out long size) || size < 0 ||
                !file.TryGetProperty("sha256", out JsonElement hashElement) || hashElement.ValueKind != JsonValueKind.String)
            {
                throw new UpdateApplyException("UPDATE_PACKAGE_MANIFEST_INVALID");
            }

            string? path = pathElement.GetString();
            string? hash = hashElement.GetString();
            if (!IsSafeRelativePath(path) || hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit) ||
                !parsed.TryAdd(path!, new PackageFile(size, hash.ToLowerInvariant())))
            {
                throw new UpdateApplyException("UPDATE_PACKAGE_MANIFEST_INVALID");
            }
        }

        return new PackageManifest(parsed);
    }

    private static void ValidateManifestFileList(
        IReadOnlyList<string> files,
        PackageManifest manifest,
        bool requireManifestFile)
    {
        if (files is null || files.Count > MaximumManifestEntries || files.Any(path => !IsSafeRelativePath(path)) ||
            files.Distinct(StringComparer.Ordinal).Count() != files.Count ||
            files.Any(path => !manifest.Files.ContainsKey(path)))
        {
            throw new UpdateApplyException("UPDATE_PLAN_FILE_LIST_INVALID");
        }

        if (requireManifestFile && files.Count != manifest.Files.Count)
        {
            throw new UpdateApplyException("UPDATE_PLAN_FILE_LIST_INVALID");
        }
    }

    private static IReadOnlyList<string> AddPackageManifest(IReadOnlyList<string> files, bool includeManifest)
    {
        if (!includeManifest || files.Contains("package-manifest.json", StringComparer.Ordinal))
        {
            return files;
        }

        return files.Concat(["package-manifest.json"]).ToArray();
    }

    private static Dictionary<string, PackageFile> AddPackageManifestHash(
        string root,
        IReadOnlyDictionary<string, PackageFile> files)
    {
        Dictionary<string, PackageFile> result = new(files, StringComparer.Ordinal);
        string manifestPath = ResolveUnderRoot(root, "package-manifest.json");
        if (File.Exists(manifestPath))
        {
            FileInfo info = new(manifestPath);
            result["package-manifest.json"] = new PackageFile(info.Length, UpdateApplyJournal.Sha256(manifestPath));
        }

        return result;
    }

    private static bool IsValidHealth(string contents, string nonce, int processId)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(contents);
            JsonElement root = document.RootElement;
            HashSet<string> properties = ["schemaVersion", "nonce", "processId", "confirmedUtc"];
            return root.ValueKind == JsonValueKind.Object &&
                root.EnumerateObject().All(property => properties.Contains(property.Name)) &&
                root.TryGetProperty("schemaVersion", out JsonElement schema) && schema.GetInt32() == 1 &&
                root.TryGetProperty("nonce", out JsonElement nonceValue) && nonceValue.ValueKind == JsonValueKind.String &&
                string.Equals(nonceValue.GetString(), nonce, StringComparison.Ordinal) &&
                root.TryGetProperty("processId", out JsonElement process) && process.GetInt32() == processId;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? ValidatePlan(UpdateApplyPlan plan)
    {
        if (plan.ParentProcessId <= 0 ||
            !IsAbsoluteNormalizedPath(plan.ParentExecutablePath) ||
            !IsAbsoluteNormalizedPath(plan.InstallDirectory) ||
            !IsAbsoluteNormalizedPath(plan.StagingDirectory) ||
            !IsAbsoluteNormalizedPath(plan.BackupDirectory) ||
            !IsAbsoluteNormalizedPath(plan.HealthFilePath) ||
            plan.HealthTimeout <= TimeSpan.Zero || plan.HealthTimeout > TimeSpan.FromMinutes(5) ||
            !IsNonce(plan.HealthNonce) ||
            !IsSafeRelativePath(plan.NewExecutableRelativePath) ||
            plan.OldManifestFiles is null || plan.NewManifestFiles is null ||
            plan.NewManifestFiles.Count == 0 ||
            !plan.NewManifestFiles.Contains(plan.NewExecutableRelativePath, StringComparer.Ordinal))
        {
            return "UPDATE_PLAN_INVALID";
        }

        if (!Directory.Exists(plan.InstallDirectory) || !Directory.Exists(plan.StagingDirectory) ||
            IsSamePath(plan.InstallDirectory, plan.StagingDirectory) ||
            IsSamePath(plan.InstallDirectory, plan.BackupDirectory) ||
            IsPathInside(plan.InstallDirectory, plan.StagingDirectory) ||
            IsPathInside(plan.InstallDirectory, plan.BackupDirectory) ||
            IsPathInside(plan.StagingDirectory, plan.BackupDirectory) ||
            IsPathInside(plan.BackupDirectory, plan.StagingDirectory) ||
            IsPathInside(plan.InstallDirectory, Path.GetDirectoryName(plan.HealthFilePath)!) ||
            IsPathInside(plan.StagingDirectory, Path.GetDirectoryName(plan.HealthFilePath)!) ||
            IsPathInside(plan.BackupDirectory, Path.GetDirectoryName(plan.HealthFilePath)!))
        {
            return "UPDATE_PLAN_ROOT_INVALID";
        }

        try
        {
            EnsureNoReparsePath(plan.InstallDirectory, mustExist: true);
            EnsureNoReparsePath(plan.StagingDirectory, mustExist: true);
            EnsureNoReparsePath(Path.GetDirectoryName(plan.ParentExecutablePath)!, mustExist: true);
            EnsureNoReparsePath(Path.GetDirectoryName(plan.HealthFilePath)!, mustExist: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "UPDATE_PLAN_REPARSE_POINT";
        }

        return null;
    }

    private static string GetJournalPath(UpdateApplyPlan plan) =>
        Path.Combine(Path.GetFullPath(plan.BackupDirectory), "update-apply.journal.json");

    private static bool IsAbsoluteNormalizedPath(string value) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) &&
        string.Equals(Path.GetFullPath(value), value, GetPathComparison());

    private static bool IsNonce(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 16 and <= 128 &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains('\0'))
        {
            return false;
        }

        string[] segments = path.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not "." and not ".." &&
            !segment.EndsWith('.') && !segment.EndsWith(' ') &&
            !segment.Any(character => char.IsControl(character) || character is '<' or '>' or ':' or '"' or '|' or '?' or '*') &&
            !IsReservedWindowsName(segment));
    }

    private static bool IsReservedWindowsName(string segment)
    {
        string stem = segment.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                char.IsAsciiDigit(stem[3]));
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, GetPathComparison()) ||
            !IsSafeRelativePath(relativePath))
        {
            throw new UpdateApplyException("UPDATE_PATH_ESCAPE");
        }

        return fullPath;
    }

    private static bool IsPathInside(string parent, string child) =>
        Path.GetFullPath(child).StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar,
            GetPathComparison());

    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            GetPathComparison());

    private static void EnsureNoReparsePath(string path, bool mustExist)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (mustExist && !Directory.Exists(full) && !File.Exists(full))
        {
            throw new IOException("Required update path does not exist.");
        }

        string existingPath = full;
        while (!Directory.Exists(existingPath) && !File.Exists(existingPath))
        {
            string? parent = Path.GetDirectoryName(existingPath);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, existingPath, GetPathComparison()))
            {
                throw new IOException("Update path has no existing ancestor.");
            }

            existingPath = parent;
        }

        FileSystemInfo? current = Directory.Exists(existingPath)
            ? new DirectoryInfo(existingPath)
            : new FileInfo(existingPath);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Update path contains a reparse point.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }
    }

    private static void EnsureParentDirectory(string path, string root)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null || !IsPathInside(root, directory) && !IsSamePath(root, directory))
        {
            throw new UpdateApplyException("UPDATE_PATH_ESCAPE");
        }

        Directory.CreateDirectory(directory);
        EnsureNoReparsePath(directory, mustExist: true);
    }

    private static bool HasExpectedFile(string path, PackageFile expected) =>
        new FileInfo(path).Length == expected.Size &&
        string.Equals(UpdateApplyJournal.Sha256(path), expected.Sha256, StringComparison.OrdinalIgnoreCase);

    private static void CopyDurable(string source, string destination, PackageFile expected)
    {
        using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        if (!HasExpectedFile(destination, expected))
        {
            TryDeleteFile(destination);
            throw new UpdateApplyException("UPDATE_COPY_HASH_MISMATCH");
        }
    }

    private static void MoveDurable(string source, string destination, PackageFile expected)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            CopyDurable(source, destination, expected);
            File.Delete(source);
        }

        if (!HasExpectedFile(destination, expected))
        {
            TryDeleteFile(destination);
            throw new UpdateApplyException("UPDATE_MOVE_HASH_MISMATCH");
        }
    }

    private static void DeleteHealthFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryDeleteHealthFile(string path)
    {
        try
        {
            DeleteHealthFile(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteStaging(string path)
    {
        try
        {
            EnsureNoReparsePath(path, mustExist: true);
            Directory.Delete(path, recursive: true);
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
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record PackageManifest(IReadOnlyDictionary<string, PackageFile> Files);

    private sealed record PackageFile(long Size, string Sha256);

    private sealed class UpdateApplyException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
}
