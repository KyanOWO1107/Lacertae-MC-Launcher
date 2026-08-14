using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Lacertae.Application.Accessibility;
using Lacertae.Application.Accounts;
using Lacertae.Application.Downloads;
using Lacertae.Application.GameRoots;
using Lacertae.Application.Games;
using Lacertae.Application.Home;
using Lacertae.Application.Install;
using Lacertae.Application.Java;
using Lacertae.Application.Launch;
using Lacertae.Application.Operations;
using Lacertae.Application.Platform;
using Lacertae.Application.Resources;
using Lacertae.Application.Startup;
using Lacertae.Application.Storage;
using Lacertae.Application.SystemInfo;
using Lacertae.Application.Versions;
using Lacertae.Desktop.Services;
using Lacertae.Desktop.ViewModels;
using Lacertae.Desktop.ViewModels.Downloads;
using Lacertae.Desktop.ViewModels.Java;
using Lacertae.Desktop.ViewModels.Onboarding;
using Lacertae.Desktop.ViewModels.Resources;
using Lacertae.Desktop.ViewModels.Startup;
using Lacertae.Desktop.ViewModels.Tasks;
using Lacertae.Desktop.ViewModels.Versions;
using Lacertae.Desktop.Views;
using Lacertae.Desktop.Views.Startup;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Home;
using Lacertae.Domain.Install;
using Lacertae.Domain.Java;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Storage;
using Lacertae.Infrastructure.Accounts;
using Lacertae.Infrastructure.Downloads;
using Lacertae.Infrastructure.GameRoots;
using Lacertae.Infrastructure.Games;
using Lacertae.Infrastructure.Install;
using Lacertae.Infrastructure.Install.Mojang;
using Lacertae.Infrastructure.Java;
using Lacertae.Infrastructure.Operations;
using Lacertae.Infrastructure.Processes;
using Lacertae.Infrastructure.Settings;
using Lacertae.Infrastructure.Startup;
using Lacertae.Infrastructure.Storage;
using Lacertae.Infrastructure.Versions;
using Lacertae.Platform.Windows.Accessibility;
using Lacertae.Platform.Windows.Dialogs;
using Lacertae.Platform.Windows.Java;
using Lacertae.Platform.Windows.Storage;
using Lacertae.Platform.Windows.SystemInfo;
using Microsoft.Extensions.DependencyInjection;

namespace Lacertae.Desktop;

public sealed class CompositionRoot : IDisposable
{
    private readonly ServiceProvider services;
    private StartupState? startupState;
    private TasksViewModel? tasksViewModel;

