using Lacertae.Application.Home;
using Lacertae.Domain.Home;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.ViewModels.Home;

public sealed class HomeViewModel
{
    private readonly Action<HomeQuickAction>? executeQuickAction;

    public HomeViewModel(
        HomeState state,
        Action<string>? navigation = null,
        Action? repairPreview = null,
        Action? download = null,
        Func<CancellationToken, Task<Result<LaunchPlan>>>? freezeLaunchPlan = null,
        Action<HomeQuickAction>? executeQuickAction = null,
        RepairPreviewViewModel? repairPreviewState = null,
        IHomeLaunchPlanHost? launchPlanHost = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        this.executeQuickAction = executeQuickAction;
        RepairPreview = repairPreviewState ?? new RepairPreviewViewModel();
        if (freezeLaunchPlan is null && state.LaunchContext is not null && launchPlanHost is not null)
        {
            freezeLaunchPlan = cancellationToken => launchPlanHost.FreezeAsync(
                state.LaunchContext,
                cancellationToken);
        }
        LaunchCard = new LaunchCardViewModel(
            state.LaunchCard,
            freezeLaunchPlan,
            navigation,
            repairPreview,
            download);
        AllModules = state.Modules
            .OrderBy(static module => module.Order)
            .Select(module => new HomeModuleViewModel(
                module,
                module.Module == HomeModuleId.QuickActions ? state.QuickActions : [],
                this.executeQuickAction))
            .ToArray();
        VisibleModules = AllModules.Where(static module => module.IsVisible).ToArray();
        OrderedItems = [LaunchCard, .. VisibleModules.Cast<object>()];
        ActiveTasks = state.ActiveTasks;
        SelectedGameRootId = state.SelectedGameRootId;
        SelectedVersionFolder = state.SelectedVersionFolder;
    }

    public LaunchCardViewModel LaunchCard { get; }

    public RepairPreviewViewModel RepairPreview { get; }

    public IReadOnlyList<HomeModuleViewModel> AllModules { get; }

    public IReadOnlyList<HomeModuleViewModel> VisibleModules { get; }

    /// <summary>
    /// The first item is always the fixed launch card. Modules are appended only
    /// after applying their validated visibility/order settings.
    /// </summary>
    public IReadOnlyList<object> OrderedItems { get; }

    public IReadOnlyList<Lacertae.Domain.Operations.OperationSnapshot> ActiveTasks { get; }

    public string? SelectedGameRootId { get; }

    public string? SelectedVersionFolder { get; }

    public bool CanLaunch => LaunchCard.CanLaunch;

    public bool IsLaunchDisabled => !CanLaunch;
}
