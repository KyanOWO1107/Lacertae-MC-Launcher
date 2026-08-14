using System.ComponentModel;
using System.Windows.Input;
using Lacertae.Application.Home;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.ViewModels.Home;

public sealed class LaunchCardViewModel : INotifyPropertyChanged
{
    private readonly HomeLaunchCardState state;
    private readonly Func<CancellationToken, Task<Result<LaunchPlan>>>? freezeLaunchPlan;
    private readonly Action<string>? navigate;
    private readonly Action? repairPreview;
    private readonly Action? download;
    private Problem? lastLaunchProblem;

    public LaunchCardViewModel(
        HomeLaunchCardState state,
        Func<CancellationToken, Task<Result<LaunchPlan>>>? freezeLaunchPlan = null,
        Action<string>? navigate = null,
        Action? repairPreview = null,
        Action? download = null)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.freezeLaunchPlan = freezeLaunchPlan;
        this.navigate = navigate;
        this.repairPreview = repairPreview;
        this.download = download;
        LaunchCommand = new AsyncCommand(
            () => ActivateLaunchAsync(CancellationToken.None),
            () => CanExecuteLaunchCommand);
        SelectRequirementCommand = new DelegateCommand(parameter =>
        {
            if (parameter is HomeLaunchRequirement requirement)
            {
                SelectRequirement(requirement);
            }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand LaunchCommand { get; }

    public ICommand SelectRequirementCommand { get; }

    public string VersionSummary => state.VersionDisplayName ?? "未选择游戏版本";

    public string AccountSummary => state.AccountPlayerName ?? "未选择账号";

    public string JavaSummary => state.JavaSummary ?? "未解析兼容 Java";

    public string MaximumMemorySummary => state.MaximumMemoryMb is int maximum
        ? $"最大内存 {maximum} MB"
        : "最大内存未解析";

    public bool CanLaunch => state.CanLaunch && freezeLaunchPlan is not null;

    private bool CanExecuteLaunchCommand => state.CanLaunch
        ? freezeLaunchPlan is not null
        : Requirements.Count > 0;

    public bool IsLaunchDisabled => !CanLaunch;

    public IReadOnlyList<HomeLaunchRequirement> Requirements => state.Requirements;

    public string? FirstActionableReason => state.FirstActionableReason;

    public Problem? LastLaunchProblem => lastLaunchProblem;

    /// <summary>
    /// The home page deliberately never retains an authentication session.
    /// A launch use case receives a fresh session while freezing its plan.
    /// </summary>
    public static bool HasCachedAuthSession => false;

    public async Task<Result<LaunchPlan>?> ActivateLaunchAsync(CancellationToken cancellationToken)
    {
        if (!state.CanLaunch)
        {
            HomeLaunchRequirement? requirement = Requirements.Count > 0 ? Requirements[0] : null;
            if (requirement is not null)
            {
                SelectRequirement(requirement);
            }

            return null;
        }

        if (freezeLaunchPlan is null)
        {
            return null;
        }

        Result<LaunchPlan> result = await freezeLaunchPlan(cancellationToken);
        lastLaunchProblem = result.Problem;
        OnPropertyChanged(nameof(LastLaunchProblem));
        return result;
    }

    public void SelectRequirement(HomeLaunchRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (requirement.IsRepairPreview)
        {
            repairPreview?.Invoke();
            return;
        }

        navigate?.Invoke(requirement.RouteId);
    }

    public void ConfirmRepairDownload()
    {
        // Download is intentionally a second, explicit action after preview.
        download?.Invoke();
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(
        this,
        new PropertyChangedEventArgs(propertyName));

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
}
