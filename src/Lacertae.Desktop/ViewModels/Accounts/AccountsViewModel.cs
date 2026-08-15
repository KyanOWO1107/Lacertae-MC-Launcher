using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Lacertae.Application.Accounts;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.ViewModels.Accounts;

public sealed record AccountPageOperations(
    Func<CancellationToken, Task<IReadOnlyList<Account>>> LoadAccounts,
    Func<string, CancellationToken, Task<Result<Account>>> AddOfflineAccount,
    Func<CancellationToken, Task<Result<Account>>>? AddMicrosoftAccount,
    Func<string, CancellationToken, Task<Result<Unit>>> SetDefaultAccount,
    Func<string, CancellationToken, Task<Result<Unit>>>? SetVersionAccount,
    Func<string, CancellationToken, Task<Result<Unit>>> DeleteAccount);

public sealed class AccountsViewModel : INotifyPropertyChanged
{
    private readonly AccountPageOperations operations;
    private readonly IAvatarCache avatarCache;
    private readonly string? gameRootId;
    private readonly string? versionFolder;
    private string? defaultAccountId;
    private string? versionOverrideAccountId;
    private AccountItemViewModel? selectedAccount;
    private AccountItemViewModel? pendingDeleteAccount;
    private string offlinePlayerNameDraft = string.Empty;
    private string? microsoftSecretDraft;
    private string? errorCode;
    private string? errorMessage;
    private string microsoftLoginStatus;
    private bool isLoading;
    private bool isMicrosoftLoginInProgress;
    private bool isDeleteConfirmationOpen;
    private bool isConfiguredMicrosoftLogin;
    private readonly string? microsoftConfigurationErrorCode;
    private CancellationTokenSource? microsoftLoginCancellation;

    public AccountsViewModel(
        AccountPageOperations operations,
        IAvatarCache avatarCache,
        string? defaultAccountId = null,
        string? versionOverrideAccountId = null,
        string? gameRootId = null,
        string? versionFolder = null,
        bool microsoftLoginConfigured = false,
        string? microsoftConfigurationErrorCode = null)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.avatarCache = avatarCache ?? throw new ArgumentNullException(nameof(avatarCache));
        ValidateOperations(operations);
        this.defaultAccountId = defaultAccountId;
        this.versionOverrideAccountId = versionOverrideAccountId;
        this.gameRootId = gameRootId;
        this.versionFolder = versionFolder;
        this.microsoftConfigurationErrorCode = string.IsNullOrWhiteSpace(microsoftConfigurationErrorCode)
            ? null
            : microsoftConfigurationErrorCode;
        isConfiguredMicrosoftLogin = microsoftLoginConfigured && operations.AddMicrosoftAccount is not null;
        microsoftLoginStatus = this.microsoftConfigurationErrorCode is not null
            ? $"Microsoft 登录配置无效（{this.microsoftConfigurationErrorCode}）；离线账号不受影响。"
            : isConfiguredMicrosoftLogin
                ? "Microsoft 登录已配置，可在此设备的系统浏览器中继续。"
                : "此构建未配置 Microsoft 登录；离线账号不受影响。";

        RefreshCommand = new AsyncCommand(
            () => LoadAsync(CancellationToken.None),
            () => !IsLoading && !IsMicrosoftLoginInProgress);
        AddOfflineAccountCommand = new AsyncCommand(
            () => AddOfflineAccountAsync(OfflinePlayerNameDraft, CancellationToken.None),
            () => !IsLoading && !IsMicrosoftLoginInProgress && !string.IsNullOrWhiteSpace(OfflinePlayerNameDraft));
        MicrosoftLoginCommand = new AsyncCommand(
            () => AddMicrosoftAccountAsync(CancellationToken.None),
            () => CanMicrosoftLogin);
        CancelMicrosoftLoginCommand = new DelegateCommand(
            _ => CancelMicrosoftLogin(),
            _ => IsMicrosoftLoginInProgress);
        SetDefaultAccountCommand = new AsyncCommand<AccountItemViewModel>(
            (item, token) => SetDefaultAccountAsync(item, token),
            item => item?.CanSetDefault == true && !IsLoading);
        SetVersionAccountCommand = new AsyncCommand<AccountItemViewModel>(
            (item, token) => SetVersionAccountAsync(item, token),
            item => item?.CanSetVersionAccount == true && CanSetVersionAccount && !IsLoading);
        BeginDeleteCommand = new DelegateCommand(
            parameter =>
            {
                if (parameter is AccountItemViewModel item)
                {
                    BeginDelete(item);
                }
            },
            parameter => parameter is AccountItemViewModel item && item.IsEnabled && !IsLoading);
        ConfirmDeleteCommand = new AsyncCommand(
            () => ConfirmDeleteAsync(DeleteConfirmationDraft, CancellationToken.None),
            () => IsDeleteConfirmationOpen && !string.IsNullOrEmpty(DeleteConfirmationDraft));
        CancelDeleteCommand = new DelegateCommand(_ => CancelDelete(), _ => IsDeleteConfirmationOpen);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    public IReadOnlyList<AccountItemViewModel> Items => Accounts;

