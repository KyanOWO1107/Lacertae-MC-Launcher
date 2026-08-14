using System.ComponentModel;
using System.Windows.Input;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Storage;

namespace Lacertae.Desktop.ViewModels.Onboarding;

public enum OnboardingStep
{
    DataLocation,
    GameRoot,
    Account,
    JavaVersion,
}

public sealed record OnboardingDataRootSnapshot
{
    public OnboardingDataRootSnapshot(DataRootMode mode, string summary)
    {
        Mode = mode;
        Summary = string.IsNullOrWhiteSpace(summary)
            ? throw new ArgumentException("Data-root summary cannot be blank.", nameof(summary))
            : summary;
    }

    public DataRootMode Mode { get; }

    public string Summary { get; }

    public string ModeLabel => Mode switch
    {
        DataRootMode.UserProfile => "Windows 用户数据目录",
        DataRootMode.LocalToExecutable => "便携数据目录",
        _ => "已选择的数据目录",
    };
}

public sealed record OnboardingStepViewModel(OnboardingStep Step, string Label, bool IsCompleted);

public sealed record OnboardingVersionSelection(string VersionId, int RequiredJavaMajor, bool IsInstalled);

public sealed record OnboardingJavaSelection(string ExecutablePath, int MajorVersion, bool IsCompatible);

public sealed record OnboardingDurableState(
    string? GameRootId,
    string? AccountId,
    string? VersionId,
    string? JavaPath,
    int? RequiredJavaMajor,
    bool VersionIsInstalled,
    bool HasCompatibleJava,
    bool IsDeferredSetup)
{
    public bool CanFormLaunchPreflight =>
        !string.IsNullOrWhiteSpace(GameRootId) &&
        !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(VersionId) &&
        VersionIsInstalled &&
        !string.IsNullOrWhiteSpace(JavaPath) &&
        HasCompatibleJava;
}

public interface IOnboardingUseCases
{
    Task<Result<GameRoot>> AddGameRootAsync(
        string path,
        bool allowEmpty,
        CancellationToken cancellationToken);

    Task<Result<Account>> AddOfflineAccountAsync(
        string playerName,
        CancellationToken cancellationToken);

    Task<Result<OnboardingVersionSelection>> SelectVersionAsync(
        string versionId,
        CancellationToken cancellationToken);

    Task<Result<OnboardingJavaSelection>> SelectJavaAsync(
        string executablePath,
        int requiredMajor,
        CancellationToken cancellationToken);
}

public sealed class OnboardingViewModel : INotifyPropertyChanged
{
    private static readonly (OnboardingStep Step, string Label)[] StepDefinitions =
    [
        (OnboardingStep.DataLocation, "数据位置摘要"),
        (OnboardingStep.GameRoot, "游戏根目录"),
        (OnboardingStep.Account, "账号"),
        (OnboardingStep.JavaVersion, "Java 与版本"),
    ];

    private readonly IOnboardingUseCases useCases;
    private readonly OnboardingDataRootSnapshot dataRoot;
    private OnboardingDurableState durableState = new(null, null, null, null, null, false, false, false);
    private string? gameRootPathDraft;
    private string? offlinePlayerNameDraft;
    private string? microsoftSecretDraft;
    private string? versionIdDraft;
    private string? javaPathDraft;
    private bool isOpen = true;
    private readonly bool requiresMicrosoftConfiguration;

    public OnboardingViewModel()
        : this(new DisabledOnboardingUseCases(), new OnboardingDataRootSnapshot(DataRootMode.UserProfile, "Windows 用户数据目录"))
    {
    }

