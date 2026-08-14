using Lacertae.Application.Accounts;
using Lacertae.Application.GameRoots;
using Lacertae.Application.Java;
using Lacertae.Application.Launch;
using Lacertae.Application.SystemInfo;
using Lacertae.Application.Versions;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Home;
using Lacertae.Domain.Java;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Home;

public interface IJavaDiscovery
{
    Task<Result<JavaDiscoveryResult>> ExecuteAsync(CancellationToken cancellationToken);
}

public interface IHomeLaunchPlanHost
{
    Task<Result<LaunchPlan>> FreezeAsync(
        HomeLaunchContext context,
        CancellationToken cancellationToken);
}

public static class HomeLaunchRequirementId
{
    public const string Root = "root";
    public const string Version = "version";
    public const string Account = "account";
    public const string Java = "java";
    public const string Files = "files";
}

public static class HomeRouteIds
{
    public const string Settings = "settings";
    public const string Versions = "versions";
    public const string Downloads = "downloads";
}

public sealed record HomeLaunchRequirement(
    string Id,
    string Label,
    string ActionableReason,
    string RouteId,
    bool IsRepairPreview);

public sealed record HomeLaunchCardState(
    string? VersionDisplayName,
    string? AccountPlayerName,
    string? JavaSummary,
    int? MaximumMemoryMb,
    bool CanLaunch,
    IReadOnlyList<HomeLaunchRequirement> Requirements,
    bool HasDamagedFiles = false)
{
    public string? FirstActionableReason => Requirements.Count == 0 ? null : Requirements[0].ActionableReason;
}

public enum HomeQuickActionId
{
    OpenSaves,
    OpenVersionDirectory,
    OpenLogs,
}

public sealed record HomeQuickAction(HomeQuickActionId Id, string Label);

public sealed record HomeModuleState(
    HomeModuleId Module,
    int Order,
    bool IsVisible,
    string Title,
    string Summary,
    bool HasError,
    string? ErrorCode);

public sealed record HomeLaunchContext(
    GameRoot GameRoot,
    ListedGameVersion Version,
    LauncherSettings GlobalSettings,
    string AccountId,
    AccountIdentity AccountIdentity,
    AccountType AccountType,
    string AccountPlayerName,
    ResolvedJavaLaunchSettings JavaSettings);

public sealed record HomeState(
    HomeLaunchCardState LaunchCard,
    IReadOnlyList<HomeModuleState> Modules,
    IReadOnlyList<OperationSnapshot> ActiveTasks,
    IReadOnlyList<HomeQuickAction> QuickActions,
    string? SelectedGameRootId,
    string? SelectedVersionFolder)
{
    public HomeLaunchContext? LaunchContext { get; init; }
}

public sealed class BuildHomeState
{
    private static readonly IReadOnlyList<HomeQuickAction> RegisteredQuickActions =
    [
        new(HomeQuickActionId.OpenSaves, "打开存档"),
        new(HomeQuickActionId.OpenVersionDirectory, "打开版本目录"),
        new(HomeQuickActionId.OpenLogs, "打开日志"),
    ];

    private readonly IGameRootRepository gameRootRepository;
    private readonly IAccountRepository accountRepository;
    private readonly ListGameVersions listGameVersions;
    private readonly IJavaDiscovery javaDiscovery;
    private readonly IMemoryInfo memoryInfo;
    private readonly JavaArchitecture preferredArchitecture;

    public BuildHomeState(
        IGameRootRepository gameRootRepository,
        IAccountRepository accountRepository,
        ListGameVersions listGameVersions,
        IJavaDiscovery javaDiscovery,
        IMemoryInfo memoryInfo,
        JavaArchitecture preferredArchitecture = JavaArchitecture.X64)
    {
        this.gameRootRepository = gameRootRepository ?? throw new ArgumentNullException(nameof(gameRootRepository));
        this.accountRepository = accountRepository ?? throw new ArgumentNullException(nameof(accountRepository));
        this.listGameVersions = listGameVersions ?? throw new ArgumentNullException(nameof(listGameVersions));
        this.javaDiscovery = javaDiscovery ?? throw new ArgumentNullException(nameof(javaDiscovery));
        this.memoryInfo = memoryInfo ?? throw new ArgumentNullException(nameof(memoryInfo));
        if (!Enum.IsDefined(preferredArchitecture))
        {
            throw new ArgumentOutOfRangeException(nameof(preferredArchitecture));
        }

        this.preferredArchitecture = preferredArchitecture;
    }

