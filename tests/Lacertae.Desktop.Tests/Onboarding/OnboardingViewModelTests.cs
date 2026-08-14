using Lacertae.Application.Java;
using Lacertae.Application.Storage;
using Lacertae.Desktop.ViewModels.Onboarding;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Results;
using Lacertae.Domain.Storage;

namespace Lacertae.Desktop.Tests.Onboarding;

public sealed class OnboardingViewModelTests
{
    [Fact]
    public void FirstUseStartsWithFourDurableSteps()
    {
        OnboardingViewModel viewModel = new(
            new FakeOnboardingUseCases(),
            new OnboardingDataRootSnapshot(DataRootMode.UserProfile, "使用 Windows 用户数据目录"));

        Assert.Equal(
            [OnboardingStep.DataLocation, OnboardingStep.GameRoot, OnboardingStep.Account, OnboardingStep.JavaVersion],
            viewModel.Steps.Select(static step => step.Step));
        Assert.Equal(OnboardingStep.DataLocation, viewModel.CurrentStep);
        Assert.False(viewModel.IsComplete);
        Assert.False(viewModel.RequiresMicrosoftConfiguration);
    }

    [Fact]
    public async Task EmptyRootAndOfflineAccountCanCompleteWithoutMicrosoftSetup()
    {
        FakeOnboardingUseCases useCases = new();
        OnboardingViewModel viewModel = new(useCases, new OnboardingDataRootSnapshot(DataRootMode.UserProfile, "用户目录"));

        Assert.True((await viewModel.AddGameRootAsync("C:\\Games\\empty", allowEmpty: true, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await viewModel.AddOfflineAccountAsync("Alex", TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await viewModel.SelectVersionAsync("1.21.1", TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await viewModel.SelectJavaAsync("C:\\Java\\java.exe", TestContext.Current.CancellationToken)).IsSuccess);

        Assert.True(viewModel.IsComplete);
        Assert.True(viewModel.CanLaunch);
        Assert.False(viewModel.RequiresMicrosoftConfiguration);
        Assert.Equal("root-1", viewModel.DurableState.GameRootId);
        Assert.Equal("account-1", viewModel.DurableState.AccountId);
        Assert.Equal("1.21.1", viewModel.DurableState.VersionId);
        Assert.True(useCases.AllowEmptyWasRequested);
    }

    [Fact]
    public async Task ClosingOnboardingPreservesCompletedStepsAndClearsDraftSecrets()
    {
        OnboardingViewModel viewModel = new(
            new FakeOnboardingUseCases(),
            new OnboardingDataRootSnapshot(DataRootMode.LocalToExecutable, "便携数据目录"));
        await viewModel.AddGameRootAsync("C:\\Games\\empty", allowEmpty: true, TestContext.Current.CancellationToken);
        viewModel.OfflinePlayerNameDraft = "partially typed";
        viewModel.MicrosoftSecretDraft = "must-not-persist";

        viewModel.Close();

        Assert.Equal("root-1", viewModel.DurableState.GameRootId);
        Assert.Null(viewModel.OfflinePlayerNameDraft);
        Assert.Null(viewModel.MicrosoftSecretDraft);
        Assert.False(viewModel.IsOpen);
        Assert.True(viewModel.DataRootModeIsInformational);
    }

    [Fact]
    public void DeferSetupEntersDisabledLaunchState()
    {
        OnboardingViewModel viewModel = new(
            new FakeOnboardingUseCases(),
            new OnboardingDataRootSnapshot(DataRootMode.UserProfile, "用户目录"));

        viewModel.DeferSetup();

        Assert.True(viewModel.IsComplete);
        Assert.True(viewModel.IsDeferredSetup);
        Assert.False(viewModel.CanLaunch);
        Assert.True(viewModel.IsLaunchDisabled);
    }

    [Fact]
    public void ReopenRaisesVisibilityNotificationEvenWhenAlreadyOpen()
    {
        OnboardingViewModel viewModel = new(
            new FakeOnboardingUseCases(),
            new OnboardingDataRootSnapshot(DataRootMode.UserProfile, "用户目录"));
        bool notified = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(OnboardingViewModel.IsOpen))
            {
                notified = true;
            }
        };

        viewModel.Reopen();

        Assert.True(viewModel.IsOpen);
        Assert.True(notified);
    }

    private sealed class FakeOnboardingUseCases : IOnboardingUseCases
    {
        public bool AllowEmptyWasRequested { get; private set; }

        public Task<Result<GameRoot>> AddGameRootAsync(string path, bool allowEmpty, CancellationToken cancellationToken)
        {
            AllowEmptyWasRequested = allowEmpty;
            return Task.FromResult(Result<GameRoot>.Success(new GameRoot(
                "root-1",
                path,
                "empty",
                GameRootAvailability.Available,
                DateTimeOffset.UtcNow)));
        }

        public Task<Result<Account>> AddOfflineAccountAsync(string playerName, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Account>.Success(new Account(
                "account-1",
                new AccountIdentity(AccountIdentity.OfflineProviderId, "5627dd98-e6be-3c21-b8a8-e92344183641"),
                AccountType.Offline,
                playerName,
                null,
                null,
                AccountStatus.Active,
                null)));

        public Task<Result<OnboardingVersionSelection>> SelectVersionAsync(string versionId, CancellationToken cancellationToken) =>
            Task.FromResult(Result<OnboardingVersionSelection>.Success(new OnboardingVersionSelection(versionId, 21, true)));

        public Task<Result<OnboardingJavaSelection>> SelectJavaAsync(
            string executablePath,
            int requiredMajor,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<OnboardingJavaSelection>.Success(new OnboardingJavaSelection(executablePath, requiredMajor, true)));
    }
}