    public OnboardingViewModel(
        IOnboardingUseCases useCases,
        OnboardingDataRootSnapshot dataRoot,
        OnboardingDurableState? initialState = null)
    {
        this.useCases = useCases ?? throw new ArgumentNullException(nameof(useCases));
        this.dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        requiresMicrosoftConfiguration = false;
        durableState = initialState ?? durableState;
        AddGameRootCommand = new AsyncCommand(AddGameRootFromDraftAsync, () => !string.IsNullOrWhiteSpace(GameRootPathDraft));
        AddOfflineAccountCommand = new AsyncCommand(AddOfflineAccountFromDraftAsync, () => !string.IsNullOrWhiteSpace(OfflinePlayerNameDraft));
        SelectVersionCommand = new AsyncCommand(SelectVersionFromDraftAsync, () => !string.IsNullOrWhiteSpace(VersionIdDraft));
        SelectJavaCommand = new AsyncCommand(SelectJavaFromDraftAsync, () => !string.IsNullOrWhiteSpace(JavaPathDraft));
        DeferSetupCommand = new DelegateCommand(DeferSetup);
        CloseCommand = new DelegateCommand(Close);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public OnboardingDataRootSnapshot DataRoot => dataRoot;

    public string DataRootSummary => dataRoot.Summary;

    public string DataRootModeLabel => dataRoot.ModeLabel;

    public bool DataRootModeIsInformational => dataRoot.Mode is DataRootMode.UserProfile or DataRootMode.LocalToExecutable;

    public IReadOnlyList<OnboardingStepViewModel> Steps => StepDefinitions
        .Select(static definition => definition.Step == OnboardingStep.DataLocation
            ? new OnboardingStepViewModel(definition.Step, definition.Label, true)
            : new OnboardingStepViewModel(definition.Step, definition.Label, false))
        .Select(step => step with { IsCompleted = IsStepCompleted(step.Step) })
        .ToArray();

    public OnboardingStep CurrentStep => IsStepCompleted(OnboardingStep.GameRoot)
        ? IsStepCompleted(OnboardingStep.Account)
            ? IsStepCompleted(OnboardingStep.JavaVersion)
                ? OnboardingStep.JavaVersion
                : OnboardingStep.JavaVersion
            : OnboardingStep.Account
        : OnboardingStep.DataLocation;

    public OnboardingDurableState DurableState => durableState;

    public bool IsComplete => durableState.IsDeferredSetup || durableState.CanFormLaunchPreflight;

    public bool CanLaunch => durableState.CanFormLaunchPreflight && !durableState.IsDeferredSetup;

    public bool IsLaunchDisabled => durableState.IsDeferredSetup || !CanLaunch;

    public bool IsDeferredSetup => durableState.IsDeferredSetup;

    public bool RequiresMicrosoftConfiguration => requiresMicrosoftConfiguration;

    public bool IsOpen
    {
        get => isOpen;
        private set
        {
            if (isOpen == value)
            {
                return;
            }

            isOpen = value;
            OnPropertyChanged(nameof(IsOpen));
        }
    }

    public string? GameRootPathDraft
    {
        get => gameRootPathDraft;
        set
        {
            if (string.Equals(gameRootPathDraft, value, StringComparison.Ordinal))
            {
                return;
            }

            gameRootPathDraft = value;
            OnPropertyChanged(nameof(GameRootPathDraft));
        }
    }

    public string? OfflinePlayerNameDraft
    {
        get => offlinePlayerNameDraft;
        set
        {
            if (string.Equals(offlinePlayerNameDraft, value, StringComparison.Ordinal))
            {
                return;
            }

            offlinePlayerNameDraft = value;
            OnPropertyChanged(nameof(OfflinePlayerNameDraft));
        }
    }

    public string? MicrosoftSecretDraft
    {
        get => microsoftSecretDraft;
        set
        {
            if (string.Equals(microsoftSecretDraft, value, StringComparison.Ordinal))
            {
                return;
            }

            microsoftSecretDraft = value;
            OnPropertyChanged(nameof(MicrosoftSecretDraft));
        }
    }

    public string? VersionIdDraft
    {
        get => versionIdDraft;
        set
        {
            if (string.Equals(versionIdDraft, value, StringComparison.Ordinal))
            {
                return;
            }

            versionIdDraft = value;
            OnPropertyChanged(nameof(VersionIdDraft));
        }
    }

    public string? JavaPathDraft
    {
        get => javaPathDraft;
        set
        {
            if (string.Equals(javaPathDraft, value, StringComparison.Ordinal))
            {
                return;
            }

            javaPathDraft = value;
            OnPropertyChanged(nameof(JavaPathDraft));
        }
    }

    public ICommand AddGameRootCommand { get; }

    public ICommand AddOfflineAccountCommand { get; }

    public ICommand SelectVersionCommand { get; }

    public ICommand SelectJavaCommand { get; }

    public ICommand DeferSetupCommand { get; }

    public ICommand CloseCommand { get; }

    public async Task<Result<GameRoot>> AddGameRootAsync(
        string path,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        Result<GameRoot> result = await useCases.AddGameRootAsync(path, allowEmpty, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        durableState = durableState with { GameRootId = result.Value.Id, IsDeferredSetup = false };
        GameRootPathDraft = null;
        PublishStateChanged();
        return result;
    }

    public async Task<Result<Account>> AddOfflineAccountAsync(
        string playerName,
        CancellationToken cancellationToken)
    {
        Result<Account> result = await useCases.AddOfflineAccountAsync(playerName, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        durableState = durableState with { AccountId = result.Value.Id, IsDeferredSetup = false };
        OfflinePlayerNameDraft = null;
        MicrosoftSecretDraft = null;
        PublishStateChanged();
        return result;
    }

    public async Task<Result<OnboardingVersionSelection>> SelectVersionAsync(
        string versionId,
        CancellationToken cancellationToken)
    {
        Result<OnboardingVersionSelection> result = await useCases.SelectVersionAsync(versionId, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        if (!result.Value.IsInstalled)
        {
            return Result<OnboardingVersionSelection>.Failure(Problem("ONBOARDING_VERSION_NOT_INSTALLED"));
        }

        durableState = durableState with
        {
            VersionId = result.Value.VersionId,
            RequiredJavaMajor = result.Value.RequiredJavaMajor,
            VersionIsInstalled = true,
            IsDeferredSetup = false,
        };
        VersionIdDraft = null;
        PublishStateChanged();
        return result;
    }

    public async Task<Result<OnboardingJavaSelection>> SelectJavaAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        if (durableState.RequiredJavaMajor is not int requiredMajor)
        {
            return Result<OnboardingJavaSelection>.Failure(Problem("ONBOARDING_VERSION_REQUIRED"));
        }

        Result<OnboardingJavaSelection> result = await useCases.SelectJavaAsync(
            executablePath,
            requiredMajor,
            cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        if (!result.Value.IsCompatible || result.Value.MajorVersion != requiredMajor)
        {
            return Result<OnboardingJavaSelection>.Failure(Problem("ONBOARDING_JAVA_INCOMPATIBLE"));
        }

        durableState = durableState with
        {
            JavaPath = result.Value.ExecutablePath,
            HasCompatibleJava = true,
            IsDeferredSetup = false,
        };
        JavaPathDraft = null;
        PublishStateChanged();
        return result;
    }

    public void DeferSetup()
    {
        durableState = durableState with { IsDeferredSetup = true };
        ClearDrafts();
        IsOpen = false;
        PublishStateChanged();
    }

    public void Close()
    {
        ClearDrafts();
        IsOpen = false;
    }

    public void Reopen()
    {
        if (!isOpen)
        {
            IsOpen = true;
            return;
        }

        OnPropertyChanged(nameof(IsOpen));
    }

    private Task<Result<GameRoot>> AddGameRootFromDraftAsync() =>
        AddGameRootAsync(GameRootPathDraft!, allowEmpty: true, CancellationToken.None);

    private Task<Result<Account>> AddOfflineAccountFromDraftAsync() =>
        AddOfflineAccountAsync(OfflinePlayerNameDraft!, CancellationToken.None);

    private Task<Result<OnboardingVersionSelection>> SelectVersionFromDraftAsync() =>
        SelectVersionAsync(VersionIdDraft!, CancellationToken.None);

    private Task<Result<OnboardingJavaSelection>> SelectJavaFromDraftAsync() =>
        SelectJavaAsync(JavaPathDraft!, CancellationToken.None);

    private bool IsStepCompleted(OnboardingStep step) => step switch
    {
        OnboardingStep.DataLocation => true,
        OnboardingStep.GameRoot => !string.IsNullOrWhiteSpace(durableState.GameRootId),
        OnboardingStep.Account => !string.IsNullOrWhiteSpace(durableState.AccountId),
        OnboardingStep.JavaVersion => durableState.CanFormLaunchPreflight,
        _ => false,
    };

    private void ClearDrafts()
    {
        GameRootPathDraft = null;
        OfflinePlayerNameDraft = null;
        MicrosoftSecretDraft = null;
        VersionIdDraft = null;
        JavaPathDraft = null;
    }

    private void PublishStateChanged()
    {
        OnPropertyChanged(nameof(Steps));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(DurableState));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(IsLaunchDisabled));
        OnPropertyChanged(nameof(IsDeferredSetup));
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(
        this,
        new PropertyChangedEventArgs(propertyName));

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.Configuration,
        "problem.onboarding.invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.onboarding.review"]);

    private sealed class DisabledOnboardingUseCases : IOnboardingUseCases
    {
        public Task<Result<GameRoot>> AddGameRootAsync(string path, bool allowEmpty, CancellationToken cancellationToken) =>
            Task.FromResult(Result<GameRoot>.Failure(Problem("ONBOARDING_ACTION_UNAVAILABLE")));

        public Task<Result<Account>> AddOfflineAccountAsync(string playerName, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Account>.Failure(Problem("ONBOARDING_ACTION_UNAVAILABLE")));

        public Task<Result<OnboardingVersionSelection>> SelectVersionAsync(string versionId, CancellationToken cancellationToken) =>
            Task.FromResult(Result<OnboardingVersionSelection>.Failure(Problem("ONBOARDING_ACTION_UNAVAILABLE")));

        public Task<Result<OnboardingJavaSelection>> SelectJavaAsync(string executablePath, int requiredMajor, CancellationToken cancellationToken) =>
            Task.FromResult(Result<OnboardingJavaSelection>.Failure(Problem("ONBOARDING_ACTION_UNAVAILABLE")));
    }

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        event EventHandler? ICommand.CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
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
}
