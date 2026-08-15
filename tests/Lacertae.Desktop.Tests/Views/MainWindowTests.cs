using Avalonia.Controls;
using Avalonia.Threading;
using Lacertae.Application.Accounts;
using Lacertae.Application.Home;
using Lacertae.Application.Startup;
using Lacertae.Desktop.ViewModels;
using Lacertae.Desktop.ViewModels.Accounts;
using Lacertae.Desktop.ViewModels.Onboarding;
using Lacertae.Desktop.Views;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Storage;

namespace Lacertae.Desktop.Tests.Views;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void ShellExposesStableNavigationAndDisabledLaunchAction()
    {
        MainWindow window = new();
        window.Show();

        Assert.Equal("Lacertae", window.Title);
        Assert.Equal(
            ["home", "accounts", "versions", "downloads", "resources", "tasks", "settings"],
            ((MainWindowViewModel)window.DataContext!).NavigationItems.Select(static item => item.RouteId));
        Assert.NotNull(window.FindControl<Button>("LaunchButton"));
        Assert.False(window.FindControl<Button>("LaunchButton")!.IsEnabled);
        Assert.Equal("未选择游戏版本", window.FindControl<TextBlock>("VersionSummary")!.Text);
    }

    [Fact]
    public void AccountsRouteIsNavigableAndHasDedicatedContentState()
    {
        MainWindowViewModel viewModel = new();

        Assert.True(viewModel.TryNavigate(LauncherRouteIds.Accounts));
        Assert.Equal(LauncherRouteIds.Accounts, viewModel.CurrentRouteId);
        Assert.True(viewModel.IsAccountsPage);
        Assert.False(viewModel.IsAccountsContentVisible);
    }

    [AvaloniaFact]
    public void ConfiguredAccountsRouteRendersDedicatedAccountPanel()
    {
        MainWindow window = new();
        window.Show();
        MainWindowViewModel viewModel = (MainWindowViewModel)window.DataContext!;
        viewModel.ConfigureAccounts(new AccountsViewModel(
            new AccountPageOperations(
                _ => Task.FromResult<IReadOnlyList<Account>>([]),
                (_, _) => Task.FromResult(Result<Account>.Failure(new Problem(
                    "TEST", ProblemStage.Authentication, "test", false, "test", []))),
                null,
                (_, _) => Task.FromResult(Result.Success()),
                null,
                (_, _) => Task.FromResult(Result.Success())),
            new EmptyAvatarCache()));
        viewModel.Navigate(LauncherRouteIds.Accounts);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsAccountsContentVisible);
        Assert.True(window.FindControl<Control>("AccountsContentPanel")!.IsEffectivelyVisible);
    }

    [Fact]
    public void CreatingMainWindowBeforeStartupIsRejected()
    {
        using Lacertae.Desktop.CompositionRoot root = new();

        Assert.Throws<InvalidOperationException>(root.CreateMainWindow);
    }

    [Fact]
    public void HomeQuickActionExecutesThroughTypedRoute()
    {
        MainWindowViewModel viewModel = new();
        HomeQuickAction action = viewModel.Home.VisibleModules
            .Single(module => module.Module == Lacertae.Domain.Home.HomeModuleId.QuickActions)
            .QuickActions.First(static action => action.Id == HomeQuickActionId.OpenSaves);

        viewModel.Home.VisibleModules
            .Single(module => module.Module == Lacertae.Domain.Home.HomeModuleId.QuickActions)
            .ExecuteQuickActionCommand.Execute(action);

        Assert.Equal("resources", viewModel.CurrentRouteId);
    }

    [Fact]
    public void RepairPreviewRequiresExplicitConfirmationAndDoesNotStartDownload()
    {
        MainWindowViewModel viewModel = new();

        viewModel.OpenRepairPreviewCommand.Execute(null);

        Assert.True(viewModel.IsRepairPreviewOpen);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.RepairPreviewSummary));
        Assert.Equal(LauncherRouteIds.Downloads, viewModel.CurrentRouteId);
        Assert.False(viewModel.ConfirmRepairDownloadCommand.CanExecute(null));
        Assert.False(viewModel.CanConfirmRepairDownload);
    }

    [AvaloniaFact]
    public void RepairPreviewPanelIsVisibleOnDownloadsAndConfirmationIsDisabled()
    {
        MainWindow window = new();
        window.Show();
        MainWindowViewModel viewModel = (MainWindowViewModel)window.DataContext!;

        viewModel.OpenRepairPreviewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(LauncherRouteIds.Downloads, viewModel.CurrentRouteId);
        Assert.True(window.FindControl<Button>("ConfirmRepairDownloadButton")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<Button>("ConfirmRepairDownloadButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public void CompactNavigationIsUsedBelowNineHundredLogicalPixels()
    {
        MainWindow window = new();
        window.Width = 880;
        window.Height = 640;
        window.Show();

        MainWindowViewModel viewModel = (MainWindowViewModel)window.DataContext!;

        Assert.True(viewModel.IsCompactNavigation);
        Assert.True(window.FindControl<ItemsControl>("CompactNavigationItems")!.IsVisible);
        Assert.False(window.FindControl<ItemsControl>("WideNavigationItems")!.IsVisible);
    }

    [AvaloniaFact]
    public void NavigationReturnsFocusToPageHeading()
    {
        MainWindow window = new();
        window.Show();
        Button settings = window.FindControl<Button>("SettingsNavigationButton")!;

        settings.Focus();
        settings.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("settings", ((MainWindowViewModel)window.DataContext!).CurrentRouteId);
        Assert.True(window.FindControl<TextBlock>("PageHeading")!.IsFocused);
    }

    [AvaloniaFact]
    public void CompactWindowKeepsLaunchAndSettingsActionsVisible()
    {
        MainWindow window = new();
        window.Width = 1024;
        window.Height = 640;
        window.Show();

        Assert.True(window.FindControl<Button>("LaunchButton")!.IsEffectivelyVisible);
        Assert.True(window.FindControl<Button>("SettingsNavigationButton")!.IsEffectivelyVisible);
    }

    [Fact]
    public void MissingAccountOrVersionKeepsFirstUseFlowVisibleEvenWhenRootExists()
    {
        MainWindowViewModel viewModel = new();
        viewModel.ApplyStartupState(new StartupState(
            new DataRoot(DataRootMode.UserProfile, "C:\\Roaming", "C:\\Local"),
            LauncherSettings.Default with { SelectedGameRootId = "root-1" },
            [new GameRoot("root-1", "C:\\Games\\.minecraft", "Minecraft", GameRootAvailability.Available, null)]));

        Assert.True(viewModel.IsOnboardingVisible);
        Assert.Equal("root-1", viewModel.Onboarding.DurableState.GameRootId);
        Assert.Null(viewModel.Onboarding.DurableState.AccountId);
    }

    [AvaloniaFact]
    public void OnboardingOverlayHidesShellNavigationUntilClosed()
    {
        MainWindow window = new();
        window.Show();
        MainWindowViewModel viewModel = (MainWindowViewModel)window.DataContext!;
        viewModel.ApplyStartupState(new StartupState(
            new DataRoot(DataRootMode.UserProfile, "C:\\Roaming", "C:\\Local"),
            LauncherSettings.Default,
            []));

        Assert.False(window.FindControl<ItemsControl>("WideNavigationItems")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<ItemsControl>("CompactNavigationItems")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<Button>("SettingsNavigationButton")!.IsEffectivelyVisible);
        viewModel.Onboarding.Close();

        Assert.True(viewModel.IsShellVisible);
    }

    [Fact]
    public void FourSelectedSettingStringsDoNotHideOnboardingWithoutVerifiedPreflight()
    {
        MainWindowViewModel viewModel = new();
        viewModel.ApplyStartupState(
            new StartupState(
                new DataRoot(DataRootMode.UserProfile, "C:\\Roaming", "C:\\Local"),
                LauncherSettings.Default with
                {
                    SelectedGameRootId = "root-1",
                    DefaultAccountId = "account-1",
                    SelectedVersionFolder = "1.21.1",
                    GlobalJavaPath = "C:\\Java\\java.exe",
                },
                [new GameRoot("root-1", "C:\\Games\\.minecraft", "Minecraft", GameRootAvailability.Available, null)]),
            preflightState: new OnboardingDurableState(
                "root-1",
                "account-1",
                "1.21.1",
                "C:\\Java\\java.exe",
                21,
                VersionIsInstalled: false,
                HasCompatibleJava: false,
                IsDeferredSetup: false));

        Assert.True(viewModel.IsOnboardingVisible);
        Assert.False(viewModel.CanLaunch);
    }

    [Fact]
    public async Task CompletingOnboardingHidesOverlayAndOffersTypedReopenCommand()
    {
        MainWindowViewModel viewModel = new();
        viewModel.ApplyStartupState(
            new StartupState(
                new DataRoot(DataRootMode.UserProfile, "C:\\Roaming", "C:\\Local"),
                LauncherSettings.Default,
                []),
            new FakeOnboardingUseCases());

        await viewModel.Onboarding.AddGameRootAsync(
            "C:\\Games\\empty",
            allowEmpty: true,
            TestContext.Current.CancellationToken);
        await viewModel.Onboarding.AddOfflineAccountAsync(
            "Alex",
            TestContext.Current.CancellationToken);
        await viewModel.Onboarding.SelectVersionAsync(
            "1.21.1",
            TestContext.Current.CancellationToken);
        await viewModel.Onboarding.SelectJavaAsync(
            "C:\\Java\\java.exe",
            TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsOnboardingVisible);
        Assert.True(viewModel.CanOpenOnboarding);
        Assert.True(viewModel.CanLaunch);

        viewModel.OpenOnboardingCommand.Execute(null);

        Assert.True(viewModel.IsOnboardingVisible);
        Assert.True(viewModel.Onboarding.IsOpen);
    }

    private sealed class FakeOnboardingUseCases : IOnboardingUseCases
    {
        public Task<Result<GameRoot>> AddGameRootAsync(
            string path,
            bool allowEmpty,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<GameRoot>.Success(new GameRoot(
                "root-1",
                path,
                "empty",
                GameRootAvailability.Available,
                DateTimeOffset.UtcNow)));

        public Task<Result<Account>> AddOfflineAccountAsync(
            string playerName,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<Account>.Success(new Account(
                "account-1",
                new AccountIdentity(AccountIdentity.OfflineProviderId, "5627dd98-e6be-3c21-b8a8-e92344183641"),
                AccountType.Offline,
                playerName,
                null,
                null,
                AccountStatus.Active,
                null)));

        public Task<Result<OnboardingVersionSelection>> SelectVersionAsync(
            string versionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<OnboardingVersionSelection>.Success(
                new OnboardingVersionSelection(versionId, 21, true)));

        public Task<Result<OnboardingJavaSelection>> SelectJavaAsync(
            string executablePath,
            int requiredMajor,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<OnboardingJavaSelection>.Success(
                new OnboardingJavaSelection(executablePath, requiredMajor, true)));
    }

    private sealed class EmptyAvatarCache : IAvatarCache
    {
        public Task<Result<AvatarCacheResult>> RefreshAsync(Uri? skinUri, CancellationToken cancellationToken) =>
            Task.FromResult(Result<AvatarCacheResult>.Success(new AvatarCacheResult(null, true, DateTimeOffset.UtcNow)));

        public string? ResolvePath(string? cacheKey) => null;
    }
}
