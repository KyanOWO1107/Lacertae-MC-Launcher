using System.ComponentModel;
using System.Windows.Input;
using Lacertae.Application.GameRoots;
using Lacertae.Application.Games;
using Lacertae.Application.Platform;
using Lacertae.Application.Settings;
using Lacertae.Application.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Desktop.ViewModels.Versions;

public sealed class VersionRowViewModel
{
    public VersionRowViewModel(ListedGameVersion version, GameRoot root)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(root);
        Version = version;
        Root = root;
    }

    public ListedGameVersion Version { get; }

    public GameRoot Root { get; }

    public string DisplayName => Version.DisplayName;

    public string FolderName => Version.FolderName;

    public string PhysicalFolder => Version.FolderName;

    public string PhysicalPath => Path.Combine(Root.NormalizedPath, "versions", Version.FolderName);

    public string VersionType => Version.VersionType;

    public bool HasModLoader => Version.HasModLoader;

    public string LoaderLabel => HasModLoader ? "含 Mod Loader" : "原版";

    public bool IsIsolated => Version.IsolationDecision.IsIsolated;

    public string IsolationLabel => IsIsolated ? "隔离" : "共享";

    public bool RequiresIsolationNotice => Version.IsolationDecision.RequiresUserNotice;

    public string IsolationReasonKey => Version.IsolationDecision.ReasonKey;

    public bool IsAvailable => Root.Availability == GameRootAvailability.Available;

    public string AvailabilityLabel => IsAvailable ? "可用" : "不可用";
}

