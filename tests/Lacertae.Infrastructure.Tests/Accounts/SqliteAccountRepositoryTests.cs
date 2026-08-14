using Lacertae.Domain.Accounts;
using Lacertae.Infrastructure.Accounts;
using Lacertae.Infrastructure.Storage;

namespace Lacertae.Infrastructure.Tests.Accounts;

public sealed class SqliteAccountRepositoryTests
{
    [Fact]
    public async Task UpsertAndFindRoundTripsOfflineProfileWithoutSecretMaterial()
    {
        string directory = CreateDirectory();
        try
        {
            SqliteConnectionFactory factory = new(Path.Combine(directory, "lacertae.db"));
            Assert.True((await new SqliteMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken)).IsSuccess);
            SqliteAccountRepository repository = new(factory);
            Account expected = new(
                "account-1",
                new AccountIdentity(AccountIdentity.OfflineProviderId, "5627dd98-e6be-3c21-b8a8-e92344183641"),
                AccountType.Offline,
                "Alex",
                null,
                null,
                AccountStatus.Active,
                null);

            Assert.True((await repository.UpsertAsync(expected, TestContext.Current.CancellationToken)).IsSuccess);

            Account? actual = await repository.FindByIdentityAsync(
                expected.Identity,
                TestContext.Current.CancellationToken);

            Assert.Equal(expected, actual);
            Assert.Null(actual!.SecretRef);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lacertae-accounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