    public CompositionRoot()
    {
        ServiceCollection registrations = new();
        registrations.AddSingleton<IMotionPreference, WindowsMotionPreference>();
        registrations.AddSingleton<ThemeService>(provider =>
            new ThemeService(provider.GetRequiredService<IMotionPreference>()));
        registrations.AddSingleton<IPlatformPaths, WindowsPlatformPaths>();
        registrations.AddSingleton<IFileSystem, SystemFileSystem>();
        registrations.AddSingleton<DataRootResolver>();
        registrations.AddSingleton<IStartupDataRootResolver>(provider => provider.GetRequiredService<DataRootResolver>());
        registrations.AddSingleton<IStartupLoggingInitializer, FileLoggingInitializer>();
        registrations.AddSingleton<IStartupStorageFactory, DurableStartupStorageFactory>();
        registrations.AddSingleton<IPlatformDialogService, WindowsPlatformDialogService>();
        registrations.AddSingleton<IJavaProbe>(_ => new JavaProbe(new SystemProcessRunner()));
        registrations.AddSingleton<IHomeLaunchPlanHost, DefaultHomeLaunchPlanHost>();
        registrations.AddSingleton<IBackgroundTaskStore>(provider =>
        {
            Result<DataRoot> dataRoot = provider.GetRequiredService<DataRootResolver>().Resolve();
            if (!dataRoot.IsSuccess)
            {
                throw new InvalidOperationException($"Cannot resolve the background-task database: {dataRoot.Problem?.Code}");
            }

            return new SqliteBackgroundTaskStore(new SqliteConnectionFactory(dataRoot.Value.DatabasePath));
        });
        registrations.AddSingleton<StartupCoordinator>();
        registrations.AddSingleton<MainWindowViewModel>();
        registrations.AddTransient<MainWindow>();
        services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async Task<Result<StartupState>> InitializeAsync(CancellationToken cancellationToken)
    {
        Result<StartupState> result = await services.GetRequiredService<StartupCoordinator>().InitializeAsync(cancellationToken);
        if (result.IsSuccess)
        {
            if (!IsValidSettings(result.Value.Settings))
            {
                return Result<StartupState>.Failure(CreateSettingsCorruptProblem());
            }

            startupState = result.Value;
            ApplyTheme(result.Value.Settings.Theme);
            StartupOnboardingUseCases onboardingUseCases = new(
                result.Value,
                services.GetRequiredService<IFileSystem>());
            OnboardingDurableState preflightState =
                await onboardingUseCases.ValidateStartupPreflightAsync(cancellationToken);
            services.GetRequiredService<MainWindowViewModel>().ApplyStartupState(
                result.Value,
                onboardingUseCases,
                preflightState);
            await ConfigureFeaturePagesAsync(result.Value, cancellationToken);
            await TryApplyHomeStateAsync(result.Value, cancellationToken);
        }

        return result;
    }

    private async Task ConfigureFeaturePagesAsync(
        StartupState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        SqliteConnectionFactory connectionFactory = new(state.DataRoot.DatabasePath);
        SqliteVersionOverrideRepository versionOverrides = new(connectionFactory);
        ListGameVersions listGameVersions = new(new CmlLibGameEngine(), versionOverrides);
        JsonSettingsRepository settingsRepository = new(state.DataRoot.SettingsPath);
        GameRoot? selectedRoot = state.GameRoots.FirstOrDefault(root =>
            string.Equals(root.Id, state.Settings.SelectedGameRootId, StringComparison.Ordinal));
        ListedGameVersion? selectedVersion = null;
        if (selectedRoot is not null && !string.IsNullOrWhiteSpace(state.Settings.SelectedVersionFolder))
        {
            try
            {
                Result<IReadOnlyList<ListedGameVersion>> listed = await listGameVersions.ExecuteAsync(
                    selectedRoot,
                    state.Settings,
                    cancellationToken);
                if (listed.IsSuccess)
                {
                    selectedVersion = listed.Value.FirstOrDefault(version =>
                        string.Equals(version.FolderName, state.Settings.SelectedVersionFolder, StringComparison.Ordinal));
                }
            }
            catch (IOException)
            {
                // The versions page will expose the typed load failure; Java
                // settings remain usable without a version-specific requirement.
            }
            catch (UnauthorizedAccessException)
            {
                // The versions page will expose the typed load failure; Java
                // settings remain usable without a version-specific requirement.
            }
            catch (InvalidOperationException)
            {
                // The versions page will expose the typed load failure; Java
                // settings remain usable without a version-specific requirement.
            }
        }
        VersionsViewModel versions = new(
            new SqliteGameRootRepository(connectionFactory),
            listGameVersions,
            state.Settings,
            services.GetRequiredService<IPlatformDialogService>(),
            settingsRepository);

        RenameVersionFolder renameVersionFolder = new(
            versionOverrides,
            new JsonVersionRenameJournal(Path.Combine(state.DataRoot.LocalPath, "version-rename.journal.json")));
        SaveVersionOverride saveVersionOverride = new(versionOverrides);
        IJavaProbe javaProbe = services.GetRequiredService<IJavaProbe>();

        MojangVanillaMetadataSource metadataSource = new(new MojangVanillaMetadataSourceOptions());
        PlanVanillaInstall planner = new(metadataSource);
        HttpArtifactDownloader artifactDownloader = new(
            new DownloadSourceSelector([new OfficialDownloadSource()]));
        ExecuteVanillaInstall executor = new(
            artifactDownloader,
            new StreamingGameFileVerifier(),
            new SqliteInstallJournalRepository(connectionFactory));
        BackgroundOperationRunner operationRunner = new();
        ConcurrentDictionary<string, CancellationTokenSource> operationCancellation = new(StringComparer.Ordinal);
        ConcurrentDictionary<string, (GameRoot Root, string VersionId)> operationRequests = new(StringComparer.Ordinal);
        TasksViewModel? taskPage = null;
        VanillaDownloadsViewModel downloads = new(
            metadataSource,
            (root, versionId, cancellationToken) => planner.ExecuteAsync(
                root,
                versionId,
                InstallAction.Install,
                VanillaPlatform.WindowsX64,
                cancellationToken),
            StartVanillaInstallAsync,
            state.GameRoots,
            selectedRoot,
            state.Settings,
            settingsRepository);

        MainWindowViewModel mainWindow = services.GetRequiredService<MainWindowViewModel>();
        JavaArchitecture preferredArchitecture = Environment.Is64BitOperatingSystem
            ? JavaArchitecture.X64
            : JavaArchitecture.X86;
        Func<CancellationToken, Task<Result<JavaInstallation>>>? installManagedJava = null;
        if (selectedVersion is not null)
        {
            InstallManagedJava managedJavaInstaller = new(
                new MojangJavaRuntimeCatalog(new MojangJavaRuntimeCatalogOptions()),
                artifactDownloader,
                javaProbe);
            installManagedJava = installCancellationToken => managedJavaInstaller.ExecuteAsync(
                state.DataRoot,
                selectedVersion.Java.Component,
                preferredArchitecture,
                null,
                installCancellationToken);
        }

        async Task<Result<Unit>> SaveGlobalJavaPathAsync(
            string? executablePath,
            CancellationToken saveCancellationToken)
        {
            Result<LauncherSettings> current = await settingsRepository.LoadAsync(saveCancellationToken);
            if (!current.IsSuccess)
            {
                return Result<Unit>.Failure(current.Problem!);
            }

            return await settingsRepository.SaveAsync(
                current.Value with { GlobalJavaPath = executablePath },
                saveCancellationToken);
        }

        mainWindow.ConfigureJavaSettings(new JavaSettingsViewModel(
            new JavaDiscoveryResult([], []),
            selectedVersion?.Java.MajorVersion,
            preferredArchitecture,
            javaProbe,
            SaveGlobalJavaPathAsync,
            installManagedJava));
        if (OperatingSystem.IsWindows())
        {
            _ = RestoreJavaSelectionAsync();
        }
        taskPage = new TasksViewModel(
            taskStore: services.GetRequiredService<IBackgroundTaskStore>(),
            retry: RetryTaskAsync,
            cancel: CancelTaskAsync);
        tasksViewModel = taskPage;
        mainWindow.ConfigureFeaturePages(
            versions,
            downloads,
            row => new VersionSettingsViewModel(
                row,
                saveVersionOverride,
                renameVersionFolder,
                javaProbe,
                services.GetRequiredService<IBackgroundTaskStore>(),
                string.IsNullOrWhiteSpace(row.Version.JavaPath)
                    ? preferredArchitecture
                    : null),
            taskPage,
            selectedRoot is not null && selectedVersion is not null
                ? new LocalResourcesViewModel(
                    selectedRoot.NormalizedPath,
                    selectedVersion.IsolationDecision.IsIsolated
                        ? Path.Combine(selectedRoot.NormalizedPath, "versions", selectedVersion.FolderName)
                        : null,
                    services.GetRequiredService<IPlatformDialogService>(),
                    new ResolveLocalResourceFolders(services.GetRequiredService<IFileSystem>()))
                : null);

        async Task RestoreJavaSelectionAsync()
        {
            IJavaDiscovery discovery = CreateJavaDiscovery(
                services.GetRequiredService<IFileSystem>(),
                javaProbe);
            await mainWindow.JavaSettings.RefreshAsync(discovery, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(state.Settings.GlobalJavaPath))
            {
                mainWindow.JavaSettings.ManualPathText = state.Settings.GlobalJavaPath;
                await mainWindow.JavaSettings.UseManualPathAsync(CancellationToken.None);
            }
        }

        async Task<Result<Unit>> StartVanillaInstallAsync(
            GameRoot root,
            string versionId,
            CancellationToken cancellationToken)
        {
            if (root.Availability != GameRootAvailability.Available ||
                string.IsNullOrWhiteSpace(versionId))
            {
                return Result.Failure(new Problem(
                    "VERSION_INSTALL_START_INVALID",
                    ProblemStage.Installation,
                    "problem.install.invalid_request",
                    false,
                    Guid.NewGuid().ToString("N"),
                    ["action.install.review"]));
            }

            InstallVanillaOperation operation = new(
                planner,
                executor,
                root,
                versionId,
                VanillaPlatform.WindowsX64,
                InstallAction.Install,
                services.GetRequiredService<IBackgroundTaskStore>());
            CancellationTokenSource operationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!operationCancellation.TryAdd(operation.Id, operationToken))
            {
                operationToken.Dispose();
                return Result.Failure(new Problem(
                    "VERSION_INSTALL_START_FAILED",
                    ProblemStage.Installation,
                    "problem.install.start_failed",
                    true,
                    operation.Id,
                    ["action.install.retry"]));
            }

            operationRequests[operation.Id] = (root, versionId);
            _ = RunOperationAsync(operation, operationToken);
            return Result.Success();
        }

        async Task RunOperationAsync(
            InstallVanillaOperation operation,
            CancellationTokenSource operationToken)
        {
            try
            {
                Result<Unit> result = await operationRunner.RunAsync(
                    operation,
                    new Progress<OperationSnapshot>(snapshot => taskPage?.AcceptSnapshot(snapshot)),
                    operationToken.Token);
                if (result.IsSuccess)
                {
                    operationRequests.TryRemove(operation.Id, out _);
                }
            }
            catch (Exception)
            {
                taskPage?.AcceptSnapshot(new OperationSnapshot(
                    operation.Id,
                    operation.Kind,
                    OperationState.Failed,
                    null,
                    "BACKGROUND_OPERATION_FAILED"));
            }
            finally
            {
                operationCancellation.TryRemove(operation.Id, out _);
                operationToken.Dispose();
            }
        }

        async Task RetryTaskAsync(TaskItemViewModel item)
        {
            if (!operationRequests.TryGetValue(item.Id, out (GameRoot Root, string VersionId) request))
            {
                return;
            }

            Result<Unit> result = await StartVanillaInstallAsync(request.Root, request.VersionId, CancellationToken.None);
            if (result.IsSuccess)
            {
                operationRequests.TryRemove(item.Id, out _);
            }
        }

        Task CancelTaskAsync(TaskItemViewModel item)
        {
            if (operationCancellation.TryGetValue(item.Id, out CancellationTokenSource? cancellation))
            {
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        }
    }

    private static DiscoverJavaInstallations CreateJavaDiscovery(
        IFileSystem fileSystem,
        IJavaProbe javaProbe)
    {
        List<IJavaCandidateSource> sources =
        [
            new PathJavaCandidateSource(Environment.GetEnvironmentVariable("PATH") ?? string.Empty, fileSystem),
            new CommonDirectoryJavaCandidateSource(
                [
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                ],
                fileSystem),
        ];
        if (OperatingSystem.IsWindows())
        {
            sources.Add(new RegistryJavaCandidateSource(new WindowsRegistryJavaReader(), fileSystem));
        }
        return new DiscoverJavaInstallations(
            sources,
            javaProbe,
            fileSystem,
            new WindowsPathComparer());
    }

    public void ApplyTheme(ThemeMode theme, bool reduceMotion = false) =>
        services.GetRequiredService<ThemeService>().Apply(theme, reduceMotion);

    public static Problem CreateStartupFailureProblem() => new(
        "STARTUP_FAILED",
        ProblemStage.Unknown,
        "problem.startup.failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.startup.retry"],
        SafeStartupContext());