public sealed class VersionsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IGameRootRepository gameRootRepository;
    private readonly ListGameVersions listGameVersions;
    private LauncherSettings settings;
    private readonly IPlatformDialogService? platformDialogService;
    private readonly ISettingsRepository? settingsRepository;
    private readonly SemaphoreSlim settingsSaveGate = new(1, 1);
    private readonly List<VersionRowViewModel> allVersions = [];
    private GameRoot[] gameRoots = [];
    private IReadOnlyList<VersionRowViewModel> visibleVersions = [];
    private GameRoot? selectedGameRoot;
    private VersionRowViewModel? selectedVersion;
    private string searchText = string.Empty;
    private bool isLoading;
    private bool suppressSelectionRefresh;
    private long refreshGeneration;
    private long selectionPersistenceGeneration;
    private string? errorCode;
    private string? errorMessage;

    public VersionsViewModel()
        : this(new EmptyGameRootRepository(), new ListGameVersions(new EmptyGameEngine(), new EmptyVersionOverrideRepository()), LauncherSettings.Default)
    {
    }

    public VersionsViewModel(
        IGameRootRepository gameRootRepository,
        ListGameVersions listGameVersions,
        LauncherSettings settings,
        IPlatformDialogService? platformDialogService = null,
        ISettingsRepository? settingsRepository = null)
    {
        this.gameRootRepository = gameRootRepository ?? throw new ArgumentNullException(nameof(gameRootRepository));
        this.listGameVersions = listGameVersions ?? throw new ArgumentNullException(nameof(listGameVersions));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.platformDialogService = platformDialogService;
        this.settingsRepository = settingsRepository;
        RefreshCommand = new AsyncCommand(() => LoadAsync(CancellationToken.None), () => !IsLoading);
        OpenDirectoryCommand = new DelegateCommand(parameter =>
        {
            if (parameter is VersionRowViewModel row)
            {
                OpenDirectory(row);
            }
        });
        SelectGameRootCommand = new AsyncCommand<string?>(
            rootId => SelectGameRootAsync(rootId, CancellationToken.None),
            _ => !IsLoading);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose() => settingsSaveGate.Dispose();

    public IReadOnlyList<GameRoot> GameRoots => gameRoots;

    public GameRoot? SelectedGameRoot
    {
        get => selectedGameRoot;
        set
        {
            if (value is not null)
            {
                if (gameRoots.Length == 0)
                {
                    SetError("GAME_ROOT_NOT_ALLOWED", "请先读取已登记的游戏根目录。");
                    return;
                }

                GameRoot? approved = gameRoots.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, value.Id, StringComparison.Ordinal) &&
                    string.Equals(candidate.NormalizedPath, value.NormalizedPath, StringComparison.Ordinal));
                if (approved is null)
                {
                    SetError("GAME_ROOT_NOT_ALLOWED", "只能选择已登记的游戏根目录。");
                    return;
                }

                value = approved;
            }

            if (ReferenceEquals(selectedGameRoot, value))
            {
                return;
            }

            selectedGameRoot = value;
            OnPropertyChanged(nameof(SelectedGameRoot));
            OnPropertyChanged(nameof(IsRootUnavailable));
            OnPropertyChanged(nameof(RootAvailabilityLabel));
            if (!suppressSelectionRefresh && value is not null)
            {
                allVersions.Clear();
                RebuildVisibleVersions();
                SelectedVersion = null;
                _ = HandleSelectedGameRootChangedAsync(value);
            }
        }
    }

    public IReadOnlyList<VersionRowViewModel> Versions => visibleVersions;

    public IReadOnlyList<VersionRowViewModel> VisibleVersions => visibleVersions;

    public VersionRowViewModel? SelectedVersion
    {
        get => selectedVersion;
        set
        {
            if (ReferenceEquals(selectedVersion, value))
            {
                return;
            }

            selectedVersion = value;
            OnPropertyChanged(nameof(SelectedVersion));
        }
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(searchText, next, StringComparison.Ordinal))
            {
                return;
            }

            searchText = next;
            OnPropertyChanged(nameof(SearchText));
            RebuildVisibleVersions();
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
            OnPropertyChanged(nameof(IsNotLoading));
        }
    }

    public bool IsNotLoading => !IsLoading;

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

    public bool IsRootUnavailable => SelectedGameRoot?.Availability == GameRootAvailability.Unavailable;

    public string RootAvailabilityLabel => SelectedGameRoot is null
        ? "未选择游戏根目录"
        : SelectedGameRoot.Availability == GameRootAvailability.Available
            ? "根目录可用"
            : "根目录不可用";

    public bool HasVersions => VisibleVersions.Count > 0;

    public bool IsEmpty => !HasVersions;

    public ICommand RefreshCommand { get; }

    public ICommand OpenDirectoryCommand { get; }

    public ICommand SelectGameRootCommand { get; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        ClearError();
        allVersions.Clear();
        RebuildVisibleVersions();
        SelectedVersion = null;

        try
        {
            if (settingsRepository is not null)
            {
                Result<LauncherSettings> loadedSettings = await settingsRepository.LoadAsync(cancellationToken);
                if (!loadedSettings.IsSuccess)
                {
                    SetError("GAME_ROOT_SETTINGS_LOAD_FAILED", "无法读取游戏根目录设置，请重试。");
                    return;
                }

                settings = loadedSettings.Value;
            }

            gameRoots = (await gameRootRepository.GetAllAsync(cancellationToken))
                .OrderBy(static root => root.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static root => root.Id, StringComparer.Ordinal)
                .ToArray();
            OnPropertyChanged(nameof(GameRoots));

            GameRoot? configured = gameRoots.FirstOrDefault(root =>
                string.Equals(root.Id, settings.SelectedGameRootId, StringComparison.Ordinal));
            suppressSelectionRefresh = true;
            try
            {
                SelectedGameRoot = configured ?? gameRoots.FirstOrDefault(static root => root.Availability == GameRootAvailability.Available)
                    ?? (gameRoots.Length > 0 ? gameRoots[0] : null);
            }
            finally
            {
                suppressSelectionRefresh = false;
            }
            await RefreshVersionsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetError("GAME_ROOT_LIST_FAILED", exception.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SelectGameRootAsync(string? rootId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootId))
        {
            SetError("GAME_ROOT_REQUIRED", "请选择一个游戏根目录。");
            return;
        }

        GameRoot? root = GameRoots.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, rootId, StringComparison.Ordinal));
        if (root is null)
        {
            SetError("GAME_ROOT_NOT_FOUND", "所选游戏根目录不存在。");
            return;
        }

        suppressSelectionRefresh = true;
        try
        {
            SelectedGameRoot = root;
        }
        finally
        {
            suppressSelectionRefresh = false;
        }
        allVersions.Clear();
        RebuildVisibleVersions();
        SelectedVersion = null;
        Result<Unit> persisted = await PersistSelectedGameRootAsync(root, cancellationToken);
        await RefreshVersionsAsync(cancellationToken);
        if (!persisted.IsSuccess && ReferenceEquals(root, SelectedGameRoot))
        {
            SetError(
                "GAME_ROOT_SETTINGS_SAVE_FAILED",
                "根目录已切换，但未能保存选择；重启后可能恢复原设置。");
        }

    }

    public async Task<Result<IReadOnlyList<ListedGameVersion>>> RefreshVersionsAsync(CancellationToken cancellationToken)
    {
        long generation = Interlocked.Increment(ref refreshGeneration);
        GameRoot? root = SelectedGameRoot;
        if (root is null)
        {
            if (IsCurrentRefresh(generation))
            {
                SetError("GAME_ROOT_REQUIRED", "请选择一个游戏根目录。");
                IsLoading = false;
            }

            return Result<IReadOnlyList<ListedGameVersion>>.Failure(Problem("GAME_ROOT_REQUIRED"));
        }

        if (root.Availability != GameRootAvailability.Available)
        {
            if (IsCurrentRefresh(generation))
            {
                SetError("GAME_ROOT_UNAVAILABLE", "所选游戏根目录不可用，请检查路径后重试。");
                IsLoading = false;
            }

            return Result<IReadOnlyList<ListedGameVersion>>.Failure(Problem("GAME_ROOT_UNAVAILABLE"));
        }

        IsLoading = true;
        ClearError();
        try
        {
            Result<IReadOnlyList<ListedGameVersion>> result = await listGameVersions.ExecuteAsync(
                root,
                settings,
                cancellationToken);
            if (!IsCurrentRefresh(generation))
            {
                return result;
            }

            if (!result.IsSuccess)
            {
                SetError(result.Problem!.Code, "版本列表暂时不可用，请检查根目录和版本文件。");
                return result;
            }

            allVersions.Clear();
            allVersions.AddRange(result.Value.Select(version => new VersionRowViewModel(version, root)));
            RebuildVisibleVersions();
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!IsCurrentRefresh(generation))
            {
                return Result<IReadOnlyList<ListedGameVersion>>.Failure(Problem("VERSION_REFRESH_SUPERSEDED"));
            }

            SetError("VERSION_LIST_FAILED", exception.Message);
            return Result<IReadOnlyList<ListedGameVersion>>.Failure(Problem("VERSION_LIST_FAILED"));
        }
        finally
        {
            if (IsCurrentRefresh(generation))
            {
                IsLoading = false;
            }
        }
    }

    private async Task HandleSelectedGameRootChangedAsync(GameRoot root)
    {
        Result<Unit> persisted;
        try
        {
            persisted = await PersistSelectedGameRootAsync(root, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            persisted = Result<Unit>.Failure(Problem("GAME_ROOT_SETTINGS_SAVE_FAILED"));
        }

        await RefreshVersionsAsync(CancellationToken.None);
        if (!persisted.IsSuccess && ReferenceEquals(root, SelectedGameRoot))
        {
            SetError(
                "GAME_ROOT_SETTINGS_SAVE_FAILED",
                "根目录已切换，但未能保存选择；重启后可能恢复原设置。");
        }
    }

    private async Task<Result<Unit>> PersistSelectedGameRootAsync(
        GameRoot root,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        long generation = Interlocked.Increment(ref selectionPersistenceGeneration);
        if (settingsRepository is null)
        {
            settings = settings with { SelectedGameRootId = root.Id };
            return Result.Success();
        }

        await settingsSaveGate.WaitAsync(cancellationToken);
        try
        {
            if (generation != Volatile.Read(ref selectionPersistenceGeneration))
            {
                return Result.Success();
            }

            LauncherSettings next = settings with { SelectedGameRootId = root.Id };
            Result<Unit> saved = await settingsRepository.SaveAsync(next, cancellationToken);
            if (saved.IsSuccess && generation == Volatile.Read(ref selectionPersistenceGeneration))
            {
                settings = next;
            }

            return saved;
        }
        finally
        {
            settingsSaveGate.Release();
        }
    }

    private bool IsCurrentRefresh(long generation) =>
        generation == Volatile.Read(ref refreshGeneration);

    public void OpenDirectory(VersionRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!allVersions.Contains(row) || SelectedGameRoot is null ||
            !string.Equals(row.Version.GameRootId, SelectedGameRoot.Id, StringComparison.Ordinal))
        {
            SetError("VERSION_DIRECTORY_NOT_ALLOWED", "只能打开当前列表中已验证的版本目录。");
            return;
        }

        if (platformDialogService is null)
        {
            SetError("PLATFORM_DIALOG_UNAVAILABLE", "当前平台暂不支持打开目录。");
            return;
        }

        try
        {
            Result<string> allowedPath = ResolveVersionDirectory.Execute(SelectedGameRoot, row.FolderName);
            if (!allowedPath.IsSuccess)
            {
                SetError("VERSION_DIRECTORY_NOT_ALLOWED", "版本目录路径未通过安全校验。");
                return;
            }

            platformDialogService.OpenDirectory(allowedPath.Value);
            ClearError();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetError("VERSION_DIRECTORY_OPEN_FAILED", exception.Message);
        }
    }

    public event EventHandler<VersionRowViewModel>? EditSettingsRequested;

    public event EventHandler<VersionRowViewModel>? RenameRequested;

    public void RequestEditSettings(VersionRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (allVersions.Contains(row))
        {
            EditSettingsRequested?.Invoke(this, row);
        }
    }

    public void RequestRename(VersionRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (allVersions.Contains(row))
        {
            RenameRequested?.Invoke(this, row);
        }
    }

    private void RebuildVisibleVersions()
    {
        string query = SearchText.Trim();
        visibleVersions = allVersions
            .Where(row => query.Length == 0 ||
                row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.FolderName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.VersionType.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.DisplayName, StringComparer.Ordinal)
            .ThenBy(static row => row.FolderName, StringComparer.Ordinal)
            .ToArray();
        OnPropertyChanged(nameof(Versions));
        OnPropertyChanged(nameof(VisibleVersions));
        OnPropertyChanged(nameof(HasVersions));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ClearError()
    {
        ErrorCode = null;
        ErrorMessage = null;
    }

    private void SetError(string code, string message)
    {
        ErrorCode = code;
        ErrorMessage = string.IsNullOrWhiteSpace(message) ? "操作失败，请检查设置。" : message;
        OnPropertyChanged(nameof(IsRootUnavailable));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.VersionResolution,
        "problem.version.desktop",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.version.review_settings"]);

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        event EventHandler? ICommand.CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        event EventHandler? ICommand.CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => canExecute();

        public async void Execute(object? parameter) => await execute();
    }

    private sealed class AsyncCommand<T>(Func<T?, Task> execute, Func<T?, bool> canExecute) : ICommand
    {
        event EventHandler? ICommand.CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => canExecute(parameter is T value ? value : default);

        public async void Execute(object? parameter) => await execute(parameter is T value ? value : default);
    }

    private sealed class EmptyGameRootRepository : IGameRootRepository
    {
        public Task<IReadOnlyList<GameRoot>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GameRoot>>([]);

        public Task<GameRoot?> FindByNormalizedPathAsync(string normalizedPath, CancellationToken cancellationToken) =>
            Task.FromResult<GameRoot?>(null);

        public Task<Result<Unit>> UpsertAsync(GameRoot gameRoot, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RemoveAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class EmptyGameEngine : IGameEngine
    {
        public Task<Result<IReadOnlyList<GameVersionDescriptor>>> InspectLocalVersionsAsync(
            string gameRootPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<GameVersionDescriptor>>.Success([]));

        public Task<Result<GameProcessSpec>> BuildProcessSpecAsync(LaunchPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(Result<GameProcessSpec>.Failure(Problem("GAME_PROCESS_UNSUPPORTED")));
    }

    private sealed class EmptyVersionOverrideRepository : IVersionOverrideRepository
    {
        public Task<IReadOnlyList<VersionOverride>> GetForGameRootAsync(string gameRootId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VersionOverride>>([]);

        public Task<Result<Unit>> UpsertAsync(VersionOverride versionOverride, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RemoveAsync(string gameRootId, string versionFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RenameAsync(string gameRootId, string sourceFolder, string targetFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
