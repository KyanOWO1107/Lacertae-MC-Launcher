using Lacertae.Application.Install;
using Lacertae.Application.Java;
using Lacertae.Application.Storage;
using Lacertae.Application.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.Java;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Launch;

public sealed class LaunchPreflight : ILaunchPreflight
{
    private const long MinimumWorkingFreeBytes = 512L * 1024 * 1024;

    private readonly IGameFileVerifier verifier;
    private readonly IInstallEnvironment environment;
    private readonly IJavaProbe? javaProbe;
    private readonly IVersionRenameJournal? renameJournal;

    public LaunchPreflight(
        IGameFileVerifier verifier,
        IInstallEnvironment? environment = null,
        IJavaProbe? javaProbe = null,
        IVersionRenameJournal? renameJournal = null)
    {
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        this.environment = environment ?? new SystemInstallEnvironment();
        this.javaProbe = javaProbe;
        this.renameJournal = renameJournal;
    }

    public async Task<Result<LaunchPreflightResult>> ExecuteAsync(
        LaunchPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<string> failureCodes = [];
        List<string> damagedArtifacts = [];
        HashSet<string> suggestedActions = new(StringComparer.Ordinal);
        long availableFreeBytes = 0;

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.GameRootPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Result<LaunchPreflightResult>.Failure(Problem("LAUNCH_ROOT_PATH_INVALID"));
        }

        if (!environment.DirectoryExists(root))
        {
            failureCodes.Add("LAUNCH_ROOT_UNAVAILABLE");
            suggestedActions.Add("action.game_root.locate");
        }
        else if (!SecureFileSystem.IsSafeDirectory(root))
        {
            failureCodes.Add("LAUNCH_ROOT_UNAVAILABLE");
            suggestedActions.Add("action.game_root.locate");
        }
        else
        {
            CheckGameDirectory(plan, root, failureCodes, suggestedActions);
            availableFreeBytes = CheckFreeSpace(root, failureCodes, suggestedActions);
        }

        await CheckJavaAsync(plan, failureCodes, suggestedActions, cancellationToken);
        await CheckRequiredFilesAsync(plan, root, damagedArtifacts, failureCodes, suggestedActions, cancellationToken);
        CheckActiveOperations(plan, root, failureCodes, suggestedActions);

        if (damagedArtifacts.Count > 0)
        {
            suggestedActions.Add("action.version.repair");
        }

