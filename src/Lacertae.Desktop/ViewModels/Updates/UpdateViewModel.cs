using System.ComponentModel;
using System.Windows.Input;
using Lacertae.Application.Updates;
using Lacertae.Domain.Common;
using Lacertae.Domain.Updates;

namespace Lacertae.Desktop.ViewModels.Updates;

public enum UpdateUiState
{
    Disabled,
    Checking,
    Current,
    Available,
    Downloading,
    ReadyToApply,
    ApplyingOnExit,
    Failed,
    RolledBack,
}

/// <summary>
/// Non-modal update interaction state. All network and filesystem work is
/// supplied by delegates so the shell remains usable when updates are not
/// configured and the game/install activity can be checked by the host.
/// </summary>
public sealed class UpdateViewModel : INotifyPropertyChanged
{
    private readonly Func<CancellationToken, Task<Lacertae.Domain.Results.Result<UpdateCheckResult>>>? check;
    private readonly Func<VerifiedUpdateManifest, CancellationToken, Task<Lacertae.Domain.Results.Result<StagedUpdate>>>? download;
    private readonly Func<StagedUpdate, CancellationToken, Task<Lacertae.Domain.Results.Result<Unit>>>? apply;
    private readonly Func<StagedUpdate, CancellationToken, Task<Lacertae.Domain.Results.Result<Unit>>>? cancelStaged;
    private readonly Func<bool> gameRunning;
    private readonly Func<bool> installRunning;
    private bool confirmationOpen;
    private UpdateUiState state;
    private VerifiedUpdateManifest? availableUpdate;
    private StagedUpdate? stagedUpdate;
    private string? errorCode;
    private string? errorMessage;
    private bool isBusy;

    public UpdateViewModel(
        bool enabled = false,
        Func<CancellationToken, Task<Lacertae.Domain.Results.Result<UpdateCheckResult>>>? check = null,
        Func<VerifiedUpdateManifest, CancellationToken, Task<Lacertae.Domain.Results.Result<StagedUpdate>>>? download = null,
        Func<StagedUpdate, CancellationToken, Task<Lacertae.Domain.Results.Result<Unit>>>? apply = null,
        Func<StagedUpdate, CancellationToken, Task<Lacertae.Domain.Results.Result<Unit>>>? cancelStaged = null,
        Func<bool>? gameRunning = null,
        Func<bool>? installRunning = null)
    {
        this.check = check;
        this.download = download;
        this.apply = apply;
        this.cancelStaged = cancelStaged;
        this.gameRunning = gameRunning ?? (() => false);
        this.installRunning = installRunning ?? (() => false);
        IsEnabled = enabled && check is not null;
        state = IsEnabled ? UpdateUiState.Current : UpdateUiState.Disabled;
        CheckCommand = new AsyncCommand(_ => CheckAsync(CancellationToken.None), _ => CanCheck);
        OpenDownloadConfirmationCommand = new DelegateCommand(_ => OpenDownloadConfirmation(), _ => CanOpenDownloadConfirmation);
        ConfirmDownloadCommand = new AsyncCommand(_ => ConfirmDownloadAsync(CancellationToken.None), _ => CanConfirmDownload);
        CancelConfirmationCommand = new DelegateCommand(_ => CancelConfirmation(), _ => IsConfirmationOpen);
        ApplyOnExitCommand = new AsyncCommand(_ => ApplyOnExitAsync(CancellationToken.None), _ => CanApplyOnExit);
        CancelStagedCommand = new AsyncCommand(_ => CancelStagedAsync(CancellationToken.None), _ => CanCancelStaged);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnabled { get; }

    public UpdateUiState State
    {
        get => state;
        private set => Set(ref state, value, nameof(State));
    }

    public bool IsBannerVisible => IsEnabled && State is not UpdateUiState.Disabled and not UpdateUiState.Current;

    public bool IsAvailable => State == UpdateUiState.Available;

    public bool IsDownloading => State == UpdateUiState.Downloading;

    public bool IsReadyToApply => State == UpdateUiState.ReadyToApply;

    public bool IsFailed => State is UpdateUiState.Failed or UpdateUiState.RolledBack;

    public string StateLabel => State switch
    {
        UpdateUiState.Disabled => "未启用",
        UpdateUiState.Checking => "检查中",
        UpdateUiState.Current => "已是最新",
        UpdateUiState.Available => "发现更新",
        UpdateUiState.Downloading => "下载中",
        UpdateUiState.ReadyToApply => "等待退出后更新",
        UpdateUiState.ApplyingOnExit => "准备应用更新",
        UpdateUiState.Failed => "更新失败",
        UpdateUiState.RolledBack => "已回滚",
        _ => "未知状态",
    };

    public bool IsConfirmationOpen
    {
        get => confirmationOpen;
        private set => Set(ref confirmationOpen, value, nameof(IsConfirmationOpen));
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => Set(ref isBusy, value, nameof(IsBusy));
    }

    public VerifiedUpdateManifest? AvailableUpdate => availableUpdate;

    public StagedUpdate? StagedUpdate => stagedUpdate;

    public string? Version => availableUpdate?.Manifest.Version;

    public string? Channel => availableUpdate is null ? null : GetChannelLabel(availableUpdate.Manifest.Channel);

    public long PackageSize => availableUpdate?.Manifest.Package.Size ?? 0;

    public IReadOnlyDictionary<string, string> ReleaseNotes =>
        availableUpdate?.Manifest.ReleaseNotes ?? new Dictionary<string, string>(StringComparer.Ordinal);

    public string? ErrorCode
    {
        get => errorCode;
        private set => Set(ref errorCode, value, nameof(ErrorCode));
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set => Set(ref errorMessage, value, nameof(ErrorMessage));
    }

    public bool CanCheck => IsEnabled && !IsBusy && check is not null;

    public bool CanOpenDownloadConfirmation => !IsBusy && State == UpdateUiState.Available && availableUpdate is not null && download is not null;

    public bool CanConfirmDownload => IsConfirmationOpen && CanOpenDownloadConfirmation;

    public bool CanApplyOnExit => !IsBusy && State == UpdateUiState.ReadyToApply && stagedUpdate is not null && apply is not null && !gameRunning() && !installRunning();

    public bool CanCancelStaged => !IsBusy && State == UpdateUiState.ReadyToApply && stagedUpdate is not null && cancelStaged is not null;

    public ICommand CheckCommand { get; }

    public ICommand OpenDownloadConfirmationCommand { get; }

    public ICommand ConfirmDownloadCommand { get; }

    public ICommand CancelConfirmationCommand { get; }

    public ICommand ApplyOnExitCommand { get; }

    public ICommand CancelStagedCommand { get; }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!CanCheck || check is null)
        {
            return;
        }

