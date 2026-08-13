using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;
using Lacertae.Infrastructure.Settings;

namespace Lacertae.Infrastructure.Tests.Settings;

public sealed class JsonSettingsRepositoryTests
{
    [Fact]
    public async Task LoadAsyncReturnsDefaultsOnlyWhenFileIsAbsent()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsRepository repository = new(path);

            var result = await repository.LoadAsync(TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess, result.Problem?.Code);
            Assert.Equal(LauncherSettings.Default, result.Value);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LoadAsyncRejectsUnknownFields()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"theme\":\"system\",\"selectedGameRootId\":null,\"selectedVersionFolder\":null,\"defaultAccountId\":null,\"globalJavaPath\":null,\"isolationPolicy\":\"modLoaderOrNonRelease\",\"checkUpdatesOnStartup\":true,\"unknown\":true}");

            var result = await new JsonSettingsRepository(path).LoadAsync(TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal("SETTINGS_CORRUPT", result.Problem?.Code);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LoadAsyncKeepsInvalidJsonByteForByte()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            byte[] original = "{ invalid json"u8.ToArray();
            File.WriteAllBytes(path, original);

            var result = await new JsonSettingsRepository(path).LoadAsync(TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal("SETTINGS_CORRUPT", result.Problem?.Code);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SaveAsyncAtomicallyReplacesAndCreatesBackup()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "old");
            JsonSettingsRepository repository = new(path);

            var result = await repository.SaveAsync(LauncherSettings.Default with { Theme = ThemeMode.Dark }, TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess, result.Problem?.Code);
            Assert.Contains("\"theme\": \"dark\"", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.NotEmpty(Directory.GetFiles(directory, "settings.json.*.bak"));
            Assert.Empty(Directory.GetFiles(directory, "settings.json.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LoadAsyncRejectsFutureSchemaVersion()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"schemaVersion\":2,\"theme\":\"system\",\"selectedGameRootId\":null,\"selectedVersionFolder\":null,\"defaultAccountId\":null,\"globalJavaPath\":null,\"isolationPolicy\":\"modLoaderOrNonRelease\",\"checkUpdatesOnStartup\":true}");

            var result = await new JsonSettingsRepository(path).LoadAsync(TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal("SETTINGS_VERSION_UNSUPPORTED", result.Problem?.Code);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lacertae-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