        string[] orderedFailures = failureCodes.Distinct(StringComparer.Ordinal).ToArray();
        string[] orderedActions = suggestedActions.Order(StringComparer.Ordinal).ToArray();
        bool ready = orderedFailures.Length == 0 && damagedArtifacts.Count == 0;
        return Result<LaunchPreflightResult>.Success(new LaunchPreflightResult(
            ready,
            damagedArtifacts.Distinct(StringComparer.Ordinal).ToArray(),
            orderedFailures,
            orderedActions,
            availableFreeBytes));
    }

    public Task<Result<LaunchPreflightResult>> CheckAsync(
        LaunchPlan plan,
        CancellationToken cancellationToken) => ExecuteAsync(plan, cancellationToken);

    public async Task<Result<Unit>> EnsureReadyAsync(
        LaunchPlan plan,
        CancellationToken cancellationToken)
    {
        Result<LaunchPreflightResult> result = await ExecuteAsync(plan, cancellationToken);
        if (!result.IsSuccess)
        {
            return Result<Unit>.Failure(result.Problem!);
        }

        if (result.Value.IsReady)
        {
            return Result.Success();
        }

        string[] actions = result.Value.SuggestedActionKeys.Count == 0
            ? ["action.launch.review_settings"]
            : result.Value.SuggestedActionKeys.ToArray();
        string failureCode = result.Value.MissingOrDamagedArtifactIds.Count > 0
            ? "LAUNCH_REQUIRED_FILES_INVALID"
            : result.Value.FailureCodes.Count > 0
                ? result.Value.FailureCodes[0]
                : "LAUNCH_PREFLIGHT_FAILED";
        return Result<Unit>.Failure(new Problem(
            failureCode,
            ProblemStage.LaunchPlanning,
            "problem.launch.preflight_failed",
            false,
            plan.CorrelationId,
            actions,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["missingArtifactCount"] = result.Value.MissingOrDamagedArtifactIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
    }

    private void CheckGameDirectory(
        LaunchPlan plan,
        string root,
        List<string> failureCodes,
        HashSet<string> suggestedActions)
    {
        string gameDirectory;
        try
        {
            gameDirectory = Path.GetFullPath(plan.GameDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            failureCodes.Add("LAUNCH_GAME_DIRECTORY_INVALID");
            suggestedActions.Add("action.launch.review_settings");
            return;
        }

        if (!IsUnderRoot(gameDirectory, root))
        {
            failureCodes.Add("LAUNCH_GAME_DIRECTORY_OUTSIDE_ROOT");
            suggestedActions.Add("action.launch.review_settings");
            return;
        }

        try
        {
            SecureFileSystem.EnsureDirectory(gameDirectory, root);
            if (!SecureFileSystem.IsSafeDirectory(gameDirectory, root))
            {
                failureCodes.Add("LAUNCH_GAME_DIRECTORY_UNAVAILABLE");
                suggestedActions.Add("action.launch.review_settings");
                return;
            }
        }
        catch (IOException)
        {
            failureCodes.Add("LAUNCH_GAME_DIRECTORY_UNAVAILABLE");
            suggestedActions.Add("action.launch.review_settings");
            return;
        }
        catch (UnauthorizedAccessException)
        {
            failureCodes.Add("LAUNCH_GAME_DIRECTORY_UNAVAILABLE");
            suggestedActions.Add("action.launch.review_settings");
            return;
        }
        catch (NotSupportedException)
        {
            failureCodes.Add("LAUNCH_GAME_DIRECTORY_UNAVAILABLE");
            suggestedActions.Add("action.launch.review_settings");
            return;
        }

        if (!environment.IsDirectoryWritable(gameDirectory))
        {
            failureCodes.Add("LAUNCH_GAME_DIRECTORY_UNWRITABLE");
            suggestedActions.Add("action.launch.review_settings");
        }
    }

    private long CheckFreeSpace(
        string root,
        List<string> failureCodes,
        HashSet<string> suggestedActions)
    {
        try
        {
            long available = environment.GetAvailableFreeBytes(root);
            if (available < MinimumWorkingFreeBytes)
            {
                failureCodes.Add("LAUNCH_DISK_SPACE_INSUFFICIENT");
                suggestedActions.Add("action.storage.free_space");
            }

            return available;
        }
        catch (IOException)
        {
            failureCodes.Add("LAUNCH_DISK_SPACE_UNKNOWN");
            suggestedActions.Add("action.storage.check_space");
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            failureCodes.Add("LAUNCH_DISK_SPACE_UNKNOWN");
            suggestedActions.Add("action.storage.check_space");
            return 0;
        }
    }

    private async Task CheckJavaAsync(
        LaunchPlan plan,
        List<string> failureCodes,
        HashSet<string> suggestedActions,
        CancellationToken cancellationToken)
    {
        string executable = plan.JavaExecutablePath;
        string? executableDirectory;
        try
        {
            executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            executableDirectory = null;
        }

        if (!Path.IsPathFullyQualified(executable) ||
            executableDirectory is null ||
            !SecureFileSystem.IsSafeFile(executable, executableDirectory))
        {
            failureCodes.Add("LAUNCH_JAVA_MISSING");
            suggestedActions.Add("action.java.install_or_select");
            return;
        }

        if (javaProbe is null)
        {
            return;
        }

        Result<JavaInstallation> probe = await javaProbe.ProbeAsync(
            executable,
            JavaSource.Manual,
            false,
            cancellationToken);
        if (!probe.IsSuccess)
        {
            failureCodes.Add(probe.Problem?.Code ?? "LAUNCH_JAVA_INVALID");
            suggestedActions.Add("action.java.check_runtime");
            return;
        }

        if (probe.Value.MajorVersion != plan.RequiredJavaMajor)
        {
            failureCodes.Add("LAUNCH_JAVA_MAJOR_MISMATCH");
            suggestedActions.Add("action.java.select_matching_runtime");
        }

        if (plan.JavaArchitecture != JavaArchitecture.Unknown &&
            probe.Value.Architecture != plan.JavaArchitecture)
        {
            failureCodes.Add("LAUNCH_JAVA_ARCHITECTURE_MISMATCH");
            suggestedActions.Add("action.java.select_matching_runtime");
        }
    }

    private async Task CheckRequiredFilesAsync(
        LaunchPlan plan,
        string root,
        List<string> damagedArtifacts,
        List<string> failureCodes,
        HashSet<string> suggestedActions,
        CancellationToken cancellationToken)
    {
        foreach (DownloadArtifact artifact in plan.RequiredFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path;
            try
            {
                string relative = artifact.RelativeDestinationPath.Replace('\\', '/');
                path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                damagedArtifacts.Add(artifact.ArtifactId);
                failureCodes.Add("LAUNCH_REQUIRED_FILE_PATH_INVALID");
                continue;
            }

            if (!IsUnderRoot(path, root) || !SecureFileSystem.IsSafeFile(path, root))
            {
                damagedArtifacts.Add(artifact.ArtifactId);
                continue;
            }

            try
            {
                using Stream stream = SecureFileSystem.OpenRead(path, root);
                if (stream.Length != artifact.ExpectedSize)
                {
                    damagedArtifacts.Add(artifact.ArtifactId);
                    continue;
                }
            }
            catch (IOException)
            {
                damagedArtifacts.Add(artifact.ArtifactId);
                failureCodes.Add("LAUNCH_REQUIRED_FILE_UNREADABLE");
                continue;
            }

            Result<bool> verified = await verifier.VerifyAsync(artifact, path, cancellationToken);
            if (!verified.IsSuccess)
            {
                failureCodes.Add(verified.Problem?.Code ?? "LAUNCH_REQUIRED_FILE_VERIFY_FAILED");
                damagedArtifacts.Add(artifact.ArtifactId);
            }
            else if (!verified.Value)
            {
                damagedArtifacts.Add(artifact.ArtifactId);
            }
        }
    }

    private void CheckActiveOperations(
        LaunchPlan plan,
        string root,
        List<string> failureCodes,
        HashSet<string> suggestedActions)
    {
        if (InstallRootLocks.IsBusy(root))
        {
            failureCodes.Add("LAUNCH_INSTALL_ACTIVE");
            suggestedActions.Add("action.install.wait_for_completion");
        }

        if (renameJournal is null)
        {
            return;
        }

        Result<Lacertae.Domain.Versions.VersionRenameJournalEntry?> journal =
            renameJournal.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!journal.IsSuccess)
        {
            failureCodes.Add(journal.Problem?.Code ?? "LAUNCH_RENAME_STATE_UNKNOWN");
            suggestedActions.Add("action.version.review_rename");
            return;
        }

        if (journal.Value is not null &&
            string.Equals(journal.Value.Plan.GameRootId, plan.GameRootId, StringComparison.Ordinal) &&
            (string.Equals(journal.Value.Plan.SourceFolder, plan.VersionFolder, StringComparison.Ordinal) ||
             string.Equals(journal.Value.Plan.TargetFolder, plan.VersionFolder, StringComparison.Ordinal)))
        {
            failureCodes.Add("LAUNCH_RENAME_ACTIVE");
            suggestedActions.Add("action.version.review_rename");
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.LaunchPlanning,
        "problem.launch.preflight_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.launch.review_settings"]);
}
