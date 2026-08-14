using System.Security.Cryptography;
using System.Text;
using Lacertae.Application.Java;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Java;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Launch;

public sealed record LaunchFreezeRequest(
    GameRoot GameRoot,
    GameVersionDescriptor Version,
    VersionOverride VersionOverride,
    LauncherSettings GlobalSettings,
    Account Account,
    AuthSession Session,
    ResolvedJavaLaunchSettings JavaSettings,
    IReadOnlyList<DownloadArtifact> RequiredFiles,
    IReadOnlyList<string>? GlobalJvmArguments = null,
    IReadOnlyList<string>? GlobalGameArguments = null,
    LaunchDisposition Disposition = LaunchDisposition.KeepLauncherOpen);

public sealed class FreezeLaunchPlan(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public Task<Result<LaunchPlan>> ExecuteAsync(
        LaunchFreezeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Execute(request));
    }

    public Result<LaunchPlan> Execute(LaunchFreezeRequest request)
    {
        if (request is null || request.GameRoot is null || request.Version is null ||
            request.VersionOverride is null || request.GlobalSettings is null || request.Account is null ||
            request.Session is null || request.JavaSettings is null || request.RequiredFiles is null)
        {
            return Result<LaunchPlan>.Failure(InvalidProblem("LAUNCH_PLAN_INVALID"));
        }

        if (!IsValidRootAndVersion(request) ||
            request.Account.Status == AccountStatus.Deleting ||
            !Enum.IsDefined(request.Disposition))
        {
            return Result<LaunchPlan>.Failure(InvalidProblem("LAUNCH_PLAN_INVALID"));
        }

        if (request.Session.ExpiresUtc is DateTimeOffset expiresUtc && expiresUtc <= timeProvider.GetUtcNow())
        {
            return Result<LaunchPlan>.Failure(new Problem(
                "AUTH_SESSION_EXPIRED",
                ProblemStage.Authentication,
                "problem.auth.session_expired",
                true,
                Guid.NewGuid().ToString("N"),
                ["action.auth.sign_in_again"]));
        }

        if (!string.Equals(request.Session.PlayerName, request.Account.PlayerName, StringComparison.Ordinal) ||
            !string.Equals(request.Session.ProfileUuid, request.Account.Identity.ProfileUuid, StringComparison.Ordinal))
        {
            return Result<LaunchPlan>.Failure(InvalidProblem("AUTH_SESSION_ACCOUNT_MISMATCH"));
        }

        string root;
        string gameDirectory;
        string javaExecutablePath;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.GameRoot.NormalizedPath));
            string versionPath = Path.GetFullPath(Path.Combine(root, "versions", request.Version.FolderName));
            IsolationDecision isolation = VersionIsolationResolver.Resolve(
                request.GlobalSettings.IsolationPolicy,
                new VersionCharacteristics(request.Version.HasModLoader, request.Version.VersionType),
                request.VersionOverride.Isolation);
            gameDirectory = isolation.IsIsolated ? versionPath : root;
            if (!IsUnderRoot(gameDirectory, root))
            {
                return Result<LaunchPlan>.Failure(InvalidProblem("LAUNCH_PLAN_PATH_INVALID"));
            }

            string selectedJavaPath = string.IsNullOrWhiteSpace(request.VersionOverride.JavaPath)
                ? request.JavaSettings.Installation.ExecutablePath
                : request.VersionOverride.JavaPath!;
            javaExecutablePath = Path.GetFullPath(selectedJavaPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Result<LaunchPlan>.Failure(InvalidProblem("LAUNCH_PLAN_PATH_INVALID"));
        }

        if (!string.Equals(request.VersionOverride.GameRootId, request.GameRoot.Id, StringComparison.Ordinal) ||
            !string.Equals(request.VersionOverride.VersionFolder, request.Version.FolderName, StringComparison.Ordinal) ||
            !string.Equals(request.Version.GameRootId, request.GameRoot.Id, StringComparison.Ordinal) ||
            request.Version.Java is null || request.Version.Java.MajorVersion < 1 ||
            request.JavaSettings.Installation is null ||
            request.JavaSettings.Installation.MajorVersion != request.Version.Java.MajorVersion ||
            request.JavaSettings.Memory is null ||
            request.JavaSettings.Memory.MinimumMb < 256 ||
            request.JavaSettings.Memory.MaximumMb < request.JavaSettings.Memory.MinimumMb)
        {
            return Result<LaunchPlan>.Failure(InvalidProblem("LAUNCH_PLAN_INVALID"));
        }

        Result<JvmArgumentSet> jvmResult = ResolveJvmArguments(request);
        if (!jvmResult.IsSuccess)
        {
            return Result<LaunchPlan>.Failure(jvmResult.Problem!);
        }

        Result<IReadOnlyList<string>> gameArgumentsResult = LaunchArgumentParser.ParseLines(
            request.VersionOverride.GameArguments.Count > 0
                ? request.VersionOverride.GameArguments
                : request.GlobalGameArguments ?? []);
        if (!gameArgumentsResult.IsSuccess)
        {
            return Result<LaunchPlan>.Failure(gameArgumentsResult.Problem!);
        }

        Result<IReadOnlyList<DownloadArtifact>> filesResult = NormalizeRequiredFiles(request.RequiredFiles, root);
        if (!filesResult.IsSuccess)
        {
            return Result<LaunchPlan>.Failure(filesResult.Problem!);
        }

        try
        {
            JavaInstallation installation = request.JavaSettings.Installation;
            if (!string.Equals(installation.ExecutablePath, javaExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                installation = installation with
                {
                    Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(javaExecutablePath))).ToLowerInvariant(),
                    ExecutablePath = javaExecutablePath,
                };
            }

            return Result<LaunchPlan>.Success(new LaunchPlan(
                Guid.NewGuid().ToString("N"),
                request.GameRoot.Id,
                request.Version.FolderName,
                request.Version.FolderName,
                root,
                gameDirectory,
                installation.Id,
                javaExecutablePath,
                request.Version.Java.MajorVersion,
                request.Account.Id,
                request.Account.Type,
                request.Account.PlayerName,
                request.Account.Identity.ProfileUuid,
                request.Session,
                jvmResult.Value.MemoryArguments
                    .Select(ParseMemoryMb)
                    .Where(static value => value.HasValue)
                    .Select(static value => value!.Value)
                    .DefaultIfEmpty(request.JavaSettings.Memory.MinimumMb)
                    .Min(),
                jvmResult.Value.MemoryArguments
                    .Select(ParseMaximumMemoryMb)
                    .Where(static value => value.HasValue)
                    .Select(static value => value!.Value)
                    .DefaultIfEmpty(request.JavaSettings.Memory.MaximumMb)
                    .Max(),
                [.. jvmResult.Value.MemoryArguments, .. jvmResult.Value.GarbageCollectorArguments],
                jvmResult.Value.UserArguments,
                gameArgumentsResult.Value,
                filesResult.Value,
                request.Disposition,
                timeProvider.GetUtcNow(),
                string.IsNullOrWhiteSpace(request.VersionOverride.DisplayName)
                    ? request.Version.DisplayName
                    : request.VersionOverride.DisplayName,
                installation.Architecture));
        }
        catch (ArgumentException)
        {
            return Result<LaunchPlan>.Failure(InvalidProblem("LAUNCH_PLAN_INVALID"));
        }
        catch (NotSupportedException)
        {
            return Result<LaunchPlan>.Failure(InvalidProblem("LAUNCH_PLAN_INVALID"));
        }
    }

    private static Result<JvmArgumentSet> ResolveJvmArguments(LaunchFreezeRequest request)
    {
        IReadOnlyList<string> userArguments = request.VersionOverride.JvmArguments.Count > 0
            ? request.VersionOverride.JvmArguments
            : request.GlobalJvmArguments ?? request.JavaSettings.JvmArguments.UserArguments;
        Result<IReadOnlyList<string>> parsed = LaunchArgumentParser.ParseLines(userArguments);
        if (!parsed.IsSuccess)
        {
            return Result<JvmArgumentSet>.Failure(parsed.Problem!);
        }

        bool needsResolve = request.VersionOverride.GcProfile is not null ||
            request.VersionOverride.MinimumMemoryMb is not null ||
            request.VersionOverride.MaximumMemoryMb is not null;
        if (!needsResolve)
        {
            return Result<JvmArgumentSet>.Success(new JvmArgumentSet(
                request.JavaSettings.JvmArguments.MemoryArguments.ToArray(),
                request.JavaSettings.JvmArguments.GarbageCollectorArguments.ToArray(),
                parsed.Value));
        }

        int minimum = request.VersionOverride.MinimumMemoryMb ?? request.JavaSettings.Memory.MinimumMb;
        int maximum = request.VersionOverride.MaximumMemoryMb ?? request.JavaSettings.Memory.MaximumMb;
        if (minimum < 256 || maximum < minimum)
        {
            return Result<JvmArgumentSet>.Failure(InvalidProblem("LAUNCH_PLAN_INVALID_MEMORY"));
        }

        Result<JvmArgumentSet> resolved = JvmArgumentResolver.Resolve(
            request.VersionOverride.GcProfile ?? GcProfile.Automatic,
            request.JavaSettings.Installation.MajorVersion,
            request.JavaSettings.Installation.Architecture,
            new MemoryAllocation(minimum, maximum, MemoryMode.Fixed),
            parsed.Value);
        return resolved;
    }

    private static Result<IReadOnlyList<DownloadArtifact>> NormalizeRequiredFiles(
        IReadOnlyList<DownloadArtifact> artifacts,
        string root)
    {
        Dictionary<string, DownloadArtifact> unique = new(StringComparer.OrdinalIgnoreCase);
        foreach (DownloadArtifact? artifact in artifacts)
        {
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.RelativeDestinationPath) ||
                artifact.RelativeDestinationPath.Contains('\0'))
            {
                return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem("LAUNCH_PLAN_FILES_INVALID"));
            }

            string relative = artifact.RelativeDestinationPath.Replace('\\', '/');
            if (relative.Split('/').Any(static segment => segment is "" or "." or ".."))
            {
                return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem("LAUNCH_PLAN_FILES_INVALID"));
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem("LAUNCH_PLAN_FILES_INVALID"));
            }

            if (!IsUnderRoot(fullPath, root))
            {
                return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem("LAUNCH_PLAN_FILES_INVALID"));
            }

            DownloadArtifact normalized = artifact with { RelativeDestinationPath = relative };
            if (unique.TryGetValue(relative, out DownloadArtifact? existing) && existing != normalized)
            {
                return Result<IReadOnlyList<DownloadArtifact>>.Failure(InvalidProblem("LAUNCH_PLAN_FILES_CONFLICT"));
            }

            unique[relative] = normalized;
        }

        return Result<IReadOnlyList<DownloadArtifact>>.Success(unique.Values.ToArray());
    }

    private static int? ParseMemoryMb(string argument)
    {
        if (!argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ParseMemoryValue(argument[4..]);
    }

    private static int? ParseMaximumMemoryMb(string argument) =>
        argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)
            ? ParseMemoryValue(argument[4..])
            : null;

    private static int? ParseMemoryValue(string value)
    {
        if (value.EndsWith('M') && int.TryParse(value[..^1], out int mb))
        {
            return mb;
        }

        if (value.EndsWith('G') && int.TryParse(value[..^1], out int gb) && gb <= int.MaxValue / 1024)
        {
            return gb * 1024;
        }

        return null;
    }

    private static bool IsValidRootAndVersion(LaunchFreezeRequest request) =>
        !string.IsNullOrWhiteSpace(request.GameRoot.Id) &&
        !string.IsNullOrWhiteSpace(request.GameRoot.NormalizedPath) &&
        request.GameRoot.Availability == GameRootAvailability.Available &&
        string.Equals(request.Version.GameRootId, request.GameRoot.Id, StringComparison.Ordinal) &&
        IsSafeSegment(request.Version.FolderName) &&
        string.Equals(request.VersionOverride.GameRootId, request.GameRoot.Id, StringComparison.Ordinal) &&
        string.Equals(request.VersionOverride.VersionFolder, request.Version.FolderName, StringComparison.Ordinal);

    private static bool IsUnderRoot(string path, string root)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value is not "." and not ".." &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static Problem InvalidProblem(string code) => new(
        code,
        ProblemStage.LaunchPlanning,
        code == "AUTH_SESSION_ACCOUNT_MISMATCH" ? "problem.auth.session_mismatch" : "problem.launch.plan.invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.launch.review_settings"]);
}
