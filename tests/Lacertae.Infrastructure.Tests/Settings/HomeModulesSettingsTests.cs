using Lacertae.Domain.Home;
using Lacertae.Domain.Settings;
using Lacertae.Infrastructure.Settings;

namespace Lacertae.Infrastructure.Tests.Settings;

public sealed class HomeModulesSettingsTests
{
    [Fact]
    public async Task LoadAsyncRejectsMissingKnownModule()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, Document(
                "{\"module\":\"recentVersions\",\"order\":0,\"isVisible\":true}",
                "{\"module\":\"activeTasks\",\"order\":1,\"isVisible\":true}",
                "{\"module\":\"quickActions\",\"order\":2,\"isVisible\":true}"));

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
    public async Task LoadAsyncRejectsDuplicateModuleOrOrder()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, Document(
                "{\"module\":\"recentVersions\",\"order\":0,\"isVisible\":true}",
                "{\"module\":\"activeTasks\",\"order\":0,\"isVisible\":true}",
                "{\"module\":\"quickActions\",\"order\":2,\"isVisible\":true}",
                "{\"module\":\"releaseNotes\",\"order\":3,\"isVisible\":true}"));

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
    public async Task SaveAsyncRejectsUnknownModuleWithoutReplacingFile()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            const string original = "keep";
            File.WriteAllText(path, original);
            LauncherSettings invalid = LauncherSettings.Default with
            {
                HomeModules =
                [
                    new HomeModulePlacement((HomeModuleId)99, 0, true),
                    new HomeModulePlacement(HomeModuleId.ActiveTasks, 1, true),
                    new HomeModulePlacement(HomeModuleId.QuickActions, 2, true),
                    new HomeModulePlacement(HomeModuleId.ReleaseNotes, 3, true),
                ],
            };

            var result = await new JsonSettingsRepository(path).SaveAsync(invalid, TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal("SETTINGS_CORRUPT", result.Problem?.Code);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lacertae-settings-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Document(params string[] modules) =>
        "{\"schemaVersion\":1,\"theme\":\"system\",\"selectedGameRootId\":null,\"selectedVersionFolder\":null,\"defaultAccountId\":null,\"globalJavaPath\":null,\"isolationPolicy\":\"modLoaderOrNonRelease\",\"checkUpdatesOnStartup\":true,\"homeModules\":[" +
        string.Join(',', modules) + "]}";
}
