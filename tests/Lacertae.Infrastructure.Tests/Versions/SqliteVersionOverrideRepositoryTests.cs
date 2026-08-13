using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Versions;
using Lacertae.Infrastructure.GameRoots;
using Lacertae.Infrastructure.Storage;
using Lacertae.Infrastructure.Versions;

namespace Lacertae.Infrastructure.Tests.Versions;

public sealed class SqliteVersionOverrideRepositoryTests
{
    [Fact]
    public async Task UpsertAndGetRoundTripAllFields()
    {
        string directory = CreateDirectory();
        try
        {
            SqliteConnectionFactory factory = new(Path.Combine(directory, "lacertae.db"));
            Assert.True((await new SqliteMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken)).IsSuccess);
            SqliteGameRootRepository gameRootRepository = new(factory);
            Assert.True((await gameRootRepository.UpsertAsync(
                new GameRoot("root-1", @"C:\Games\.minecraft", "Minecraft", GameRootAvailability.Available, null),
                TestContext.Current.CancellationToken)).IsSuccess);

            SqliteVersionOverrideRepository repository = new(factory);
            VersionOverride expected = new(
                "root-1",
                "fabric-1.21",
                "Fabric 1.21",
                IsolationOverride.ForceIsolated,
                "account-1",
                @"C:\Java\21\bin\javaw.exe",
                1024,
                4096,
                GcProfile.Zgc,
                ["-Xms1G", "-Dfoo=bar"],
                ["--demo"]);

            Assert.True((await repository.UpsertAsync(expected, TestContext.Current.CancellationToken)).IsSuccess);
            VersionOverride actual = Assert.Single(await repository.GetForGameRootAsync(
                expected.GameRootId,
                TestContext.Current.CancellationToken));

            Assert.Equal(expected with { JvmArguments = actual.JvmArguments, GameArguments = actual.GameArguments }, actual);
            Assert.Equal(expected.JvmArguments, actual.JvmArguments);
            Assert.Equal(expected.GameArguments, actual.GameArguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UpsertRejectsArgumentsContainingNul()
    {
        string directory = CreateDirectory();
        try
        {
            SqliteConnectionFactory factory = new(Path.Combine(directory, "lacertae.db"));
            Assert.True((await new SqliteMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await new SqliteGameRootRepository(factory).UpsertAsync(
                new GameRoot("root-1", @"C:\Games\.minecraft", "Minecraft", GameRootAvailability.Available, null),
                TestContext.Current.CancellationToken)).IsSuccess);

            VersionOverride invalid = new(
                "root-1",
                "version",
                null,
                IsolationOverride.Inherit,
                null,
                null,
                null,
                null,
                null,
                ["good\0bad"],
                []);

            var result = await new SqliteVersionOverrideRepository(factory).UpsertAsync(
                invalid,
                TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal("VERSION_OVERRIDE_INVALID", result.Problem?.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lacertae-version-overrides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