    public MainWindow CreateMainWindow()
    {
        if (startupState is null)
        {
            throw new InvalidOperationException("Startup must succeed before creating the main window.");
        }

        return services.GetRequiredService<MainWindow>();
    }

    public static Window CreateRecoveryWindow(
        Problem problem,
        IStartupRecoveryHost? recoveryHost = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new Window
        {
            Title = "Lacertae - 启动恢复",
            Width = 620,
            Height = 420,
            MinWidth = 520,
            MinHeight = 320,
            Content = new StartupView
            {
                DataContext = new StartupViewModel(problem, recoveryHost: recoveryHost),
            },
        };
    }

    public void Dispose()
    {
        tasksViewModel?.Dispose();
        services.Dispose();
    }

    private static bool IsValidSettings(LauncherSettings settings) =>
        Enum.IsDefined(settings.Theme) &&
        Enum.IsDefined(settings.IsolationPolicy) &&
        HomeModulePlacement.IsValid(settings.HomeModules);

    private async Task TryApplyHomeStateAsync(
        StartupState state,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SqliteConnectionFactory connectionFactory = new(state.DataRoot.DatabasePath);
            IFileSystem fileSystem = services.GetRequiredService<IFileSystem>();
            IReadOnlyList<IJavaCandidateSource> sources =
            [
                new PathJavaCandidateSource(Environment.GetEnvironmentVariable("PATH") ?? string.Empty, fileSystem),
                new CommonDirectoryJavaCandidateSource(
                    [
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    ],
                    fileSystem),
                new RegistryJavaCandidateSource(new WindowsRegistryJavaReader(), fileSystem),
            ];
            DiscoverJavaInstallations discovery = new(
                sources,
                new JavaProbe(new SystemProcessRunner()),
                fileSystem,
                new WindowsPathComparer());
            BuildHomeState buildHomeState = new(
                new SqliteGameRootRepository(connectionFactory),
                new SqliteAccountRepository(connectionFactory),
                new ListGameVersions(
                    new CmlLibGameEngine(),
                    new SqliteVersionOverrideRepository(connectionFactory)),
                discovery,
                new WindowsMemoryInfo());
            IReadOnlyList<OperationSnapshot> activeTasks = [];
            Result<IReadOnlyList<OperationSnapshot>> activeTaskResult = await services
                .GetRequiredService<IBackgroundTaskStore>()
                .GetActiveAsync(cancellationToken);
            bool activeTasksReadFailed = !activeTaskResult.IsSuccess;
            if (activeTaskResult.IsSuccess)
            {
                activeTasks = activeTaskResult.Value;
            }
            Result<HomeState> home = await buildHomeState.ExecuteAsync(
                state.Settings,
                activeTasks,
                hasDamagedFiles: false,
                activeTasksReadFailed: activeTasksReadFailed,
                cancellationToken: cancellationToken);
            if (home.IsSuccess && HasDamagedHomeFiles(home.Value, fileSystem))
            {
                home = await buildHomeState.ExecuteAsync(
                    state.Settings,
                    activeTasks,
                    hasDamagedFiles: true,
                    activeTasksReadFailed: activeTasksReadFailed,
                    cancellationToken: cancellationToken);
            }
            if (home.IsSuccess)
            {
                services.GetRequiredService<MainWindowViewModel>().ApplyHomeState(home.Value);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Startup remains usable with the safe disabled home state. The
            // structured startup log records infrastructure diagnostics.
        }
    }

