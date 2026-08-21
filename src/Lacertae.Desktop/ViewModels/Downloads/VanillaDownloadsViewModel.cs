using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Lacertae.Application.GameRoots;
using Lacertae.Application.Install;
using Lacertae.Application.Settings;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Install;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;

namespace Lacertae.Desktop.ViewModels.Downloads;

public enum VanillaVersionTypeFilter
{
    All,
    Release,
    Snapshot,
}

public sealed record VanillaVersionItem(
    string Id,
    string Type,
    DateTimeOffset ReleaseTime,
    Uri MetadataUri,
    string MetadataSha1)
{
    public string SourceLabel => IsOfficialMetadataHost(MetadataUri.Host)
        ? "official"
        : "unknown";

    public string SourceDisplayLabel => SourceLabel == "official" ? "官方源" : "未知源";

    public string TypeLabel => Type.ToLowerInvariant() switch
    {
        "release" => "正式版",
        "snapshot" => "快照版",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type,
    };

    public string ReleaseTimeLabel => ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool IsOfficialMetadataHost(string host) =>
        host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase);
}

public sealed class VanillaDownloadsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IVanillaVersionCatalog catalog;
    private readonly Func<GameRoot, string, CancellationToken, Task<Result<VanillaInstallPlan>>>? planInstall;
    private readonly Func<GameRoot, string, CancellationToken, Task<Result<Unit>>>? startInstall;
    private readonly AsyncCommand confirmInstallCommand;
    private readonly AsyncCommand prepareInstallCommand;
    private readonly AsyncCommand refreshCommand;
    private readonly DelegateCommand cancelInstallCommand;
    private readonly ISettingsRepository? settingsRepository;
    private readonly IGameRootRepository? gameRootRepository;
    private readonly SemaphoreSlim settingsSaveGate = new(1, 1);
    private LauncherSettings? settings;
    private IReadOnlyList<GameRoot> gameRoots = [];
    private IReadOnlyList<VanillaVersionItem> versions = [];
    private IReadOnlyList<VanillaVersionItem> filteredVersions = [];
    private string searchText = string.Empty;
    private VanillaVersionTypeFilter typeFilter = VanillaVersionTypeFilter.All;
    private VanillaVersionItem? selectedVersion;
    private GameRoot? selectedRoot;
    private VanillaInstallPlan? installPlan;
    private bool isLoading;
    private bool isInstallConfirmationOpen;
    private long selectionPersistenceGeneration;
    private string? errorCode;
    private string? errorMessage;

    public VanillaDownloadsViewModel(
        IVanillaVersionCatalog catalog,
        Func<GameRoot, string, CancellationToken, Task<Result<VanillaInstallPlan>>>? plan = null,
        Func<GameRoot, string, CancellationToken, Task<Result<Unit>>>? start = null,
        IReadOnlyList<GameRoot>? gameRoots = null,
        GameRoot? selectedRoot = null,
        LauncherSettings? settings = null,
        ISettingsRepository? settingsRepository = null,
        IGameRootRepository? gameRootRepository = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        planInstall = plan;
        startInstall = start;
        this.gameRoots = (gameRoots ?? []).ToArray();
        this.selectedRoot = selectedRoot ?? this.gameRoots.FirstOrDefault(
            static root => root.Availability == GameRootAvailability.Available);
        if (settingsRepository is not null && settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        this.settings = settings;
        this.settingsRepository = settingsRepository;
        this.gameRootRepository = gameRootRepository;
        refreshCommand = new AsyncCommand(
            () => LoadAsync(CancellationToken.None),
            () => !IsLoading);
        confirmInstallCommand = new AsyncCommand(
            ConfirmInstallAsync,
            () => IsInstallConfirmationOpen && installPlan is not null && startInstall is not null);
        prepareInstallCommand = new AsyncCommand(
            () => PrepareInstallAsync(selectedRoot!, CancellationToken.None),
            () => selectedRoot is not null && selectedVersion is not null && planInstall is not null);
        cancelInstallCommand = new DelegateCommand(CancelInstall, () => IsInstallConfirmationOpen);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<VanillaVersionItem> Versions => versions;

    public IReadOnlyList<VanillaVersionItem> FilteredVersions => filteredVersions;

    public IReadOnlyList<GameRoot> GameRoots => gameRoots;

    public string SearchText
    {
        get => searchText;
        set
        {
            string normalized = value ?? string.Empty;
            if (string.Equals(searchText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            searchText = normalized;
            OnPropertyChanged(nameof(SearchText));
            RebuildFilteredVersions();
        }
    }

    public VanillaVersionTypeFilter TypeFilter
    {
        get => typeFilter;
        set
        {
            if (typeFilter == value)
            {
                return;
            }

            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown vanilla version filter.");
            }

            typeFilter = value;
            OnPropertyChanged(nameof(TypeFilter));
            OnPropertyChanged(nameof(TypeFilterIndex));
            RebuildFilteredVersions();
        }
    }

    public int TypeFilterIndex
    {
        get => (int)TypeFilter;
        set
        {
            if (value is < 0 or > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown vanilla version filter.");
            }

            TypeFilter = (VanillaVersionTypeFilter)value;
        }
    }

    public VanillaVersionItem? SelectedVersion
    {
        get => selectedVersion;
        set
        {
            if (ReferenceEquals(selectedVersion, value))
            {
                return;
            }

            selectedVersion = value;
            installPlan = null;
            isInstallConfirmationOpen = false;
            OnPropertyChanged(nameof(SelectedVersion));
            OnPropertyChanged(nameof(IsInstallConfirmationOpen));
            OnPropertyChanged(nameof(InstallSummary));
            OnPropertyChanged(nameof(CanPrepareInstall));
            NotifyCommandState();
        }
    }

    public GameRoot? SelectedRoot
    {
        get => selectedRoot;
        set
        {
            if (ReferenceEquals(selectedRoot, value))
            {
                return;
            }

            selectedRoot = value;
            installPlan = null;
            isInstallConfirmationOpen = false;
            OnPropertyChanged(nameof(SelectedRoot));
            OnPropertyChanged(nameof(IsInstallConfirmationOpen));
            OnPropertyChanged(nameof(InstallSummary));
            OnPropertyChanged(nameof(CanPrepareInstall));
            NotifyCommandState();
            _ = PersistSelectedRootAsync(value);
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (isLoading == value)
            {
                return;
            }

            isLoading = value;
            OnPropertyChanged(nameof(IsLoading));
            refreshCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorCode);

    public string? ErrorCode
    {
        get => errorCode;
        private set
        {
            if (string.Equals(errorCode, value, StringComparison.Ordinal))
            {
                return;
            }

            errorCode = value;
            OnPropertyChanged(nameof(ErrorCode));
            OnPropertyChanged(nameof(HasError));
        }
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (string.Equals(errorMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            errorMessage = value;
            OnPropertyChanged(nameof(ErrorMessage));
        }
    }

    public bool IsInstallConfirmationOpen => isInstallConfirmationOpen;

    public bool CanPrepareInstall => selectedRoot is not null && selectedVersion is not null && planInstall is not null;

    public string InstallSummary => installPlan is null
        ? "请选择版本并生成安装预览。"
        : $"{installPlan.RequiredDownloadBytes.ToString(CultureInfo.InvariantCulture)} B · 官方源";

    public string ConfirmationUnavailableReason => planInstall is null || startInstall is null
        ? "安装服务尚未就绪。"
        : "生成预览后才能确认安装。";

    public ICommand ConfirmInstallCommand => confirmInstallCommand;

    public ICommand PrepareInstallCommand => prepareInstallCommand;

    public ICommand CancelInstallCommand => cancelInstallCommand;

    public ICommand RefreshCommand => refreshCommand;

    public void Dispose() => settingsSaveGate.Dispose();

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorCode = null;
        ErrorMessage = null;
        try
        {
            Result<Unit> selectionState = await ReloadSelectionStateAsync(cancellationToken);
            if (!selectionState.IsSuccess)
            {
                SetError(
                    selectionState.Problem?.Code ?? "GAME_ROOT_SETTINGS_LOAD_FAILED",
                    "无法读取游戏根目录设置，请重试。");
                return;
            }

            Result<IReadOnlyList<VanillaVersionSummary>> result = await catalog.ListAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                versions = [];
                OnPropertyChanged(nameof(Versions));
                SetError(result.Problem?.Code ?? "VERSION_METADATA_UNAVAILABLE", "无法读取官方原版版本清单，请稍后重试。");
                return;
            }

            versions = result.Value
                .Select(static version => new VanillaVersionItem(
                    version.Id,
                    version.Type,
                    version.ReleaseTime,
                    version.MetadataUri,
                    version.MetadataSha1))
                .OrderByDescending(static version => string.Equals(version.Type, "release", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(static version => version.ReleaseTime)
                .ThenBy(static version => version.Id, StringComparer.Ordinal)
                .ToArray();
            OnPropertyChanged(nameof(Versions));
            RebuildFilteredVersions();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            versions = [];
            OnPropertyChanged(nameof(Versions));
            SetError("VERSION_METADATA_UNAVAILABLE", "无法读取官方原版版本清单，请稍后重试。");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<Result<Unit>> ReloadSelectionStateAsync(CancellationToken cancellationToken)
    {
        if (settingsRepository is not null)
        {
            Result<LauncherSettings> loadedSettings = await settingsRepository.LoadAsync(cancellationToken);
            if (!loadedSettings.IsSuccess)
            {
                return Result<Unit>.Failure(loadedSettings.Problem!);
            }

            settings = loadedSettings.Value;
        }

        if (gameRootRepository is null)
        {
            return Result.Success();
        }

        gameRoots = (await gameRootRepository.GetAllAsync(cancellationToken))
            .OrderBy(static root => root.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static root => root.Id, StringComparer.Ordinal)
            .ToArray();
        OnPropertyChanged(nameof(GameRoots));

        GameRoot? configured = settings?.SelectedGameRootId is string selectedId
            ? gameRoots.FirstOrDefault(root => string.Equals(root.Id, selectedId, StringComparison.Ordinal))
            : null;
        GameRoot? next = configured ?? gameRoots.FirstOrDefault(static root => root.Availability == GameRootAvailability.Available);
        if (!ReferenceEquals(selectedRoot, next))
        {
            selectedRoot = next;
            installPlan = null;
            isInstallConfirmationOpen = false;
            OnPropertyChanged(nameof(SelectedRoot));
            OnPropertyChanged(nameof(IsInstallConfirmationOpen));
            OnPropertyChanged(nameof(InstallSummary));
            OnPropertyChanged(nameof(CanPrepareInstall));
            NotifyCommandState();
        }

        return Result.Success();
    }

    public async Task PrepareInstallAsync(GameRoot root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        SelectedRoot = root;
        installPlan = null;
        isInstallConfirmationOpen = false;
        OnPropertyChanged(nameof(IsInstallConfirmationOpen));
        OnPropertyChanged(nameof(InstallSummary));
        NotifyCommandState();
        if (root.Availability != GameRootAvailability.Available || selectedVersion is null || planInstall is null)
        {
            SetError("VERSION_INSTALL_PREVIEW_UNAVAILABLE", "请选择可用的游戏根目录和版本后再生成预览。");
            return;
        }

        Result<VanillaInstallPlan> result = await planInstall(
            root,
            selectedVersion.Id,
            cancellationToken);
        if (!result.IsSuccess)
        {
            SetError(result.Problem?.Code ?? "VERSION_INSTALL_PREVIEW_UNAVAILABLE", "无法生成安装预览，请检查版本和存储空间。");
            return;
        }

        installPlan = result.Value;
        isInstallConfirmationOpen = true;
        ErrorCode = null;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsInstallConfirmationOpen));
        OnPropertyChanged(nameof(InstallSummary));
        NotifyCommandState();
    }

    private async Task ConfirmInstallAsync()
    {
        if (!isInstallConfirmationOpen || installPlan is null || selectedRoot is null || startInstall is null)
        {
            return;
        }

        Result<Unit> result = await startInstall(
            selectedRoot,
            installPlan.VersionId,
            CancellationToken.None);
        if (!result.IsSuccess)
        {
            SetError(result.Problem?.Code ?? "VERSION_INSTALL_FAILED", "安装任务未能启动，请稍后重试。");
            return;
        }

        CancelInstall();
    }

    private void CancelInstall()
    {
        installPlan = null;
        isInstallConfirmationOpen = false;
        OnPropertyChanged(nameof(IsInstallConfirmationOpen));
        OnPropertyChanged(nameof(InstallSummary));
        NotifyCommandState();
    }

    private void RebuildFilteredVersions()
    {
        IEnumerable<VanillaVersionItem> query = versions;
        query = typeFilter switch
        {
            VanillaVersionTypeFilter.Release => query.Where(static version => string.Equals(version.Type, "release", StringComparison.OrdinalIgnoreCase)),
            VanillaVersionTypeFilter.Snapshot => query.Where(static version => string.Equals(version.Type, "snapshot", StringComparison.OrdinalIgnoreCase)),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query = query.Where(version =>
                version.Id.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                version.Type.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        filteredVersions = query.ToArray();
        OnPropertyChanged(nameof(FilteredVersions));
    }

    private void SetError(string code, string message)
    {
        ErrorCode = code;
        ErrorMessage = message;
    }

    private async Task PersistSelectedRootAsync(GameRoot? root)
    {
        long generation = Interlocked.Increment(ref selectionPersistenceGeneration);
        if (settingsRepository is null || settings is null)
        {
            if (settings is not null && generation == Volatile.Read(ref selectionPersistenceGeneration))
            {
                settings = settings with { SelectedGameRootId = root?.Id };
            }

            return;
        }

        bool entered = false;
        try
        {
            await settingsSaveGate.WaitAsync(CancellationToken.None);
            entered = true;
            if (generation != Volatile.Read(ref selectionPersistenceGeneration))
            {
                return;
            }

            LauncherSettings next = settings with { SelectedGameRootId = root?.Id };
            Result<Unit> saved = await settingsRepository.SaveAsync(next, CancellationToken.None);
            if (saved.IsSuccess && generation == Volatile.Read(ref selectionPersistenceGeneration))
            {
                settings = next;
                return;
            }

            if (!saved.IsSuccess && generation == Volatile.Read(ref selectionPersistenceGeneration))
            {
                SetError(
                    "GAME_ROOT_SETTINGS_SAVE_FAILED",
                    "安装目标已切换，但未能保存选择；重启后可能恢复原设置。");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (generation == Volatile.Read(ref selectionPersistenceGeneration))
            {
                SetError(
                    "GAME_ROOT_SETTINGS_SAVE_FAILED",
                    "安装目标已切换，但未能保存选择；重启后可能恢复原设置。");
            }
        }
        finally
        {
            if (entered)
            {
                settingsSaveGate.Release();
            }
        }
    }

    private void NotifyCommandState()
    {
        confirmInstallCommand.RaiseCanExecuteChanged();
        prepareInstallCommand.RaiseCanExecuteChanged();
        cancelInstallCommand.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(
        this,
        new PropertyChangedEventArgs(propertyName));

    private sealed class DelegateCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter) => execute();
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        public bool CanExecute(object? parameter) => canExecute();

        public async void Execute(object? parameter) => await execute();
    }
}
