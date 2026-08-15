using System.ComponentModel;
using System.Windows.Input;
using Lacertae.Application.Home;
using Lacertae.Application.Java;
using Lacertae.Application.Startup;
using Lacertae.Desktop.ViewModels.Accounts;
using Lacertae.Desktop.ViewModels.Downloads;
using Lacertae.Desktop.ViewModels.Home;
using Lacertae.Desktop.ViewModels.Java;
using Lacertae.Desktop.ViewModels.Onboarding;
using Lacertae.Desktop.ViewModels.Resources;
using Lacertae.Desktop.ViewModels.Tasks;
using Lacertae.Desktop.ViewModels.Updates;
using Lacertae.Desktop.ViewModels.Versions;
using Lacertae.Domain.Home;
using Lacertae.Domain.Storage;

namespace Lacertae.Desktop.ViewModels;

public sealed record LauncherPageViewModel(
    string RouteId,
    string Heading,
    string Summary,
    string Body,
    bool IsActionPage = false);

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, Func<LauncherPageViewModel>> pageFactories =
        new Dictionary<string, Func<LauncherPageViewModel>>(StringComparer.Ordinal)
        {
            [LauncherRouteIds.Home] = static () => new LauncherPageViewModel(
                LauncherRouteIds.Home,
                "主页",
                "准备好后即可启动游戏。",
                "选择游戏根目录、账号、版本和兼容 Java 后，启动按钮会在启动预检通过时启用。"),
            [LauncherRouteIds.Accounts] = static () => new LauncherPageViewModel(
                LauncherRouteIds.Accounts,
                "账号",
                "管理离线和 Microsoft 多账号。",
                "账号公开资料保存在本地数据库；Microsoft 会话材料由平台密钥库保护，界面只展示玩家名、状态和已缓存的本地头像。"),
            [LauncherRouteIds.Versions] = static () => new LauncherPageViewModel(
                LauncherRouteIds.Versions,
                "版本",
                "查看当前游戏根目录中的本地版本。",
                "M1 只管理原版版本及其安装状态；版本文件不会在浏览时被改写。"),
            [LauncherRouteIds.Downloads] = static () => new LauncherPageViewModel(
                LauncherRouteIds.Downloads,
                "下载",
                "安装或修复原版 Minecraft 文件。",
                "下载页只包含原版安装和修复操作，其他来源或整合包将在后续版本提供。",
                IsActionPage: true),
            [LauncherRouteIds.Resources] = static () => new LauncherPageViewModel(
                LauncherRouteIds.Resources,
                "资源",
                "管理当前版本的本地资源文件夹。",
                "这里打开所选版本范围内的 Mods、资源包、光影和存档文件夹；在线搜索与安装会在后续版本提供。"),
            [LauncherRouteIds.Tasks] = static () => new LauncherPageViewModel(
                LauncherRouteIds.Tasks,
                "任务",
                "查看安装、修复和其他后台任务。",
                "长任务会保留进度和可恢复状态，关闭窗口不会把未完成的任务伪装成成功。"),
            [LauncherRouteIds.Settings] = static () => new LauncherPageViewModel(
                LauncherRouteIds.Settings,
                "设置",
                "调整主题、数据位置说明和启动偏好。",
                "便携模式由程序目录中的 lacertae.portable 标记决定；修改标记后需要重启启动器。"),
        };

    private readonly string greeting = "欢迎使用 Lacertae";
    private readonly string versionSummary = "未选择游戏版本";
    private bool isCompactNavigation;
    private string currentRouteId = LauncherRouteIds.Home;
    private LauncherPageViewModel currentPage;
    private bool hasStartupState;
    private bool isOnboardingVisible;
    private HomeViewModel home;
    private readonly RepairPreviewViewModel repairPreview = new();
    private readonly DelegateCommand openOnboardingCommand;
    private readonly IHomeLaunchPlanHost? launchPlanHost;
    private VersionsViewModel? versions;
    private VanillaDownloadsViewModel? downloads;
    private TasksViewModel? tasks;
    private LocalResourcesViewModel? resources;
    private AccountsViewModel? accounts;
    private VersionSettingsViewModel? versionSettings;
    private Func<VersionRowViewModel, VersionSettingsViewModel>? versionSettingsFactory;
    private JavaSettingsViewModel javaSettings;
    private UpdateViewModel updates;

    public MainWindowViewModel(
        IHomeLaunchPlanHost? launchPlanHost = null,
        IJavaProbe? javaProbe = null)
    {
        this.launchPlanHost = launchPlanHost;
        javaSettings = new(
            new JavaDiscoveryResult([], []),
            null,
            Lacertae.Domain.Java.JavaArchitecture.Unknown,
            javaProbe);
        updates = new UpdateViewModel();
        NavigationItems =
        [
            new NavigationItemViewModel(LauncherRouteIds.Home, "主页", "启动概览"),
            new NavigationItemViewModel(LauncherRouteIds.Accounts, "账号", "离线与 Microsoft 多账号"),
            new NavigationItemViewModel(LauncherRouteIds.Versions, "版本", "本地版本"),
            new NavigationItemViewModel(LauncherRouteIds.Downloads, "下载", "原版安装与修复"),
            new NavigationItemViewModel(LauncherRouteIds.Resources, "资源", "当前版本本地文件夹"),
            new NavigationItemViewModel(LauncherRouteIds.Tasks, "任务", "后台任务"),
            new NavigationItemViewModel(LauncherRouteIds.Settings, "设置", "启动器设置"),
        ];
        currentPage = CreatePage(LauncherRouteIds.Home);
        SelectRoute(currentRouteId);
        Onboarding = new OnboardingViewModel();
        Onboarding.PropertyChanged += OnboardingPropertyChanged;
        repairPreview.PropertyChanged += RepairPreviewPropertyChanged;
        OpenRepairPreviewCommand = new DelegateCommand(OpenRepairPreview, () => true);
        CloseRepairPreviewCommand = repairPreview.CloseCommand;
        ConfirmRepairDownloadCommand = repairPreview.ConfirmDownloadCommand;
        CloseVersionSettingsCommand = new DelegateCommand(CloseVersionSettings, () => true);
        home = CreateHomeViewModel(CreateEmptyHomeState());
        openOnboardingCommand = new DelegateCommand(OpenOnboarding, () => CanOpenOnboarding);
        OpenOnboardingCommand = openOnboardingCommand;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public string Greeting => greeting;

    public string VersionSummary => versionSummary;

    public bool CanLaunch => hasStartupState && Onboarding.CanLaunch;

    public bool IsLaunchDisabled => !CanLaunch;

    public JavaSettingsViewModel JavaSettings => javaSettings;

    public VersionsViewModel? Versions => versions;

    public VanillaDownloadsViewModel? Downloads => downloads;

    public TasksViewModel? Tasks => tasks;

    public LocalResourcesViewModel? Resources => resources;

    public AccountsViewModel? Accounts => accounts;

    public UpdateViewModel Updates => updates;

    public VersionSettingsViewModel? VersionSettings => versionSettings;

    public HomeViewModel Home => home;

    public bool IsRepairPreviewOpen => repairPreview.IsOpen;

    public string RepairPreviewSummary => repairPreview.Summary;

    public bool CanConfirmRepairDownload => repairPreview.CanConfirmDownload;

    public OnboardingViewModel Onboarding { get; private set; }

    public bool HasStartupState => hasStartupState;

    public bool CanOpenOnboarding => hasStartupState;

    public bool IsSettingsPage => string.Equals(
        CurrentRouteId,
        LauncherRouteIds.Settings,
        StringComparison.Ordinal);

    public bool IsHomePage => string.Equals(CurrentRouteId, LauncherRouteIds.Home, StringComparison.Ordinal);

    public bool IsAccountsPage => string.Equals(CurrentRouteId, LauncherRouteIds.Accounts, StringComparison.Ordinal);

    public bool IsNotHomePage => !IsHomePage;

    public bool IsVersionsPage => string.Equals(CurrentRouteId, LauncherRouteIds.Versions, StringComparison.Ordinal);

    public bool IsDownloadsPage => string.Equals(CurrentRouteId, LauncherRouteIds.Downloads, StringComparison.Ordinal);

    public bool IsTasksPage => string.Equals(CurrentRouteId, LauncherRouteIds.Tasks, StringComparison.Ordinal);

    public bool IsResourcesPage => string.Equals(CurrentRouteId, LauncherRouteIds.Resources, StringComparison.Ordinal);

    public bool IsVersionSettingsVisible => IsVersionsPage && VersionSettings is not null;

    public bool IsVersionsContentVisible => IsShellVisible && IsVersionsPage && Versions is not null;

    public bool IsDownloadsContentVisible => IsShellVisible && IsDownloadsPage && Downloads is not null;

    public bool IsTasksContentVisible => IsShellVisible && IsTasksPage && Tasks is not null;

    public bool IsResourcesContentVisible => IsShellVisible && IsResourcesPage && Resources is not null;

    public bool IsAccountsContentVisible => IsShellVisible && IsAccountsPage && Accounts is not null;

    public bool IsGenericPageVisible => IsShellVisible && IsNotHomePage &&
        !IsVersionsContentVisible && !IsDownloadsContentVisible &&
        !IsTasksContentVisible && !IsResourcesContentVisible && !IsAccountsContentVisible;

    public bool IsHomeContentVisible => IsShellVisible && IsHomePage;

    public bool IsUpdateBannerVisible => IsShellVisible && updates.IsBannerVisible;

    public ICommand OpenOnboardingCommand { get; }

    public ICommand OpenRepairPreviewCommand { get; }

    public ICommand CloseRepairPreviewCommand { get; }

    public ICommand ConfirmRepairDownloadCommand { get; }

    public ICommand CloseVersionSettingsCommand { get; }

    public bool IsOnboardingVisible
    {
        get => isOnboardingVisible;
        private set
        {
            if (isOnboardingVisible == value)
            {
                return;
            }

            isOnboardingVisible = value;
            OnPropertyChanged(nameof(IsOnboardingVisible));
            OnPropertyChanged(nameof(IsShellVisible));
            OnPropertyChanged(nameof(IsShellWideNavigation));
            OnPropertyChanged(nameof(IsShellCompactNavigation));
            OnPropertyChanged(nameof(IsVersionsContentVisible));
            OnPropertyChanged(nameof(IsDownloadsContentVisible));
            OnPropertyChanged(nameof(IsTasksContentVisible));
            OnPropertyChanged(nameof(IsResourcesContentVisible));
            OnPropertyChanged(nameof(IsAccountsContentVisible));
            OnPropertyChanged(nameof(IsGenericPageVisible));
            OnPropertyChanged(nameof(IsHomeContentVisible));
            OnPropertyChanged(nameof(IsUpdateBannerVisible));
        }
    }

    public bool IsShellVisible => !IsOnboardingVisible;

    public bool IsShellWideNavigation => IsShellVisible && IsWideNavigation;

    public bool IsShellCompactNavigation => IsShellVisible && IsCompactNavigation;

    public string CurrentRouteId => currentRouteId;

    public LauncherPageViewModel CurrentPage => currentPage;

    public string CurrentPageHeading => currentPage.Heading;

    public bool IsCompactNavigation
    {
        get => isCompactNavigation;
        private set
        {
            if (isCompactNavigation == value)
            {
                return;
            }

            isCompactNavigation = value;
            OnPropertyChanged(nameof(IsCompactNavigation));
            OnPropertyChanged(nameof(IsWideNavigation));
            OnPropertyChanged(nameof(IsShellWideNavigation));
            OnPropertyChanged(nameof(IsShellCompactNavigation));
        }
    }

    public bool IsWideNavigation => !IsCompactNavigation;

    public void SetViewportWidth(double logicalWidth) => IsCompactNavigation = logicalWidth < 900;

    public void ApplyStartupState(
        StartupState startupState,
        IOnboardingUseCases? onboardingUseCases = null,
        OnboardingDurableState? preflightState = null,
        bool microsoftLoginConfigured = false,
        string? microsoftConfigurationErrorCode = null)
    {
        ArgumentNullException.ThrowIfNull(startupState);
        hasStartupState = true;
        Onboarding.PropertyChanged -= OnboardingPropertyChanged;
        Onboarding = new OnboardingViewModel(
            onboardingUseCases ?? new DisabledOnboardingUseCases(),
            new OnboardingDataRootSnapshot(
                startupState.DataRoot.Mode,
                BuildDataRootSummary(startupState.DataRoot.Mode)),
            preflightState ?? new OnboardingDurableState(
                startupState.Settings.SelectedGameRootId,
                startupState.Settings.DefaultAccountId,
                startupState.Settings.SelectedVersionFolder,
                startupState.Settings.GlobalJavaPath,
                null,
                false,
                false,
                false),
            microsoftLoginConfigured: microsoftLoginConfigured,
            microsoftConfigurationErrorCode: microsoftConfigurationErrorCode);
        Onboarding.PropertyChanged += OnboardingPropertyChanged;
        IsOnboardingVisible = preflightState?.CanFormLaunchPreflight != true;
        OnPropertyChanged(nameof(HasStartupState));
        OnPropertyChanged(nameof(CanOpenOnboarding));
        openOnboardingCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(Onboarding));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(IsLaunchDisabled));
    }

    public void OpenOnboarding()
    {
        if (!CanOpenOnboarding)
        {
            return;
        }

        Onboarding.Reopen();
        IsOnboardingVisible = true;
    }

    public bool TryNavigate(string routeId)
    {
        ArgumentNullException.ThrowIfNull(routeId);
        if (!pageFactories.ContainsKey(routeId))
        {
            return false;
        }

        currentRouteId = routeId;
        currentPage = CreatePage(routeId);
        SelectRoute(routeId);
        OnPropertyChanged(nameof(CurrentRouteId));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CurrentPageHeading));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsAccountsPage));
        OnPropertyChanged(nameof(IsNotHomePage));
        OnPropertyChanged(nameof(IsVersionsPage));
        OnPropertyChanged(nameof(IsDownloadsPage));
        OnPropertyChanged(nameof(IsTasksPage));
        OnPropertyChanged(nameof(IsResourcesPage));
        OnPropertyChanged(nameof(IsVersionSettingsVisible));
        OnPropertyChanged(nameof(IsVersionsContentVisible));
        OnPropertyChanged(nameof(IsDownloadsContentVisible));
        OnPropertyChanged(nameof(IsTasksContentVisible));
        OnPropertyChanged(nameof(IsResourcesContentVisible));
        OnPropertyChanged(nameof(IsAccountsContentVisible));
        OnPropertyChanged(nameof(IsGenericPageVisible));
        OnPropertyChanged(nameof(IsHomeContentVisible));
        OnPropertyChanged(nameof(IsUpdateBannerVisible));
        return true;
    }

    public void Navigate(string routeId)
    {
        if (!TryNavigate(routeId))
        {
            throw new ArgumentException($"Unknown launcher route '{routeId}'.", nameof(routeId));
        }
    }

    public void ApplyHomeState(HomeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        home = CreateHomeViewModel(state);
        OnPropertyChanged(nameof(Home));
    }

    /// <summary>
    /// Installs the real M1 feature pages after startup has resolved the
    /// durable data root. The parameterless shell remains dependency-free for
    /// recovery and headless UI tests.
    /// </summary>
    public void ConfigureFeaturePages(
        VersionsViewModel versions,
        VanillaDownloadsViewModel downloads,
        Func<VersionRowViewModel, VersionSettingsViewModel>? versionSettingsFactory = null,
        TasksViewModel? tasks = null,
        LocalResourcesViewModel? resources = null)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(downloads);

        if (this.versions is not null)
        {
            this.versions.EditSettingsRequested -= OnEditSettingsRequested;
        }

        this.versions = versions;
        this.downloads = downloads;
        this.tasks = tasks;
        this.resources = resources;
        this.versionSettingsFactory = versionSettingsFactory;
        versions.EditSettingsRequested += OnEditSettingsRequested;
        OnPropertyChanged(nameof(Versions));
        OnPropertyChanged(nameof(Downloads));
        OnPropertyChanged(nameof(IsVersionsContentVisible));
        OnPropertyChanged(nameof(IsDownloadsContentVisible));
        OnPropertyChanged(nameof(Tasks));
        OnPropertyChanged(nameof(Resources));
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(IsTasksContentVisible));
        OnPropertyChanged(nameof(IsResourcesContentVisible));
        OnPropertyChanged(nameof(IsAccountsContentVisible));
        OnPropertyChanged(nameof(IsGenericPageVisible));

        _ = LoadFeaturePagesSafelyAsync(versions, downloads, tasks);
    }

    public void ConfigureJavaSettings(JavaSettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        javaSettings = settings;
        OnPropertyChanged(nameof(JavaSettings));
    }

    public void ConfigureAccounts(AccountsViewModel accountViewModel)
    {
        ArgumentNullException.ThrowIfNull(accountViewModel);
        accounts = accountViewModel;
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(IsAccountsContentVisible));
        OnPropertyChanged(nameof(IsGenericPageVisible));
        _ = LoadAccountsSafelyAsync(accountViewModel);
    }

    public void ConfigureUpdates(UpdateViewModel updateViewModel)
    {
        ArgumentNullException.ThrowIfNull(updateViewModel);
        if (updates is not null)
        {
            updates.PropertyChanged -= OnUpdatesPropertyChanged;
        }

        updates = updateViewModel;
        updates.PropertyChanged += OnUpdatesPropertyChanged;
        OnPropertyChanged(nameof(Updates));
        OnPropertyChanged(nameof(IsUpdateBannerVisible));
    }

    private void OnUpdatesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpdateViewModel.IsBannerVisible) or nameof(UpdateViewModel.State))
        {
            OnPropertyChanged(nameof(IsUpdateBannerVisible));
        }
    }

    private static async Task LoadFeaturePagesSafelyAsync(
        VersionsViewModel versions,
        VanillaDownloadsViewModel downloads,
        TasksViewModel? tasks)
    {
        try
        {
            await versions.LoadAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The page keeps its typed error state; a page-load failure must
            // not turn an otherwise usable launcher into a startup failure.
        }

        try
        {
            await downloads.LoadAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // VanillaDownloadsViewModel exposes metadata failures inline.
        }

        if (tasks is not null)
        {
            try
            {
                await tasks.RefreshAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // TasksViewModel exposes its typed store error inline.
            }
        }
    }

    private static async Task LoadAccountsSafelyAsync(AccountsViewModel accounts)
    {
        try
        {
            await accounts.LoadAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // AccountsViewModel keeps a structured inline error; a page-load
            // failure must not make the shell unusable.
        }
    }

    private void OnEditSettingsRequested(object? sender, VersionRowViewModel row)
    {
        if (versionSettingsFactory is null)
        {
            return;
        }

        if (versionSettings is not null)
        {
            versionSettings.Changed -= OnVersionSettingsChanged;
        }
        versionSettings = versionSettingsFactory(row);
        versionSettings.Changed += OnVersionSettingsChanged;
        OnPropertyChanged(nameof(VersionSettings));
        OnPropertyChanged(nameof(IsVersionSettingsVisible));
    }

    private void CloseVersionSettings()
    {
        if (versionSettings is not null)
        {
            versionSettings.Changed -= OnVersionSettingsChanged;
        }
        versionSettings = null;
        OnPropertyChanged(nameof(VersionSettings));
        OnPropertyChanged(nameof(IsVersionSettingsVisible));
    }

    private void OnVersionSettingsChanged(object? sender, EventArgs e)
    {
        if (versions is null)
        {
            return;
        }

        // A successful save or physical rename invalidates the row snapshot;
        // reload it before the user can issue another edit against stale data.
        _ = versions.RefreshVersionsAsync(CancellationToken.None);
        if (sender is VersionSettingsViewModel)
        {
            CloseVersionSettings();
        }
    }

    private HomeViewModel CreateHomeViewModel(HomeState state) => new(
        state,
        navigation: routeId => TryNavigate(routeId),
        repairPreview: OpenRepairPreview,
        executeQuickAction: ExecuteHomeQuickAction,
        repairPreviewState: repairPreview,
        launchPlanHost: launchPlanHost);

    public void OpenRepairPreview()
    {
        repairPreview.Open();
        TryNavigate(LauncherRouteIds.Downloads);
    }

    private void ExecuteHomeQuickAction(HomeQuickAction action)
    {
        string routeId = action.Id switch
        {
            HomeQuickActionId.OpenSaves or HomeQuickActionId.OpenVersionDirectory => LauncherRouteIds.Resources,
            HomeQuickActionId.OpenLogs => LauncherRouteIds.Tasks,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action.Id, "Unknown home quick action."),
        };

        if (!TryNavigate(routeId))
        {
            throw new InvalidOperationException($"Home quick action route '{routeId}' is unavailable.");
        }
    }

    private static HomeState CreateEmptyHomeState() => new(
        new HomeLaunchCardState(
            null,
            null,
            null,
            null,
            false,
            [new HomeLaunchRequirement(
                HomeLaunchRequirementId.Root,
                "游戏根目录",
                "请选择一个可用的游戏根目录。",
                LauncherRouteIds.Settings,
                false)]),
        [
            new HomeModuleState(HomeModuleId.RecentVersions, 0, true, "最近版本", "暂无最近版本。", false, null),
            new HomeModuleState(HomeModuleId.ActiveTasks, 1, true, "活动任务", "暂无活动任务。", false, null),
            new HomeModuleState(HomeModuleId.QuickActions, 2, true, "快捷操作", "打开存档、版本目录和日志。", false, null),
            new HomeModuleState(HomeModuleId.ReleaseNotes, 3, true, "发行说明", "暂无发行说明。", false, null),
        ],
        [],
        [
            new HomeQuickAction(HomeQuickActionId.OpenSaves, "打开存档"),
            new HomeQuickAction(HomeQuickActionId.OpenVersionDirectory, "打开版本目录"),
            new HomeQuickAction(HomeQuickActionId.OpenLogs, "打开日志"),
        ],
        null,
        null);

    private LauncherPageViewModel CreatePage(string routeId) => pageFactories[routeId]();

    private void SelectRoute(string routeId)
    {
        foreach (NavigationItemViewModel item in NavigationItems)
        {
            item.IsSelected = string.Equals(item.RouteId, routeId, StringComparison.Ordinal);
        }
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(
        this,
        new PropertyChangedEventArgs(propertyName));

    private void OnboardingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnboardingViewModel.IsOpen))
        {
            IsOnboardingVisible = hasStartupState && Onboarding.IsOpen;
        }

        if (e.PropertyName is nameof(OnboardingViewModel.CanLaunch) or nameof(OnboardingViewModel.IsComplete))
        {
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(IsLaunchDisabled));
            if (Onboarding.IsComplete && Onboarding.CanLaunch && !Onboarding.IsDeferredSetup)
            {
                IsOnboardingVisible = false;
            }
        }
    }

    private void RepairPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepairPreviewViewModel.IsOpen) or nameof(RepairPreviewViewModel.Summary))
        {
            OnPropertyChanged(nameof(IsRepairPreviewOpen));
            OnPropertyChanged(nameof(RepairPreviewSummary));
        }
    }

    private static string BuildDataRootSummary(DataRootMode mode) => mode switch
    {
        DataRootMode.LocalToExecutable => "便携数据目录（由 lacertae.portable 标记决定，重启后生效）",
        DataRootMode.UserProfile => "Windows 用户数据目录",
        _ => "已选择的数据目录",
    };

    private sealed class DelegateCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class DisabledOnboardingUseCases : IOnboardingUseCases
    {
        public Task<Lacertae.Domain.Results.Result<Lacertae.Domain.GameRoots.GameRoot>> AddGameRootAsync(
            string path,
            bool allowEmpty,
            CancellationToken cancellationToken) =>
            Task.FromResult(Lacertae.Domain.Results.Result<Lacertae.Domain.GameRoots.GameRoot>.Failure(
                new Lacertae.Domain.Problems.Problem(
                    "ONBOARDING_ACTION_UNAVAILABLE",
                    Lacertae.Domain.Problems.ProblemStage.Configuration,
                    "problem.onboarding.unavailable",
                    false,
                    Guid.NewGuid().ToString("N"),
                    ["action.onboarding.review"])));

        public Task<Lacertae.Domain.Results.Result<Lacertae.Domain.Accounts.Account>> AddOfflineAccountAsync(
            string playerName,
            CancellationToken cancellationToken) =>
            Task.FromResult(Lacertae.Domain.Results.Result<Lacertae.Domain.Accounts.Account>.Failure(
                new Lacertae.Domain.Problems.Problem(
                    "ONBOARDING_ACTION_UNAVAILABLE",
                    Lacertae.Domain.Problems.ProblemStage.Configuration,
                    "problem.onboarding.unavailable",
                    false,
                    Guid.NewGuid().ToString("N"),
                    ["action.onboarding.review"])));

        public Task<Lacertae.Domain.Results.Result<OnboardingVersionSelection>> SelectVersionAsync(
            string versionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Lacertae.Domain.Results.Result<OnboardingVersionSelection>.Failure(
                new Lacertae.Domain.Problems.Problem(
                    "ONBOARDING_ACTION_UNAVAILABLE",
                    Lacertae.Domain.Problems.ProblemStage.Configuration,
                    "problem.onboarding.unavailable",
                    false,
                    Guid.NewGuid().ToString("N"),
                    ["action.onboarding.review"])));

        public Task<Lacertae.Domain.Results.Result<OnboardingJavaSelection>> SelectJavaAsync(
            string executablePath,
            int requiredMajor,
            CancellationToken cancellationToken) =>
            Task.FromResult(Lacertae.Domain.Results.Result<OnboardingJavaSelection>.Failure(
                new Lacertae.Domain.Problems.Problem(
                    "ONBOARDING_ACTION_UNAVAILABLE",
                    Lacertae.Domain.Problems.ProblemStage.Configuration,
                    "problem.onboarding.unavailable",
                    false,
                    Guid.NewGuid().ToString("N"),
                    ["action.onboarding.review"])));
    }
}
