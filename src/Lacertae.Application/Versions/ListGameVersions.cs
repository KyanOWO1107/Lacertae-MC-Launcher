using Lacertae.Application.GameRoots;
using Lacertae.Application.Games;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Versions;

public sealed record ListedGameVersion(
    GameVersionDescriptor Descriptor,
    string DisplayName,
    VersionOverride Settings,
    IsolationDecision IsolationDecision)
{
    public string GameRootId => Descriptor.GameRootId;
    public string FolderName => Descriptor.FolderName;
    public string VersionType => Descriptor.VersionType;
    public string? InheritsFrom => Descriptor.InheritsFrom;
    public JavaRequirement Java => Descriptor.Java;
    public bool HasModLoader => Descriptor.HasModLoader;
    public string? AccountId => Settings.AccountId;
    public string? JavaPath => Settings.JavaPath;
    public int? MinimumMemoryMb => Settings.MinimumMemoryMb;
    public int? MaximumMemoryMb => Settings.MaximumMemoryMb;
    public GcProfile? GcProfile => Settings.GcProfile;
    public IReadOnlyList<string> JvmArguments => Settings.JvmArguments;
    public IReadOnlyList<string> GameArguments => Settings.GameArguments;
}

public sealed class ListGameVersions(
    IGameEngine gameEngine,
    IVersionOverrideRepository overrideRepository)
{
    public Task<Result<IReadOnlyList<ListedGameVersion>>> ExecuteAsync(
        GameRoot gameRoot,
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        return ExecuteAsync(gameRoot.Id, gameRoot.NormalizedPath, settings, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<ListedGameVersion>>> ExecuteAsync(
        string gameRootId,
        string gameRootPath,
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameRootId) || string.IsNullOrWhiteSpace(gameRootPath))
        {
            return Result<IReadOnlyList<ListedGameVersion>>.Failure(InvalidProblem());
        }

        ArgumentNullException.ThrowIfNull(settings);
        Result<IReadOnlyList<GameVersionDescriptor>> inspected =
            await gameEngine.InspectLocalVersionsAsync(gameRootPath, cancellationToken);
        if (!inspected.IsSuccess)
        {
            return Result<IReadOnlyList<ListedGameVersion>>.Failure(inspected.Problem!);
        }

        IReadOnlyList<VersionOverride> overrides =
            await overrideRepository.GetForGameRootAsync(gameRootId, cancellationToken);
        Dictionary<string, VersionOverride> overridesByFolder = new(StringComparer.Ordinal);
        foreach (VersionOverride versionOverride in overrides)
        {
            if (!string.Equals(versionOverride.GameRootId, gameRootId, StringComparison.Ordinal) ||
                !overridesByFolder.TryAdd(versionOverride.VersionFolder, versionOverride))
            {
                return Result<IReadOnlyList<ListedGameVersion>>.Failure(InvalidProblem());
            }
        }

        List<ListedGameVersion> listed = [];
        foreach (GameVersionDescriptor descriptor in inspected.Value)
        {
            VersionOverride versionOverride = overridesByFolder.TryGetValue(descriptor.FolderName, out VersionOverride? found)
                ? found
                : new VersionOverride(
                    gameRootId,
                    descriptor.FolderName,
                    null,
                    IsolationOverride.Inherit,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
                    []);
            GameVersionDescriptor normalizedDescriptor = descriptor with { GameRootId = gameRootId };
            IsolationDecision isolationDecision = VersionIsolationResolver.Resolve(
                settings.IsolationPolicy,
                new VersionCharacteristics(descriptor.HasModLoader, descriptor.VersionType),
                versionOverride.Isolation);
            listed.Add(new ListedGameVersion(
                normalizedDescriptor,
                string.IsNullOrWhiteSpace(versionOverride.DisplayName)
                    ? descriptor.DisplayName
                    : versionOverride.DisplayName!,
                versionOverride,
                isolationDecision));
        }

        return Result<IReadOnlyList<ListedGameVersion>>.Success(listed);
    }

    private static Problem InvalidProblem() => new(
        "VERSION_LIST_INVALID",
        ProblemStage.VersionResolution,
        "problem.version.list_invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.version.inspect_again"]);
}
