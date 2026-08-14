using Avalonia.Controls;
using Lacertae.Application.Home;
using Lacertae.Desktop.ViewModels;
using Lacertae.Desktop.ViewModels.Home;
using Lacertae.Desktop.Views;
using Lacertae.Desktop.Views.Home;
using Lacertae.Domain.Home;

namespace Lacertae.Desktop.Tests.Home;

public sealed class HomeViewTests
{
    [AvaloniaFact]
    public void LaunchCardIsRenderedBeforeScrollableModuleGrid()
    {
        HomeView view = new()
        {
            DataContext = new HomeViewModel(CreateState()),
        };
        Window host = new() { Content = view };
        host.Show();

        Assert.NotNull(view.FindControl<Border>("LaunchCard"));
        Assert.NotNull(view.FindControl<Button>("LaunchButton"));
        Assert.True(view.FindControl<Border>("LaunchCard")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<ItemsControl>("HomeModules")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void LaunchButtonStaysVisibleWhenWindowIsCompact()
    {
        HomeView view = new()
        {
            Width = 520,
            Height = 280,
            DataContext = new HomeViewModel(CreateState()),
        };
        Window host = new() { Content = view };
        host.Show();

        Assert.True(view.FindControl<Button>("LaunchButton")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void HomeRepairPreviewShowsDisabledConfirmation()
    {
        HomeViewModel viewModel = new(CreateState());
        HomeView view = new() { DataContext = viewModel };
        Window host = new() { Content = view };
        host.Show();

        viewModel.RepairPreview.Open();

        Assert.True(view.FindControl<Button>("HomeConfirmRepairDownloadButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("HomeConfirmRepairDownloadButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public void MainWindowHomeRouteUsesTypedHomeView()
    {
        MainWindow window = new();
        window.Show();

        MainWindowViewModel viewModel = (MainWindowViewModel)window.DataContext!;

        Assert.True(viewModel.IsHomePage);
        Assert.True(window.FindControl<ContentControl>("HomeContentPanel")!.IsEffectivelyVisible);
        Assert.IsType<HomeView>(window.FindControl<ContentControl>("HomeContentPanel")!.Content);
    }

    [AvaloniaFact]
    public void GenericPageAndHomePanelAreMutuallyExclusive()
    {
        MainWindow window = new();
        window.Show();
        MainWindowViewModel viewModel = (MainWindowViewModel)window.DataContext!;

        Assert.True(viewModel.IsHomeContentVisible);
        Assert.False(window.FindControl<ScrollViewer>("ContentPanel")!.IsEffectivelyVisible);

        viewModel.Navigate(LauncherRouteIds.Settings);

        Assert.False(viewModel.IsHomeContentVisible);
        Assert.True(window.FindControl<ScrollViewer>("ContentPanel")!.IsEffectivelyVisible);
    }

    private static HomeState CreateState() => new(
        new HomeLaunchCardState("1.21.1", "Alex", "Java 21 · Fixture", 2048, false, []),
        [
            new HomeModuleState(HomeModuleId.RecentVersions, 0, true, "最近版本", "", false, null),
            new HomeModuleState(HomeModuleId.ActiveTasks, 1, true, "活动任务", "", false, null),
            new HomeModuleState(HomeModuleId.QuickActions, 2, true, "快捷操作", "", false, null),
            new HomeModuleState(HomeModuleId.ReleaseNotes, 3, true, "发行说明", "", false, null),
        ],
        [],
        [new HomeQuickAction(HomeQuickActionId.OpenSaves, "打开存档")],
        "root-1",
        "1.21.1");
}