    public Task<Result<HomeState>> ExecuteAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            settings,
            null,
            hasDamagedFiles: false,
            activeTasksReadFailed: false,
            cancellationToken: cancellationToken);

    public Task<Result<HomeState>> ExecuteAsync(
        LauncherSettings settings,
        IReadOnlyList<OperationSnapshot>? activeTasks,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            settings,
            activeTasks,
            hasDamagedFiles: false,
            activeTasksReadFailed: false,
            cancellationToken: cancellationToken);

    public Task<Result<HomeState>> ExecuteAsync(
        LauncherSettings settings,
        bool hasDamagedFiles,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            settings,
            null,
            hasDamagedFiles,
            activeTasksReadFailed: false,
            cancellationToken: cancellationToken);

    public async Task<Result<HomeState>> ExecuteAsync(
        LauncherSettings settings,
        IReadOnlyList<OperationSnapshot>? activeTasks = null,
        bool hasDamagedFiles = false,
        bool activeTasksReadFailed = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HomeModulePlacement.IsValid(settings.HomeModules))
        {
            return Result<HomeState>.Failure(Problem("SETTINGS_CORRUPT", ProblemStage.Configuration));
        }

        bool activeTasksInvalid = activeTasksReadFailed || activeTasks?.Any(static task =>
            task is null || string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Kind) || !Enum.IsDefined(task.State)) == true;
        IReadOnlyList<OperationSnapshot> tasks = activeTasks?
            .Where(static task => task is not null && (task.State is OperationState.Pending or OperationState.Running))
            .ToArray() ?? [];
        List<HomeLaunchRequirement> requirements = [];
        GameRoot? selectedRoot = null;
        ListedGameVersion? selectedVersion = null;
        Account? selectedAccount = null;
        ResolvedJavaLaunchSettings? resolvedJava = null;

        IReadOnlyList<GameRoot> roots;
        try
        {
            roots = await gameRootRepository.GetAllAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Result<HomeState>.Failure(Problem("HOME_ROOTS_UNAVAILABLE", ProblemStage.Storage));
        }

        if (!string.IsNullOrWhiteSpace(settings.SelectedGameRootId))
        {
            selectedRoot = roots.FirstOrDefault(root =>
                string.Equals(root.Id, settings.SelectedGameRootId, StringComparison.Ordinal) &&
                root.Availability == GameRootAvailability.Available);
        }

        if (selectedRoot is null)
        {
            AddRequirement(
                requirements,
                HomeLaunchRequirementId.Root,
                "游戏根目录",
                "请选择一个可用的游戏根目录。",
                HomeRouteIds.Settings,
                isRepairPreview: false);
        }

        if (selectedRoot is not null)
        {
            bool versionInspectionSucceeded = false;
            try
            {
                Result<IReadOnlyList<ListedGameVersion>> versions = await listGameVersions.ExecuteAsync(
                    selectedRoot,
                    settings,
                    cancellationToken);
                versionInspectionSucceeded = versions.IsSuccess;
                if (versions.IsSuccess)
                {
                    selectedVersion = versions.Value.FirstOrDefault(version =>
                        string.Equals(version.FolderName, settings.SelectedVersionFolder, StringComparison.Ordinal));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                versionInspectionSucceeded = false;
            }

            if (selectedVersion is null)
            {
                AddRequirement(
                    requirements,
                    HomeLaunchRequirementId.Version,
                    "游戏版本",
                    versionInspectionSucceeded
                        ? "请选择一个已安装且可解析的游戏版本。"
                        : "无法解析本地版本，请打开版本页重新扫描。",
                    HomeRouteIds.Versions,
                    isRepairPreview: false);
            }
        }
        else if (requirements.Count == 0 && !string.IsNullOrWhiteSpace(settings.SelectedVersionFolder))
        {
            AddRequirement(
                requirements,
                HomeLaunchRequirementId.Version,
                "游戏版本",
                "请先选择可用的游戏根目录，再选择已安装版本。",
                HomeRouteIds.Versions,
                isRepairPreview: false);
        }

        if (requirements.Count == 0)
        {
            string? accountId = selectedVersion?.AccountId ?? settings.DefaultAccountId;
            if (string.IsNullOrWhiteSpace(accountId))
            {
                AddRequirement(
                    requirements,
                    HomeLaunchRequirementId.Account,
                    "账号",
                    "请添加或选择一个可用账号。",
                    HomeRouteIds.Settings,
                    isRepairPreview: false);
            }
            else
            {
                try
                {
                    selectedAccount = await accountRepository.GetAsync(accountId, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    selectedAccount = null;
                }

                if (selectedAccount is null || selectedAccount.Status != AccountStatus.Active)
                {
                    AddRequirement(
                        requirements,
                        HomeLaunchRequirementId.Account,
                        "账号",
                        "所选账号不可用，请重新登录或选择其他账号。",
                        HomeRouteIds.Settings,
                        isRepairPreview: false);
                    selectedAccount = null;
                }
            }
        }

        if (requirements.Count == 0 && selectedVersion is not null)
        {
            try
            {
                List<JavaCandidate> configuredCandidates = [];
                if (!string.IsNullOrWhiteSpace(selectedVersion.JavaPath))
                {
                    configuredCandidates.Add(new JavaCandidate(selectedVersion.JavaPath, JavaSource.Manual, false));
                }

                if (!string.IsNullOrWhiteSpace(settings.GlobalJavaPath))
                {
                    configuredCandidates.Add(new JavaCandidate(settings.GlobalJavaPath, JavaSource.Manual, false));
                }

                Result<JavaDiscoveryResult> discovery = javaDiscovery is IJavaDiscoveryWithCandidates withCandidates
                    ? await withCandidates.ExecuteAsync(configuredCandidates, cancellationToken)
                    : await javaDiscovery.ExecuteAsync(cancellationToken);
                if (!discovery.IsSuccess)
                {
                    AddRequirement(
                        requirements,
                        HomeLaunchRequirementId.Java,
                        "Java",
                        "无法检查兼容 Java，请打开 Java 设置重新探测。",
                        HomeRouteIds.Settings,
                        isRepairPreview: false);
                }
                else
                {
                    MemorySnapshot memory = memoryInfo.GetSnapshot();
                    Result<ResolvedJavaLaunchSettings> java = ResolveJavaForVersion.Execute(
                        selectedVersion.Descriptor,
                        selectedVersion.Settings,
                        settings,
                        discovery.Value,
                        memory.TotalPhysicalMb,
                        memory.AvailablePhysicalMb,
                        preferredArchitecture);
                    if (!java.IsSuccess)
                    {
                        AddRequirement(
                            requirements,
                            HomeLaunchRequirementId.Java,
                            "Java",
                            "没有找到满足该版本要求的 Java，请打开 Java 设置选择或安装运行时。",
                            HomeRouteIds.Settings,
                            isRepairPreview: false);
                    }
                    else
                    {
                        resolvedJava = java.Value;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or PlatformNotSupportedException)
            {
                AddRequirement(
                    requirements,
                    HomeLaunchRequirementId.Java,
                    "Java",
                    "无法检查兼容 Java，请打开 Java 设置重新探测。",
                    HomeRouteIds.Settings,
                    isRepairPreview: false);
            }
        }

        if (requirements.Count == 0 && hasDamagedFiles)
        {
            AddRequirement(
                requirements,
                HomeLaunchRequirementId.Files,
                "文件",
                "检测到缺失或损坏的游戏文件，请预览修复。",
                HomeRouteIds.Downloads,
                isRepairPreview: true);
        }

        HomeLaunchCardState launchCard = new(
            selectedVersion?.DisplayName,
            selectedAccount?.PlayerName,
            resolvedJava is null ? null : $"Java {resolvedJava.Installation.MajorVersion} · {resolvedJava.Installation.Vendor}",
            resolvedJava?.Memory.MaximumMb,
            requirements.Count == 0 && selectedVersion is not null && selectedAccount is not null && resolvedJava is not null,
            requirements.ToArray(),
            hasDamagedFiles);

        IReadOnlyList<HomeModuleState> modules = settings.HomeModules
            .OrderBy(static placement => placement.Order)
            .Select(placement => TryBuildModuleState(placement, tasks, activeTasksInvalid))
            .ToArray();
        HomeState state = new HomeState(
            launchCard,
            modules,
            tasks,
            RegisteredQuickActions.ToArray(),
            selectedRoot?.Id,
            selectedVersion?.FolderName)
        {
            LaunchContext = launchCard.CanLaunch
                ? new HomeLaunchContext(
                    selectedRoot!,
                    selectedVersion!,
                    settings,
                    selectedAccount!.Id,
                    selectedAccount.Identity,
                    selectedAccount.Type,
                    selectedAccount.PlayerName,
                    resolvedJava!)
                : null,
        };
        return Result<HomeState>.Success(state);
    }

    private static HomeModuleState TryBuildModuleState(
        HomeModulePlacement placement,
        IReadOnlyList<OperationSnapshot> activeTasks,
        bool activeTasksInvalid)
    {
        try
        {
            return BuildModuleState(placement, activeTasks, activeTasksInvalid);
        }
        catch (Exception)
        {
            return new HomeModuleState(
                placement.Module,
                placement.Order,
                placement.IsVisible,
                ModuleTitle(placement.Module),
                "此板块暂时不可用，请稍后重试。",
                HasError: true,
                ErrorCode: "HOME_MODULE_UNAVAILABLE");
        }
    }

    private static HomeModuleState BuildModuleState(
        HomeModulePlacement placement,
        IReadOnlyList<OperationSnapshot> activeTasks,
        bool activeTasksInvalid)
    {
        if (placement.Module == HomeModuleId.ActiveTasks && activeTasksInvalid)
        {
            throw new InvalidOperationException("Active task snapshot is invalid.");
        }

        string title = ModuleTitle(placement.Module);
        string summary = placement.Module switch
        {
            HomeModuleId.RecentVersions => "查看最近使用的本地版本。",
            HomeModuleId.ActiveTasks => activeTasks.Count == 0 ? "暂无活动任务。" : $"有 {activeTasks.Count} 个活动任务。",
            HomeModuleId.QuickActions => "打开存档、版本目录和日志。",
            HomeModuleId.ReleaseNotes => "查看已安装启动器版本的说明。",
            _ => "",
        };
        return new HomeModuleState(placement.Module, placement.Order, placement.IsVisible, title, summary, false, null);
    }

    private static string ModuleTitle(HomeModuleId module) => module switch
    {
        HomeModuleId.RecentVersions => "最近版本",
        HomeModuleId.ActiveTasks => "活动任务",
        HomeModuleId.QuickActions => "快捷操作",
        HomeModuleId.ReleaseNotes => "发行说明",
        _ => "主页板块",
    };

    private static void AddRequirement(
        List<HomeLaunchRequirement> requirements,
        string id,
        string label,
        string reason,
        string routeId,
        bool isRepairPreview)
    {
        if (requirements.Count > 0)
        {
            return;
        }

        requirements.Add(new HomeLaunchRequirement(id, label, reason, routeId, isRepairPreview));
    }

    private static Problem Problem(string code, ProblemStage stage) => new(
        code,
        stage,
        code == "SETTINGS_CORRUPT" ? "problem.settings.invalid" : "problem.home.unavailable",
        false,
        Guid.NewGuid().ToString("N"),
        code == "SETTINGS_CORRUPT" ? ["action.settings.restore_backup"] : ["action.home.retry"]);
}