        IsBusy = true;
        State = UpdateUiState.Checking;
        ErrorCode = null;
        ErrorMessage = null;
        try
        {
            Lacertae.Domain.Results.Result<UpdateCheckResult> result = await check(cancellationToken);
            if (!result.IsSuccess)
            {
                SetFailure(result.Problem?.Code ?? "UPDATE_CHECK_FAILED", result.Problem?.MessageKey);
                return;
            }

            availableUpdate = result.Value.Update;
            OnPropertyChanged(nameof(AvailableUpdate));
            OnPropertyChanged(nameof(Version));
            OnPropertyChanged(nameof(Channel));
            OnPropertyChanged(nameof(PackageSize));
            OnPropertyChanged(nameof(ReleaseNotes));
            State = result.Value.Status switch
            {
                UpdateCheckStatus.Available => UpdateUiState.Available,
                UpdateCheckStatus.Current => UpdateUiState.Current,
                UpdateCheckStatus.Disabled => UpdateUiState.Disabled,
                _ => UpdateUiState.Failed,
            };
            ErrorCode = result.Value.Problem?.Code;
            ErrorMessage = result.Value.Problem?.MessageKey;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException)
        {
            SetFailure("UPDATE_CHECK_FAILED", exception.Message);
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    public void OpenDownloadConfirmation()
    {
        if (!CanOpenDownloadConfirmation)
        {
            return;
        }

        IsConfirmationOpen = true;
        RaiseCommandStates();
    }

    public void CancelConfirmation()
    {
        IsConfirmationOpen = false;
        RaiseCommandStates();
    }

    public async Task ConfirmDownloadAsync(CancellationToken cancellationToken)
    {
        if (!CanConfirmDownload || availableUpdate is null || download is null)
        {
            return;
        }

        IsConfirmationOpen = false;
        IsBusy = true;
        State = UpdateUiState.Downloading;
        ErrorCode = null;
        ErrorMessage = null;
        try
        {
            Lacertae.Domain.Results.Result<StagedUpdate> result = await download(availableUpdate, cancellationToken);
            if (!result.IsSuccess)
            {
                SetFailure(result.Problem?.Code ?? "UPDATE_STAGE_FAILED", result.Problem?.MessageKey);
                return;
            }

            stagedUpdate = result.Value;
            OnPropertyChanged(nameof(StagedUpdate));
            State = UpdateUiState.ReadyToApply;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetFailure("UPDATE_STAGE_FAILED", exception.Message);
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    public async Task ApplyOnExitAsync(CancellationToken cancellationToken)
    {
        if (!CanApplyOnExit || stagedUpdate is null || apply is null)
        {
            if (State == UpdateUiState.ReadyToApply && (gameRunning() || installRunning()))
            {
                ErrorCode = "UPDATE_ACTIVE_OPERATION";
                ErrorMessage = "update.active_operation";
                RaiseCommandStates();
            }

            return;
        }

        IsBusy = true;
        State = UpdateUiState.ApplyingOnExit;
        ErrorCode = null;
        ErrorMessage = null;
        try
        {
            Lacertae.Domain.Results.Result<Unit> result = await apply(stagedUpdate, cancellationToken);
            if (!result.IsSuccess)
            {
                State = result.Problem?.Code == "UPDATE_ROLLED_BACK" ? UpdateUiState.RolledBack : UpdateUiState.Failed;
                ErrorCode = result.Problem?.Code ?? "UPDATE_APPLY_FAILED";
                ErrorMessage = result.Problem?.MessageKey;
                return;
            }

            State = UpdateUiState.Current;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetFailure("UPDATE_APPLY_FAILED", exception.Message);
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    public async Task CancelStagedAsync(CancellationToken cancellationToken)
    {
        if (!CanCancelStaged || stagedUpdate is null || cancelStaged is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Lacertae.Domain.Results.Result<Unit> result = await cancelStaged(stagedUpdate, cancellationToken);
            if (!result.IsSuccess)
            {
                SetFailure(result.Problem?.Code ?? "UPDATE_STAGE_CANCEL_FAILED", result.Problem?.MessageKey);
                return;
            }

            stagedUpdate = null;
            OnPropertyChanged(nameof(StagedUpdate));
            State = availableUpdate is null ? UpdateUiState.Current : UpdateUiState.Available;
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private void SetFailure(string code, string? message)
    {
        State = UpdateUiState.Failed;
        ErrorCode = code;
        ErrorMessage = message;
    }

    private void RaiseCommandStates()
    {
        if (CheckCommand is AsyncCommand checkCommand)
        {
            checkCommand.RaiseCanExecuteChanged();
        }

        if (OpenDownloadConfirmationCommand is DelegateCommand openCommand)
        {
            openCommand.RaiseCanExecuteChanged();
        }

        if (ConfirmDownloadCommand is AsyncCommand confirmCommand)
        {
            confirmCommand.RaiseCanExecuteChanged();
        }

        if (CancelConfirmationCommand is DelegateCommand cancelCommand)
        {
            cancelCommand.RaiseCanExecuteChanged();
        }

        if (ApplyOnExitCommand is AsyncCommand applyCommand)
        {
            applyCommand.RaiseCanExecuteChanged();
        }

        if (CancelStagedCommand is AsyncCommand cancelStagedCommand)
        {
            cancelStagedCommand.RaiseCanExecuteChanged();
        }

        OnPropertyChanged(nameof(IsBannerVisible));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanOpenDownloadConfirmation));
        OnPropertyChanged(nameof(CanConfirmDownload));
        OnPropertyChanged(nameof(CanApplyOnExit));
        OnPropertyChanged(nameof(CanCancelStaged));
    }

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(State))
        {
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(IsBannerVisible));
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(IsReadyToApply));
            OnPropertyChanged(nameof(IsFailed));
        }
        if (propertyName is nameof(State) or nameof(IsBusy))
        {
            RaiseCommandStates();
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string GetChannelLabel(UpdateChannel value) => value switch
    {
        UpdateChannel.Stable => "稳定版",
        UpdateChannel.Preview => "预览版",
        UpdateChannel.Test => "测试版",
        UpdateChannel.Nightly => "每日构建",
        _ => "未知渠道",
    };

    private sealed class DelegateCommand(Action<object?> execute, Func<object?, bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute(parameter);

        public void Execute(object? parameter) => execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncCommand(Func<object?, Task> execute, Func<object?, bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute(parameter);

        public async void Execute(object? parameter) => await execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
