using Lacertae.Application.Accounts;
using Lacertae.Desktop.ViewModels.Accounts;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.Tests.Accounts;

public sealed class AccountsViewModelTests
{
    [Fact]
    public async Task AccountRowsExposeTypeStatusAndOnlyResolvedLocalAvatarPaths()
    {
        Account offline = OfflineAccount("offline-id", "Alex");
        Account microsoft = MicrosoftAccount("microsoft-id", "Steve", "avatar-key");
        AccountsViewModel viewModel = CreateViewModel(
            [offline, microsoft],
            avatarPath: key => key == "avatar-key" ? "C:\\Lacertae\\avatars\\avatar.png" : null);

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        AccountItemViewModel offlineRow = Assert.Single(viewModel.Accounts, row => row.Id == offline.Id);
        AccountItemViewModel microsoftRow = Assert.Single(viewModel.Accounts, row => row.Id == microsoft.Id);
        Assert.Equal("离线", offlineRow.AccountTypeLabel);
        Assert.Equal("正版", microsoftRow.AccountTypeLabel);
        Assert.Equal("可用", offlineRow.StatusLabel);
        Assert.Equal("可用", microsoftRow.StatusLabel);
        Assert.Null(offlineRow.AvatarPath);
        Assert.True(offlineRow.UsesPlaceholder);
        Assert.Equal("C:\\Lacertae\\avatars\\avatar.png", microsoftRow.AvatarPath);
        Assert.False(microsoftRow.UsesPlaceholder);
    }

    [Fact]
    public async Task SelectingDefaultUpdatesResolvedAccountSummaryImmediately()
    {
        Account first = OfflineAccount("first-id", "Alex");
        Account second = OfflineAccount("second-id", "Sam");
        List<string> selectedDefaults = [];
        AccountsViewModel viewModel = CreateViewModel(
            [first, second],
            defaultAccountId: first.Id,
            setDefault: (id, _) =>
            {
                selectedDefaults.Add(id);
                return Task.FromResult(Result.Success());
            });

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        AccountItemViewModel target = viewModel.Accounts.Single(row => row.Id == second.Id);

        Result<Unit> result = await viewModel.SetDefaultAccountAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal([second.Id], selectedDefaults);
        Assert.Equal(second.PlayerName, viewModel.ResolvedAccountSummary);
        Assert.True(target.IsDefault);
        Assert.True(target.IsResolved);
        Assert.False(viewModel.Accounts.Single(row => row.Id == first.Id).IsDefault);
    }

