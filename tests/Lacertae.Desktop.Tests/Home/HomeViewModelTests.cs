using Lacertae.Application.Home;
using Lacertae.Desktop.ViewModels.Home;
using Lacertae.Domain.Home;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.Tests.Home;

public sealed class HomeViewModelTests
{
    [Fact]
    public void LaunchCardIsFirstAndHiddenModulesDoNotReplaceIt()
    {
        HomeState state = CreateState(
            canLaunch: true,
            modules:
            [
                new HomeModuleState(HomeModuleId.RecentVersions, 3, false, "最近版本", "", false, null),
                new HomeModuleState(HomeModuleId.ActiveTasks, 1, true, "活动任务", "", false, null),
                new HomeModuleState(HomeModuleId.QuickActions, 0, true, "快捷操作", "", false, null),
                new HomeModuleState(HomeModuleId.ReleaseNotes, 2, true, "发行说明", "", false, null),
            ]);
        HomeViewModel viewModel = new(state);

        Assert.IsType<LaunchCardViewModel>(viewModel.OrderedItems[0]);
        Assert.Equal(
            [HomeModuleId.QuickActions, HomeModuleId.ActiveTasks, HomeModuleId.ReleaseNotes],
            viewModel.VisibleModules.Select(static module => module.Module));
        Assert.DoesNotContain(viewModel.OrderedItems, item => item is HomeModuleViewModel module && module.Module == HomeModuleId.RecentVersions);
    }

    [Fact]
    public void MissingRequirementNavigatesToTypedRouteAndDamagedFilesOnlyOpenRepairPreview()
    {
        HomeState state = CreateState(
            canLaunch: false,
            requirements:
            [
                new HomeLaunchRequirement(HomeLaunchRequirementId.Account, "账号", "请添加或选择一个账号", "settings", false),
                new HomeLaunchRequirement(HomeLaunchRequirementId.Files, "文件", "请检查文件并预览修复", "downloads", true),
            ]);
        List<string> routes = [];
        int repairPreviewCount = 0;
        int downloadCount = 0;
        HomeViewModel viewModel = new(
            state,
            navigation: routes.Add,
            repairPreview: () => repairPreviewCount++,
            download: () => downloadCount++);

        viewModel.LaunchCard.SelectRequirementCommand.Execute(state.LaunchCard.Requirements[0]);
        viewModel.LaunchCard.SelectRequirementCommand.Execute(state.LaunchCard.Requirements[1]);

        Assert.Equal(["settings"], routes);
        Assert.Equal(1, repairPreviewCount);
        Assert.Equal(0, downloadCount);
    }

    [Fact]
    public async Task LaunchCommandRoutesMissingRequirementAndOpensDamagedRepairPreview()
    {
        HomeState missing = CreateState(
            canLaunch: false,
            requirements:
            [
                new HomeLaunchRequirement(HomeLaunchRequirementId.Account, "账号", "请添加或选择一个账号", HomeRouteIds.Settings, false),
            ]);
        HomeState damaged = CreateState(
            canLaunch: false,
            requirements:
            [
                new HomeLaunchRequirement(HomeLaunchRequirementId.Files, "文件", "请检查文件并预览修复", HomeRouteIds.Downloads, true),
            ]);
        List<string> routes = [];
        int repairPreviewCount = 0;
        HomeViewModel missingViewModel = new(missing, navigation: routes.Add);
        HomeViewModel damagedViewModel = new(damaged, repairPreview: () => repairPreviewCount++);

        Assert.True(missingViewModel.LaunchCard.LaunchCommand.CanExecute(null));
        missingViewModel.LaunchCard.LaunchCommand.Execute(null);
        Assert.Equal([HomeRouteIds.Settings], routes);

        Assert.True(damagedViewModel.LaunchCard.LaunchCommand.CanExecute(null));
        damagedViewModel.LaunchCard.LaunchCommand.Execute(null);
        await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(1, repairPreviewCount);
    }

    [Fact]
    public async Task LaunchClickRequestsAFreshFrozenPlanWithoutHoldingAnAuthSession()
    {
        HomeState state = CreateState(canLaunch: true);
        int freezeCount = 0;
        HomeViewModel viewModel = new(
            state,
            freezeLaunchPlan: _ =>
            {
                freezeCount++;
                return Task.FromResult(Result<LaunchPlan>.Failure(new Lacertae.Domain.Problems.Problem(
                    "LAUNCH_PLAN_INVALID",
                    Lacertae.Domain.Problems.ProblemStage.LaunchPlanning,
                    "problem.launch.plan.invalid",
                    false,
                    "test",
                    ["action.launch.review_settings"])));
            });

        await viewModel.LaunchCard.ActivateLaunchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, freezeCount);
        Assert.False(LaunchCardViewModel.HasCachedAuthSession);
    }

    [Fact]
    public void ModuleErrorIsRenderedAsOwnCardWithoutDisablingLaunch()
    {
        HomeState state = CreateState(
            canLaunch: true,
            modules:
            [
                new HomeModuleState(HomeModuleId.RecentVersions, 0, true, "最近版本", "", true, "RECENT_VERSIONS_UNAVAILABLE"),
                new HomeModuleState(HomeModuleId.ActiveTasks, 1, true, "活动任务", "", false, null),
                new HomeModuleState(HomeModuleId.QuickActions, 2, true, "快捷操作", "", false, null),
                new HomeModuleState(HomeModuleId.ReleaseNotes, 3, true, "发行说明", "", false, null),
            ]);
        HomeViewModel viewModel = new(
            state,
            freezeLaunchPlan: _ => Task.FromResult(Result<LaunchPlan>.Failure(new Lacertae.Domain.Problems.Problem(
                "LAUNCH_PLAN_INVALID",
                Lacertae.Domain.Problems.ProblemStage.LaunchPlanning,
                "problem.launch.plan.invalid",
                false,
                "test",
                ["action.launch.review_settings"]))));

        Assert.True(viewModel.VisibleModules[0].HasError);
        Assert.True(viewModel.LaunchCard.CanLaunch);
    }

    private static HomeState CreateState(
        bool canLaunch,
        IReadOnlyList<HomeModuleState>? modules = null,
        IReadOnlyList<HomeLaunchRequirement>? requirements = null) =>
        new(
            new HomeLaunchCardState("1.21.1", "Alex", "Java 21 · Fixture", 2048, canLaunch, requirements ?? []),
            modules ??
            [
                new HomeModuleState(HomeModuleId.RecentVersions, 0, true, "最近版本", "", false, null),
                new HomeModuleState(HomeModuleId.ActiveTasks, 1, true, "活动任务", "", false, null),
                new HomeModuleState(HomeModuleId.QuickActions, 2, true, "快捷操作", "", false, null),
                new HomeModuleState(HomeModuleId.ReleaseNotes, 3, true, "发行说明", "", false, null),
            ],
            [],
            [
                new HomeQuickAction(HomeQuickActionId.OpenSaves, "打开存档"),
                new HomeQuickAction(HomeQuickActionId.OpenVersionDirectory, "打开版本目录"),
                new HomeQuickAction(HomeQuickActionId.OpenLogs, "打开日志"),
            ],
            "root-1",
            "1.21.1");
}
