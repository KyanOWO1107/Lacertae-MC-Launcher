using System.Security.Cryptography;
using Lacertae.Application.Accounts;
using Lacertae.Application.Settings;
using Lacertae.Application.Versions;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Tests.Accounts;

public sealed class AccountSessionOrchestrationTests
{
    [Fact]
    public async Task AddMicrosoftWritesSecretBeforeProfileAndCleansItWhenProfileWriteFails()
    {
        MicrosoftLoginResult login = CreateLoginResult("Alex", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", [1, 2, 3]);
        FakeMicrosoftClient client = new(login);
        FakeSecretVault vault = new();
        FakeAccountRepository repository = new() { UpsertResult = Result<Unit>.Failure(Problem("DB_FAILED")) };

        var result = await new AddMicrosoftAccount(repository, vault, client, TimeProvider.System)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DB_FAILED", result.Problem?.Code);
        Assert.Equal(["write", "delete"], vault.Events);
        Assert.Equal(["upsert"], repository.Events);
        Assert.Empty(vault.Stored);
        Assert.Empty(login.Cache.Bytes.ToArray());
    }

    [Fact]
    public async Task AddMicrosoftUpdatesExistingIdentityWithoutCreatingDuplicate()
    {
        Account existing = new(
            "existing-account",
            new AccountIdentity(AccountIdentity.MicrosoftProviderId, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            AccountType.Microsoft,
            "OldName",
            "old-avatar",
            "old-secret",
            AccountStatus.Active,
            null);
        MicrosoftLoginResult login = CreateLoginResult("Alex", existing.Identity.ProfileUuid, [4, 5, 6]);
        FakeAccountRepository repository = new(existing);
        FakeSecretVault vault = new();

        var result = await new AddMicrosoftAccount(repository, vault, new FakeMicrosoftClient(login), TimeProvider.System)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(existing.Id, result.Value.Id);
        Assert.Equal("Alex", result.Value.PlayerName);
        Assert.Equal("existing-account", repository.Stored!.Id);
        Assert.Equal(1, repository.UpsertCount);
        Assert.Contains("old-secret", vault.DeletedReferences);
    }

    [Fact]
    public async Task AddMicrosoftStoresOnlyTheValidatedLocalAvatarCacheKey()
    {
        Uri skinUri = new("https://textures.minecraft.net/texture/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        MicrosoftLoginResult login = CreateLoginResult(
            "Alex",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            [7, 8, 9],
            skinUri);
        string avatarKey = new('a', 64);
        FakeAvatarCache avatarCache = new(new AvatarCacheResult(avatarKey, false, DateTimeOffset.UtcNow));

        Result<Account> result = await new AddMicrosoftAccount(
                new FakeAccountRepository(),
                new FakeSecretVault(),
                new FakeMicrosoftClient(login),
                TimeProvider.System,
                avatarCache)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(avatarKey, result.Value.AvatarCacheKey);
        Assert.Equal(skinUri, avatarCache.LastSkinUri);
        Assert.DoesNotContain("textures.minecraft.net", result.Value.PlayerName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshWritesRotatedCacheOnlyAfterSuccessfulRefresh()
    {
        Account account = MicrosoftAccount("microsoft-account", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "secret-ref");
        FakeAccountRepository repository = new(account);
        FakeSecretVault vault = new([10, 11]);
        MicrosoftLoginResult refreshed = CreateLoginResult("Alex", account.Identity.ProfileUuid, [20, 21]);
        FakeMicrosoftClient client = new(refreshed);

        var result = await new RefreshAccountSession(repository, vault, client, TimeProvider.System)
            .ExecuteAsync(account.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("[SECRET]", result.Value.AccessToken.ToString());
        Assert.Equal(["read", "write"], vault.Events);
        Assert.Equal(["refresh"], client.Events);
        Assert.Equal(["upsert"], repository.Events);
        Assert.Equal([20, 21], vault.Stored["secret-ref"]);
        Assert.Empty(refreshed.Cache.Bytes.ToArray());
    }

    [Fact]
    public async Task RefreshMarksReauthenticationRequiredWhenInteractionIsRequired()
    {
        Account account = MicrosoftAccount("microsoft-account", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "secret-ref");
        FakeAccountRepository repository = new(account);
        FakeSecretVault vault = new([10, 11]);
        FakeMicrosoftClient client = new(failure: ProblemResult("AUTH_SESSION_EXPIRED"));

        var result = await new RefreshAccountSession(repository, vault, client, TimeProvider.System)
            .ExecuteAsync(account.Id, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_SESSION_EXPIRED", result.Problem?.Code);
        Assert.Equal(AccountStatus.ReauthenticationRequired, repository.Stored!.Status);
        Assert.DoesNotContain("write", vault.Events);
    }

    [Fact]
    public async Task RefreshNeverConvertsMicrosoftFailureToOfflineSession()
    {
        Account account = MicrosoftAccount("microsoft-account", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "secret-ref");
        FakeAccountRepository repository = new(account);
        FakeSecretVault vault = new([10, 11]);
        FakeMicrosoftClient client = new(failure: ProblemResult("AUTH_NETWORK_FAILED"));

        var result = await new RefreshAccountSession(repository, vault, client, TimeProvider.System)
            .ExecuteAsync(account.Id, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_NETWORK_FAILED", result.Problem?.Code);
        Assert.DoesNotContain("offline", client.Events);
    }

    [Fact]
    public async Task ResolvePrefersActiveVersionOverrideOverActiveDefault()
    {
        Account defaultAccount = MicrosoftAccount("default", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "secret-a");
        Account versionAccount = MicrosoftAccount("version", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "secret-b");
        FakeAccountRepository repository = new(defaultAccount, versionAccount);
        FakeSettingsRepository settings = new(LauncherSettings.Default with { DefaultAccountId = defaultAccount.Id });
        FakeVersionOverrideRepository overrides = new(new VersionOverride(
            "root-1",
            "1.21.1",
            null,
            IsolationOverride.Inherit,
            versionAccount.Id,
            null,
            null,
            null,
            null,
            [],
            []));

        var result = await new ResolveAccountForVersion(repository, settings, overrides)
            .ExecuteAsync("root-1", "1.21.1", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(versionAccount.Id, result.Value.Id);
    }

    [Fact]
    public async Task ResolveReturnsAccountRequiredForMissingOrInactiveSelection()
    {
        FakeAccountRepository repository = new();
        FakeSettingsRepository settings = new(LauncherSettings.Default);
        FakeVersionOverrideRepository overrides = new();

        var result = await new ResolveAccountForVersion(repository, settings, overrides)
            .ExecuteAsync("root-1", "1.21.1", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_ACCOUNT_REQUIRED", result.Problem?.Code);
        Assert.Contains("action.account.select", result.Problem!.SuggestedActionKeys);
    }

    [Fact]
    public async Task SetVersionAccountPersistsTheAccountShownOnLaunchCard()
    {
        Account account = MicrosoftAccount("version-account", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "secret-ref");
        FakeAccountRepository accounts = new(account);
        FakeVersionOverrideRepository overrides = new();

        var result = await new SetVersionAccount(accounts, overrides)
            .ExecuteAsync("root-1", "1.21.1", account.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(account.Id, overrides.Stored.Single().AccountId);
    }

    private static MicrosoftLoginResult CreateLoginResult(string name, string uuid, byte[] cache, Uri? skinUri = null)
    {
        AuthSession session = new(name, uuid, new SensitiveString("access-token"), "msa", "123", DateTimeOffset.UtcNow.AddHours(1));
        return new MicrosoftLoginResult(name, uuid, session, skinUri, new SecretMaterial(cache));
    }

    private static Account MicrosoftAccount(string id, string uuid, string secretRef) => new(
        id,
        new AccountIdentity(AccountIdentity.MicrosoftProviderId, uuid),
        AccountType.Microsoft,
        "Alex",
        null,
        secretRef,
        AccountStatus.Active,
        DateTimeOffset.UtcNow.AddMinutes(-5));

    private static Result<MicrosoftLoginResult> ProblemResult(string code) =>
        Result<MicrosoftLoginResult>.Failure(Problem(code));

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.Authentication,
        "problem.auth.test",
        code == "AUTH_NETWORK_FAILED",
        Guid.NewGuid().ToString("N"),
        ["action.auth.retry"]);

    private sealed class FakeMicrosoftClient(MicrosoftLoginResult? login = null, Result<MicrosoftLoginResult>? failure = null) : IMicrosoftIdentityClient
    {
        private readonly MicrosoftLoginResult? login = login;
        private readonly Result<MicrosoftLoginResult>? failure = failure;

        public List<string> Events { get; } = [];

        public Task<Result<MicrosoftLoginResult>> SignInInteractivelyAsync(CancellationToken cancellationToken)
        {
            Events.Add("interactive");
            return Task.FromResult(failure ?? Result<MicrosoftLoginResult>.Success(login!));
        }

        public Task<Result<MicrosoftLoginResult>> RefreshSilentlyAsync(ReadOnlyMemory<byte> serializedCache, CancellationToken cancellationToken)
        {
            Events.Add("refresh");
            return Task.FromResult(failure ?? Result<MicrosoftLoginResult>.Success(login!));
        }
    }

    private sealed class FakeSecretVault(IReadOnlyList<byte>? initial = null) : ISecretVault
    {
        public Dictionary<string, byte[]> Stored { get; } = initial is null ? [] : new(StringComparer.Ordinal) { ["secret-ref"] = initial.ToArray() };
        public List<string> Events { get; } = [];
        public List<string> DeletedReferences { get; } = [];

        public Task<Result<Unit>> WriteAsync(string secretRef, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
        {
            Events.Add("write");
            Stored[secretRef] = secret.ToArray();
            return Task.FromResult(Result.Success());
        }

        public Task<Result<byte[]>> ReadAsync(string secretRef, CancellationToken cancellationToken)
        {
            Events.Add("read");
            return Task.FromResult(Stored.TryGetValue(secretRef, out byte[]? value)
                ? Result<byte[]>.Success(value.ToArray())
                : Result<byte[]>.Failure(Problem("SECRET_NOT_FOUND")));
        }

        public Task<Result<Unit>> DeleteAsync(string secretRef, CancellationToken cancellationToken)
        {
            Events.Add("delete");
            DeletedReferences.Add(secretRef);
            Stored.Remove(secretRef);
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FakeAvatarCache(AvatarCacheResult result) : IAvatarCache
    {
        public Uri? LastSkinUri { get; private set; }

        public Task<Result<AvatarCacheResult>> RefreshAsync(Uri? skinUri, CancellationToken cancellationToken)
        {
            LastSkinUri = skinUri;
            return Task.FromResult(Result<AvatarCacheResult>.Success(result));
        }

        public string? ResolvePath(string? cacheKey) => null;
    }

    private sealed class FakeAccountRepository(params Account[] accounts) : IAccountRepository
    {
        public List<Account> Accounts { get; } = [.. accounts];
        public Account? Stored => Accounts.FirstOrDefault();
        public int UpsertCount { get; private set; }
        public List<string> Events { get; } = [];
        public Result<Unit> UpsertResult { get; init; } = Result.Success();

        public Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Account>>(Accounts);

        public Task<Account?> GetAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(Accounts.FirstOrDefault(account => account.Id == accountId));

        public Task<Account?> FindByIdentityAsync(AccountIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult(Accounts.FirstOrDefault(account => account.Identity == identity));

        public Task<Result<Unit>> UpsertAsync(Account account, CancellationToken cancellationToken)
        {
            Events.Add("upsert");
            UpsertCount++;
            if (!UpsertResult.IsSuccess)
            {
                return Task.FromResult(UpsertResult);
            }

            Accounts.RemoveAll(existing => existing.Id == account.Id);
            Accounts.Add(account);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<Unit>> SetStatusAsync(string accountId, AccountStatus status, CancellationToken cancellationToken)
        {
            Account? account = Accounts.FirstOrDefault(candidate => candidate.Id == accountId);
            if (account is not null)
            {
                Accounts.Remove(account);
                Accounts.Add(account with { Status = status });
            }

            return Task.FromResult(Result.Success());
        }

        public Task<Result<Unit>> DeleteAndClearVersionReferencesAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeSettingsRepository(LauncherSettings settings) : ISettingsRepository
    {
        public LauncherSettings Stored { get; private set; } = settings;

        public Task<Result<LauncherSettings>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<LauncherSettings>.Success(Stored));

        public Task<Result<Unit>> SaveAsync(LauncherSettings settings, CancellationToken cancellationToken)
        {
            Stored = settings;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FakeVersionOverrideRepository(VersionOverride? initial = null) : IVersionOverrideRepository
    {
        public List<VersionOverride> Stored { get; } = initial is null ? [] : [initial];

        public Task<IReadOnlyList<VersionOverride>> GetForGameRootAsync(string gameRootId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VersionOverride>>(Stored.Where(item => item.GameRootId == gameRootId).ToArray());

        public Task<Result<Unit>> UpsertAsync(VersionOverride versionOverride, CancellationToken cancellationToken)
        {
            Stored.RemoveAll(item => item.GameRootId == versionOverride.GameRootId && item.VersionFolder == versionOverride.VersionFolder);
            Stored.Add(versionOverride);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<Unit>> RemoveAsync(string gameRootId, string versionFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<Unit>> RenameAsync(string gameRootId, string sourceFolder, string targetFolder, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
