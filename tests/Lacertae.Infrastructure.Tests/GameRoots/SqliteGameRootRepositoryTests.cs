using Lacertae.Domain.GameRoots;
using Lacertae.Infrastructure.GameRoots;
using Lacertae.Infrastructure.Storage;

namespace Lacertae.Infrastructure.Tests.GameRoots;

public sealed class SqliteGameRootRepositoryTests
{
    [Fact]
    public async Task UpsertAndGetRoundTripAllFields()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lacertae-root-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            SqliteConnectionFactory factory = new(Path.Combine(directory, "lacertae.db"));
            Assert.True((await new SqliteMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken)).IsSuccess);
            SqliteGameRootRepository repository = new(factory);
            GameRoot expected = new("root-1", @"C:\Games\.minecraft", "Minecraft", GameRootAvailability.Available, DateTimeOffset.UtcNow);

            Assert.True((await repository.UpsertAsync(expected, TestContext.Current.CancellationToken)).IsSuccess);
            GameRoot? actual = await repository.FindByNormalizedPathAsync(expected.NormalizedPath, TestContext.Current.CancellationToken);

            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