    [Fact]
    public async Task VersionOverrideRemainsResolvedWhenDefaultChanges()
    {
        Account defaultAccount = OfflineAccount("default-id", "Alex");
        Account versionAccount = OfflineAccount("version-id", "Sam");
        AccountsViewModel viewModel = CreateViewModel(
            [defaultAccount, versionAccount],
            defaultAccountId: defaultAccount.Id,
            versionOverrideAccountId: versionAccount.Id);

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        AccountItemViewModel versionRow = viewModel.Accounts.Single(row => row.Id == versionAccount.Id);
        Assert.Equal(versionAccount.PlayerName, viewModel.ResolvedAccountSummary);
        Assert.True(versionRow.IsVersionOverride);
        Assert.True(versionRow.IsResolved);
        Assert.Equal("此版本专用", viewModel.ResolvedAccountSourceLabel);

        Result<Unit> result = await viewModel.SetDefaultAccountAsync(
            viewModel.Accounts.Single(row => row.Id == defaultAccount.Id),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(versionAccount.PlayerName, viewModel.ResolvedAccountSummary);
        Assert.True(versionRow.IsResolved);
    }

    [Fact]
    public void MissingMicrosoftRegistrationDisablesInteractiveLoginWithoutASecretDraft()
    {
        AccountsViewModel viewModel = CreateViewModel([], microsoftLoginConfigured: false);

        Assert.False(viewModel.IsMicrosoftLoginConfigured);
        Assert.False(viewModel.CanMicrosoftLogin);
        Assert.Contains("未配置", viewModel.MicrosoftLoginStatus, StringComparison.Ordinal);
        Assert.Null(viewModel.MicrosoftSecretDraft);
    }

    [Fact]
    public void InvalidMicrosoftRegistrationShowsSafeConfigurationCode()
    {
        AccountsViewModel viewModel = CreateViewModel(
            [],
            microsoftConfigurationErrorCode: "AUTH_MICROSOFT_CONFIG_INVALID");

        Assert.False(viewModel.CanMicrosoftLogin);
        Assert.Contains("AUTH_MICROSOFT_CONFIG_INVALID", viewModel.MicrosoftLoginStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("client", viewModel.MicrosoftLoginStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MicrosoftLoginShowsCancellableBrowserStateWithoutExposingCallbackText()
    {
        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        AccountsViewModel viewModel = CreateViewModel(
            [],
            microsoftLoginConfigured: true,
            addMicrosoft: async token =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancellationObserved.SetResult();
                    throw;
                }

                throw new InvalidOperationException("The fake browser did not cancel.");
            });

        Task<Result<Account>> login = viewModel.AddMicrosoftAccountAsync(cancellation.Token);
        Assert.True(viewModel.IsMicrosoftLoginInProgress);
        Assert.Contains("等待浏览器登录", viewModel.MicrosoftLoginStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", viewModel.MicrosoftLoginStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", viewModel.MicrosoftLoginStatus, StringComparison.OrdinalIgnoreCase);

        viewModel.CancelMicrosoftLogin();
        await cancellationObserved.Task;
        Result<Account> result = await login;

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_CANCELLED", result.Problem?.Code);
        Assert.False(viewModel.IsMicrosoftLoginInProgress);
        Assert.DoesNotContain("http://", viewModel.MicrosoftLoginStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", viewModel.MicrosoftLoginStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteRequiresExactPlayerNameAndDisablesRowWhileDeleting()
    {
        Account account = OfflineAccount("delete-id", "Alex");
        TaskCompletionSource<Result<Unit>> deletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AccountsViewModel viewModel = CreateViewModel(
            [account],
            delete: (_, _) => deletion.Task);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        AccountItemViewModel row = Assert.Single(viewModel.Accounts);

        viewModel.BeginDelete(row);
        Result<Unit> mismatch = await viewModel.ConfirmDeleteAsync(
            "alex",
            TestContext.Current.CancellationToken);
        Assert.False(mismatch.IsSuccess);
        Assert.Equal("ACCOUNT_DELETE_CONFIRMATION_MISMATCH", mismatch.Problem?.Code);
        Assert.False(row.IsDeleting);

        Task<Result<Unit>> pending = viewModel.ConfirmDeleteAsync(
            "Alex",
            TestContext.Current.CancellationToken);
        Assert.True(row.IsDeleting);
        Assert.False(row.IsEnabled);
        deletion.SetResult(Result.Success());
        Result<Unit> result = await pending;

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Empty(viewModel.Accounts);
        Assert.Equal("未选择账号", viewModel.ResolvedAccountSummary);
    }

    private static AccountsViewModel CreateViewModel(
        IReadOnlyList<Account> accounts,
        string? defaultAccountId = null,
        string? versionOverrideAccountId = null,
        bool microsoftLoginConfigured = false,
        string? microsoftConfigurationErrorCode = null,
        Func<string?, string?>? avatarPath = null,
        Func<string, CancellationToken, Task<Result<Unit>>>? setDefault = null,
        Func<string, CancellationToken, Task<Result<Unit>>>? delete = null,
        Func<CancellationToken, Task<Result<Account>>>? addMicrosoft = null)
    {
        return new AccountsViewModel(
            new AccountPageOperations(
                _ => Task.FromResult(accounts),
                (name, _) => Task.FromResult(Result<Account>.Success(
                    OfflineAccount("new-id", name))),
                addMicrosoft,
                setDefault ?? ((_, _) => Task.FromResult(Result.Success())),
                null,
                delete ?? ((_, _) => Task.FromResult(Result.Success()))),
            new FakeAvatarCache(avatarPath),
            defaultAccountId,
            versionOverrideAccountId,
            gameRootId: "root-id",
            versionFolder: "1.21.1",
            microsoftLoginConfigured: microsoftLoginConfigured,
            microsoftConfigurationErrorCode: microsoftConfigurationErrorCode);
    }

    private static Account OfflineAccount(string id, string name) => new(
        id,
        new AccountIdentity(AccountIdentity.OfflineProviderId, id.PadLeft(32, '0')),
        AccountType.Offline,
        name,
        null,
        null,
        AccountStatus.Active,
        null);

    private static Account MicrosoftAccount(string id, string name, string avatarKey) => new(
        id,
        new AccountIdentity(AccountIdentity.MicrosoftProviderId, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        AccountType.Microsoft,
        name,
        avatarKey,
        "0123456789abcdef0123456789abcdef",
        AccountStatus.Active,
        DateTimeOffset.UtcNow);

    private sealed class FakeAvatarCache(Func<string?, string?>? pathFactory) : IAvatarCache
    {
        public Task<Result<AvatarCacheResult>> RefreshAsync(Uri? skinUri, CancellationToken cancellationToken) =>
            Task.FromResult(Result<AvatarCacheResult>.Success(new AvatarCacheResult(null, true, DateTimeOffset.UtcNow)));

        public string? ResolvePath(string? cacheKey) => pathFactory?.Invoke(cacheKey);
    }
}