    public bool HasAccounts => Accounts.Count > 0;

    public AccountItemViewModel? SelectedAccount
    {
        get => selectedAccount;
        set
        {
            if (ReferenceEquals(selectedAccount, value))
            {
                return;
            }

            selectedAccount = value;
            OnPropertyChanged(nameof(SelectedAccount));
        }
    }

    public string? DefaultAccountId => defaultAccountId;

    public string? VersionOverrideAccountId => versionOverrideAccountId;

    public string? ResolvedAccountId => versionOverrideAccountId ?? defaultAccountId;

    public AccountItemViewModel? ResolvedAccount =>
        Accounts.FirstOrDefault(item => string.Equals(item.Id, ResolvedAccountId, StringComparison.Ordinal));

    public string ResolvedAccountSummary => ResolvedAccount?.PlayerName ?? "未选择账号";

    public string ResolvedAccountSourceLabel =>
        ResolvedAccount is null
            ? "未选择账号"
            : versionOverrideAccountId is not null
                ? "此版本专用"
                : "默认账号";

    public bool HasVersionContext =>
        !string.IsNullOrWhiteSpace(gameRootId) && !string.IsNullOrWhiteSpace(versionFolder);

    public bool CanSetVersionAccount => HasVersionContext && operations.SetVersionAccount is not null;

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
            RaiseCommandCanExecuteChanged();
        }
    }

    public bool IsNotLoading => !IsLoading;

    public bool IsMicrosoftLoginConfigured => isConfiguredMicrosoftLogin;

    public bool CanMicrosoftLogin =>
        IsMicrosoftLoginConfigured && !IsMicrosoftLoginInProgress && !IsLoading;

    public bool IsMicrosoftLoginInProgress
    {
        get => isMicrosoftLoginInProgress;
        private set
        {
            if (isMicrosoftLoginInProgress == value)
            {
                return;
            }

            isMicrosoftLoginInProgress = value;
            OnPropertyChanged(nameof(IsMicrosoftLoginInProgress));
            OnPropertyChanged(nameof(CanMicrosoftLogin));
            RaiseCommandCanExecuteChanged();
        }
    }

    public string MicrosoftLoginStatus
    {
        get => microsoftLoginStatus;
        private set
        {
            if (string.Equals(microsoftLoginStatus, value, StringComparison.Ordinal))
            {
                return;
            }

            microsoftLoginStatus = value;
            OnPropertyChanged(nameof(MicrosoftLoginStatus));
        }
    }

    public string OfflinePlayerNameDraft
    {
        get => offlinePlayerNameDraft;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(offlinePlayerNameDraft, next, StringComparison.Ordinal))
            {
                return;
            }

            offlinePlayerNameDraft = next;
            OnPropertyChanged(nameof(OfflinePlayerNameDraft));
            RaiseCommandCanExecuteChanged();
        }
    }

    /// <summary>
    /// Kept for compatibility with the onboarding draft shape. It is never
    /// populated by the account page and is not persisted or sent to a server.
    /// </summary>
    public string? MicrosoftSecretDraft
    {
        get => microsoftSecretDraft;
        private set
        {
            if (string.Equals(microsoftSecretDraft, value, StringComparison.Ordinal))
            {
                return;
            }

            microsoftSecretDraft = value;
            OnPropertyChanged(nameof(MicrosoftSecretDraft));
        }
    }

    private string deleteConfirmationDraft = string.Empty;

    public string DeleteConfirmationDraft
    {
        get => deleteConfirmationDraft;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(deleteConfirmationDraft, next, StringComparison.Ordinal))
            {
                return;
            }

            deleteConfirmationDraft = next;
            OnPropertyChanged(nameof(DeleteConfirmationDraft));
            RaiseCommandCanExecuteChanged();
        }
    }

    public bool IsDeleteConfirmationOpen
    {
        get => isDeleteConfirmationOpen;
        private set
        {
            if (isDeleteConfirmationOpen == value)
            {
                return;
            }

            isDeleteConfirmationOpen = value;
            OnPropertyChanged(nameof(IsDeleteConfirmationOpen));
            OnPropertyChanged(nameof(PendingDeleteAccount));
            RaiseCommandCanExecuteChanged();
        }
    }

    public AccountItemViewModel? PendingDeleteAccount => pendingDeleteAccount;

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

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorCode);

    public ICommand RefreshCommand { get; }

    public ICommand AddOfflineAccountCommand { get; }

    public ICommand MicrosoftLoginCommand { get; }

    public ICommand CancelMicrosoftLoginCommand { get; }

    public ICommand SetDefaultAccountCommand { get; }

    public ICommand SetVersionAccountCommand { get; }

    public ICommand BeginDeleteCommand { get; }

    public ICommand ConfirmDeleteCommand { get; }

    public ICommand CancelDeleteCommand { get; }

    public async Task<Result<IReadOnlyList<AccountItemViewModel>>> LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        ClearError();
        try
        {
            IReadOnlyList<Account> accounts = await operations.LoadAccounts(cancellationToken);
            RebuildRows(accounts);
            return Result<IReadOnlyList<AccountItemViewModel>>.Success(Accounts.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetError("ACCOUNT_LIST_FAILED", "账号列表暂时不可用，请稍后重试。", exception.Message);
            return Result<IReadOnlyList<AccountItemViewModel>>.Failure(
                Problem("ACCOUNT_LIST_FAILED", retryable: true));
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<Result<Account>> AddOfflineAccountAsync(
        string playerName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return Failure<Account>("AUTH_OFFLINE_NAME_REQUIRED");
        }

        IsLoading = true;
        ClearError();
        try
        {
            Result<Account> result = await operations.AddOfflineAccount(playerName.Trim(), cancellationToken);
            if (!result.IsSuccess)
            {
                SetError(result.Problem!);
                return result;
            }

            OfflinePlayerNameDraft = string.Empty;
            await LoadAsync(cancellationToken);
            return result;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<Result<Account>> AddMicrosoftAccountAsync(CancellationToken cancellationToken)
    {
        if (!IsMicrosoftLoginConfigured || operations.AddMicrosoftAccount is null)
        {
            Result<Account> unavailable = Failure<Account>("AUTH_MICROSOFT_NOT_CONFIGURED");
            SetError(unavailable.Problem!);
            return unavailable;
        }

        IsMicrosoftLoginInProgress = true;
        MicrosoftLoginStatus = "等待浏览器登录……完成后会回到 Lacertae。";
        ClearError();
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        microsoftLoginCancellation = linkedCancellation;
        try
        {
            Result<Account> result = await operations.AddMicrosoftAccount(linkedCancellation.Token);
            if (!result.IsSuccess)
            {
                SetError(result.Problem!);
                MicrosoftLoginStatus = result.Problem?.Code == "AUTH_CANCELLED"
                    ? "Microsoft 登录已取消。"
                    : $"Microsoft 登录未完成（{result.Problem?.Code ?? "AUTH_FAILED"}）。";
                return result;
            }

            await LoadAsync(cancellationToken);
            MicrosoftLoginStatus = "Microsoft 登录成功，账号资料已更新。";
            return result;
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            Result<Account> cancelled = Failure<Account>("AUTH_CANCELLED");
            SetError(cancelled.Problem!);
            MicrosoftLoginStatus = "Microsoft 登录已取消。";
            return cancelled;
        }
        finally
        {
            if (ReferenceEquals(microsoftLoginCancellation, linkedCancellation))
            {
                microsoftLoginCancellation = null;
            }

            IsMicrosoftLoginInProgress = false;
        }
    }

    public async Task<Result<Unit>> SetDefaultAccountAsync(
        AccountItemViewModel? item,
        CancellationToken cancellationToken)
    {
        if (item is null || !item.CanSetDefault)
        {
            Result<Unit> invalid = Failure<Unit>("AUTH_ACCOUNT_REQUIRED");
            SetError(invalid.Problem!);
            return invalid;
        }

        Result<Unit> result = await operations.SetDefaultAccount(item.Id, cancellationToken);
        if (!result.IsSuccess)
        {
            SetError(result.Problem!);
            return result;
        }

        defaultAccountId = item.Id;
        ReapplySelectionFlags();
        ClearError();
        return result;
    }

    public async Task<Result<Unit>> SetVersionAccountAsync(
        AccountItemViewModel? item,
        CancellationToken cancellationToken)
    {
        if (item is null || !item.CanSetVersionAccount)
        {
            Result<Unit> invalid = Failure<Unit>("AUTH_ACCOUNT_REQUIRED");
            SetError(invalid.Problem!);
            return invalid;
        }

        if (!CanSetVersionAccount || operations.SetVersionAccount is null)
        {
            Result<Unit> unavailable = Failure<Unit>("ACCOUNT_VERSION_CONTEXT_REQUIRED");
            SetError(unavailable.Problem!);
            return unavailable;
        }

        Result<Unit> result = await operations.SetVersionAccount(item.Id, cancellationToken);
        if (!result.IsSuccess)
        {
            SetError(result.Problem!);
            return result;
        }

        versionOverrideAccountId = item.Id;
        ReapplySelectionFlags();
        ClearError();
        return result;
    }

    public void BeginDelete(AccountItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsEnabled || IsLoading || IsMicrosoftLoginInProgress)
        {
            return;
        }

        pendingDeleteAccount = item;
        DeleteConfirmationDraft = string.Empty;
        OnPropertyChanged(nameof(DeleteConfirmationDraft));
        IsDeleteConfirmationOpen = true;
        ClearError();
    }

    public async Task<Result<Unit>> ConfirmDeleteAsync(
        string playerName,
        CancellationToken cancellationToken)
    {
        AccountItemViewModel? item = pendingDeleteAccount;
        if (!IsDeleteConfirmationOpen || item is null)
        {
            Result<Unit> unavailable = Failure<Unit>("ACCOUNT_DELETE_CONFIRMATION_REQUIRED");
            SetError(unavailable.Problem!);
            return unavailable;
        }

        if (!string.Equals(playerName, item.PlayerName, StringComparison.Ordinal))
        {
            Result<Unit> mismatch = Failure<Unit>("ACCOUNT_DELETE_CONFIRMATION_MISMATCH");
            SetError(mismatch.Problem!);
            return mismatch;
        }

        item.MarkDeleting();
        IsDeleteConfirmationOpen = false;
        Result<Unit> result = await operations.DeleteAccount(item.Id, cancellationToken);
        if (!result.IsSuccess)
        {
            SetError(result.Problem!);
            await LoadAsync(cancellationToken);
            return result;
        }

        Accounts.Remove(item);
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(HasAccounts));
        if (string.Equals(defaultAccountId, item.Id, StringComparison.Ordinal))
        {
            defaultAccountId = null;
        }

        if (string.Equals(versionOverrideAccountId, item.Id, StringComparison.Ordinal))
        {
            versionOverrideAccountId = null;
        }

        pendingDeleteAccount = null;
        OnPropertyChanged(nameof(PendingDeleteAccount));
        ReapplySelectionFlags();
        ClearError();
        return result;
    }

    public void CancelDelete()
    {
        pendingDeleteAccount = null;
        DeleteConfirmationDraft = string.Empty;
        OnPropertyChanged(nameof(DeleteConfirmationDraft));
        IsDeleteConfirmationOpen = false;
    }

    public void CancelMicrosoftLogin()
    {
        if (!IsMicrosoftLoginInProgress || microsoftLoginCancellation is null)
        {
            return;
        }

        microsoftLoginCancellation.Cancel();
        MicrosoftLoginStatus = "正在取消 Microsoft 登录……";
    }

    private void RebuildRows(IReadOnlyList<Account> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        Dictionary<string, AccountItemViewModel> existing = Accounts.ToDictionary(
            static item => item.Id,
            StringComparer.Ordinal);
        Accounts.Clear();
        string? resolvedId = ResolvedAccountId;
        foreach (Account account in accounts.OrderBy(static item => item.PlayerName, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Id, StringComparer.Ordinal))
        {
            string? avatarPath = avatarCache.ResolvePath(account.AvatarCacheKey);
            bool isDefault = string.Equals(account.Id, defaultAccountId, StringComparison.Ordinal);
            bool isVersionOverride = string.Equals(account.Id, versionOverrideAccountId, StringComparison.Ordinal);
            bool isResolved = string.Equals(account.Id, resolvedId, StringComparison.Ordinal);
            if (existing.TryGetValue(account.Id, out AccountItemViewModel? row))
            {
                row.Apply(account, avatarPath, isDefault, isVersionOverride, isResolved);
            }
            else
            {
                row = new AccountItemViewModel(account, avatarPath, isDefault, isVersionOverride, isResolved);
            }

            Accounts.Add(row);
        }

        if (selectedAccount is not null)
        {
            SelectedAccount = Accounts.FirstOrDefault(item => item.Id == selectedAccount.Id);
        }

        PublishResolvedState();
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(HasAccounts));
    }

    private void ReapplySelectionFlags()
    {
        string? resolvedId = ResolvedAccountId;
        foreach (AccountItemViewModel row in Accounts)
        {
            row.Apply(
                row.Account,
                row.AvatarPath,
                string.Equals(row.Id, defaultAccountId, StringComparison.Ordinal),
                string.Equals(row.Id, versionOverrideAccountId, StringComparison.Ordinal),
                string.Equals(row.Id, resolvedId, StringComparison.Ordinal));
        }

        PublishResolvedState();
    }

    private void PublishResolvedState()
    {
        OnPropertyChanged(nameof(DefaultAccountId));
        OnPropertyChanged(nameof(VersionOverrideAccountId));
        OnPropertyChanged(nameof(ResolvedAccountId));
        OnPropertyChanged(nameof(ResolvedAccount));
        OnPropertyChanged(nameof(ResolvedAccountSummary));
        OnPropertyChanged(nameof(ResolvedAccountSourceLabel));
    }

    private void SetError(Problem problem) => SetError(problem.Code, problem.MessageKey, null);

    private void SetError(string code, string message, string? detail)
    {
        ErrorCode = code;
        ErrorMessage = detail is null ? message : message;
    }

    private void ClearError()
    {
        ErrorCode = null;
        ErrorMessage = null;
    }

    private static Result<T> Failure<T>(string code) => Result<T>.Failure(Problem(code));

    private static Problem Problem(string code, bool retryable = false) => new(
        code,
        ProblemStage.Authentication,
        "problem.auth.account_ui",
        retryable,
        Guid.NewGuid().ToString("N"),
        ["action.auth.review"]);

    private static void ValidateOperations(AccountPageOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations.LoadAccounts);
        ArgumentNullException.ThrowIfNull(operations.AddOfflineAccount);
        ArgumentNullException.ThrowIfNull(operations.SetDefaultAccount);
        ArgumentNullException.ThrowIfNull(operations.DeleteAccount);
    }

    private void RaiseCommandCanExecuteChanged()
    {
        (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (AddOfflineAccountCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (MicrosoftLoginCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (CancelMicrosoftLoginCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (SetDefaultAccountCommand as AsyncCommand<AccountItemViewModel>)?.RaiseCanExecuteChanged();
        (SetVersionAccountCommand as AsyncCommand<AccountItemViewModel>)?.RaiseCanExecuteChanged();
        (BeginDeleteCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (ConfirmDeleteCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (CancelDeleteCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(
        this,
        new PropertyChangedEventArgs(propertyName));

    private sealed class DelegateCommand(
        Action<object?> execute,
        Func<object?, bool>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncCommand(
        Func<Task> execute,
        Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public async void Execute(object? parameter) => await execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncCommand<T>(
        Func<T?, CancellationToken, Task> execute,
        Func<T?, bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute(parameter is T value ? value : default);

        public async void Execute(object? parameter) => await execute(
            parameter is T value ? value : default,
            CancellationToken.None);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