    private static bool HasDamagedHomeFiles(HomeState state, IFileSystem fileSystem)
    {
        if (state.LaunchContext is not HomeLaunchContext context)
        {
            return false;
        }

        try
        {
            string root = context.GameRoot.NormalizedPath;
            string versionDirectory = Path.Combine(root, "versions", context.Version.FolderName);
            return !fileSystem.DirectoryExists(Path.Combine(root, "assets")) ||
                !fileSystem.DirectoryExists(Path.Combine(root, "libraries")) ||
                !fileSystem.DirectoryExists(versionDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static Problem CreateSettingsCorruptProblem() => new(
        "SETTINGS_CORRUPT",
        ProblemStage.Configuration,
        "problem.settings.invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.settings.restore_backup"],
        SafeStartupContext());

    private static Dictionary<string, string> SafeStartupContext() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["safePath"] = "logs/lacertae-*.log",
        };

    private sealed class StartupOnboardingUseCases : IOnboardingUseCases
    {
        private readonly AddGameRoot addGameRoot;
        private readonly AddOfflineAccount addOfflineAccount;
        private readonly SqliteAccountRepository accountRepository;

        private readonly ListGameVersions listGameVersions;
        private GameRoot? selectedRoot;
        private readonly JavaProbe javaProbe;
        private readonly JsonSettingsRepository settingsRepository;
        private readonly StartupState startupState;
        private LauncherSettings settings;

        public StartupOnboardingUseCases(StartupState startupState, IFileSystem fileSystem)
        {
            ArgumentNullException.ThrowIfNull(startupState);
            ArgumentNullException.ThrowIfNull(fileSystem);
            this.startupState = startupState;
            settings = startupState.Settings;
            SqliteConnectionFactory connectionFactory = new(startupState.DataRoot.DatabasePath);
            settingsRepository = new JsonSettingsRepository(startupState.DataRoot.SettingsPath);
            addGameRoot = new AddGameRoot(new SqliteGameRootRepository(connectionFactory), fileSystem);
            accountRepository = new SqliteAccountRepository(connectionFactory);
            addOfflineAccount = new AddOfflineAccount(accountRepository);
            selectedRoot = startupState.GameRoots.FirstOrDefault(root =>
                string.Equals(root.Id, startupState.Settings.SelectedGameRootId, StringComparison.Ordinal)) ??
                (startupState.GameRoots.Count > 0 ? startupState.GameRoots[0] : null);
            listGameVersions = new ListGameVersions(
                new CmlLibGameEngine(),
                new SqliteVersionOverrideRepository(connectionFactory));
            javaProbe = new JavaProbe(new SystemProcessRunner());
        }

        public async Task<OnboardingDurableState> ValidateStartupPreflightAsync(
            CancellationToken cancellationToken)
        {
            string? gameRootId = null;
            string? accountId = null;
            string? versionId = null;
            string? javaPath = null;
            int? requiredJavaMajor = null;
            bool versionIsInstalled = false;
            bool hasCompatibleJava = false;

            GameRoot? root = startupState.GameRoots.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, settings.SelectedGameRootId, StringComparison.Ordinal) &&
                candidate.Availability == GameRootAvailability.Available);
            if (root is not null)
            {
                gameRootId = root.Id;
            }

            if (!string.IsNullOrWhiteSpace(settings.DefaultAccountId))
            {
                try
                {
                    Account? account = await accountRepository.GetAsync(
                        settings.DefaultAccountId,
                        cancellationToken);
                    if (account is not null && account.Status == AccountStatus.Active)
                    {
                        accountId = account.Id;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A stale account must keep startup usable so onboarding can repair it.
                }
            }

            ListedGameVersion? selectedVersion = null;
            if (root is not null && !string.IsNullOrWhiteSpace(settings.SelectedVersionFolder))
            {
                try
                {
                    Result<IReadOnlyList<ListedGameVersion>> listed = await listGameVersions.ExecuteAsync(
                        root,
                        settings,
                        cancellationToken);
                    if (listed.IsSuccess)
                    {
                        selectedVersion = listed.Value.FirstOrDefault(version =>
                            string.Equals(
                                version.FolderName,
                                settings.SelectedVersionFolder,
                                StringComparison.Ordinal));
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A missing or malformed local version is an onboarding state, not startup failure.
                }
            }

            if (selectedVersion is not null)
            {
                versionId = selectedVersion.FolderName;
                requiredJavaMajor = selectedVersion.Java.MajorVersion;
                versionIsInstalled = true;
            }

            if (versionIsInstalled &&
                requiredJavaMajor is int requiredMajor &&
                !string.IsNullOrWhiteSpace(settings.GlobalJavaPath))
            {
                try
                {
                    Result<JavaInstallation> probe = await javaProbe.ProbeAsync(
                        settings.GlobalJavaPath,
                        JavaSource.Manual,
                        false,
                        cancellationToken);
                    if (probe.IsSuccess && probe.Value.MajorVersion == requiredMajor)
                    {
                        javaPath = probe.Value.ExecutablePath;
                        hasCompatibleJava = true;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A missing Java executable is repaired in the onboarding flow.
                }
            }

            return new OnboardingDurableState(
                gameRootId,
                accountId,
                versionId,
                javaPath,
                requiredJavaMajor,
                versionIsInstalled,
                hasCompatibleJava,
                IsDeferredSetup: false);
        }

        public async Task<Result<Lacertae.Domain.GameRoots.GameRoot>> AddGameRootAsync(
            string path,
            bool allowEmpty,
            CancellationToken cancellationToken)
        {
            Result<Lacertae.Domain.GameRoots.GameRoot> result = await addGameRoot.ExecuteAsync(
                path,
                allowEmpty,
                cancellationToken);
            if (result.IsSuccess)
            {
                selectedRoot = result.Value;
                Result<Unit> saved = await SaveSettingsAsync(
                    settings with { SelectedGameRootId = result.Value.Id },
                    cancellationToken);
                if (!saved.IsSuccess)
                {
                    return Result<Lacertae.Domain.GameRoots.GameRoot>.Failure(saved.Problem!);
                }
            }

            return result;
        }

        public async Task<Result<Lacertae.Domain.Accounts.Account>> AddOfflineAccountAsync(
            string playerName,
            CancellationToken cancellationToken)
        {
            Result<Lacertae.Domain.Accounts.Account> result = await addOfflineAccount.ExecuteAsync(
                playerName,
                cancellationToken);
            if (result.IsSuccess)
            {
                Result<Unit> saved = await SaveSettingsAsync(
                    settings with { DefaultAccountId = result.Value.Id },
                    cancellationToken);
                if (!saved.IsSuccess)
                {
                    return Result<Lacertae.Domain.Accounts.Account>.Failure(saved.Problem!);
                }
            }

            return result;
        }

        public async Task<Result<OnboardingVersionSelection>> SelectVersionAsync(
            string versionId,
            CancellationToken cancellationToken) =>
            selectedRoot is null
                ? Result<OnboardingVersionSelection>.Failure(Unsupported("VERSION_ROOT_REQUIRED"))
                : await SelectVersionFromRootAsync(versionId, cancellationToken);

        public async Task<Result<OnboardingJavaSelection>> SelectJavaAsync(
            string executablePath,
            int requiredMajor,
            CancellationToken cancellationToken)
        {
            Lacertae.Domain.Results.Result<Lacertae.Domain.Java.JavaInstallation> probe = await javaProbe.ProbeAsync(
                executablePath,
                Lacertae.Domain.Java.JavaSource.Manual,
                false,
                cancellationToken);
            if (!probe.IsSuccess)
            {
                return Result<OnboardingJavaSelection>.Failure(probe.Problem!);
            }

            bool compatible = probe.Value.MajorVersion == requiredMajor;
            if (!compatible)
            {
                return Result<OnboardingJavaSelection>.Success(new OnboardingJavaSelection(
                    probe.Value.ExecutablePath,
                    probe.Value.MajorVersion,
                    false));
            }

            Result<Unit> saved = await SaveSettingsAsync(
                settings with { GlobalJavaPath = probe.Value.ExecutablePath },
                cancellationToken);
            return !saved.IsSuccess
                ? Result<OnboardingJavaSelection>.Failure(saved.Problem!)
                : Result<OnboardingJavaSelection>.Success(new OnboardingJavaSelection(
                    probe.Value.ExecutablePath,
                    probe.Value.MajorVersion,
                    true));
        }

        private async Task<Result<OnboardingVersionSelection>> SelectVersionFromRootAsync(
            string versionId,
            CancellationToken cancellationToken)
        {
            Result<IReadOnlyList<ListedGameVersion>> listed = await listGameVersions.ExecuteAsync(
                selectedRoot!,
                settings,
                cancellationToken);
            if (!listed.IsSuccess)
            {
                return Result<OnboardingVersionSelection>.Failure(listed.Problem!);
            }

            ListedGameVersion? selected = listed.Value.FirstOrDefault(version =>
                string.Equals(version.FolderName, versionId, StringComparison.Ordinal));
            if (selected is null)
            {
                return Result<OnboardingVersionSelection>.Failure(Unsupported("VERSION_NOT_INSTALLED"));
            }

            Result<Unit> saved = await SaveSettingsAsync(
                settings with { SelectedVersionFolder = selected.FolderName },
                cancellationToken);
            return !saved.IsSuccess
                ? Result<OnboardingVersionSelection>.Failure(saved.Problem!)
                : Result<OnboardingVersionSelection>.Success(new OnboardingVersionSelection(
                    selected.FolderName,
                    selected.Java.MajorVersion,
                    true));
        }

        private async Task<Result<Unit>> SaveSettingsAsync(
            LauncherSettings next,
            CancellationToken cancellationToken)
        {
            Result<Unit> saved = await settingsRepository.SaveAsync(next, cancellationToken);
            if (saved.IsSuccess)
            {
                settings = next;
            }

            return saved;
        }

        private static Problem Unsupported(string code) => new(
            code,
            ProblemStage.Configuration,
            "problem.onboarding.step_unavailable",
            false,
            Guid.NewGuid().ToString("N"),
            ["action.onboarding.review"]);
    }
}
