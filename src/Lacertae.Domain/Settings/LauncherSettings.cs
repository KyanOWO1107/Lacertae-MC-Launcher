using Lacertae.Domain.Home;
using Lacertae.Domain.Versions;

namespace Lacertae.Domain.Settings;

public sealed record LauncherSettings(
    int SchemaVersion,
    ThemeMode Theme,
    string? SelectedGameRootId,
    string? SelectedVersionFolder,
    string? DefaultAccountId,
    string? GlobalJavaPath,
    VersionIsolationPolicy IsolationPolicy,
    bool CheckUpdatesOnStartup,
    IReadOnlyList<HomeModulePlacement> HomeModules)
{
    private static readonly IReadOnlyList<HomeModulePlacement> DefaultHomeModules =
        HomeModulePlacement.CopyDefaults();

    public LauncherSettings(
        int schemaVersion,
        ThemeMode theme,
        string? selectedGameRootId,
        string? selectedVersionFolder,
        string? defaultAccountId,
        string? globalJavaPath,
        VersionIsolationPolicy isolationPolicy,
        bool checkUpdatesOnStartup)
        : this(
            schemaVersion,
            theme,
            selectedGameRootId,
            selectedVersionFolder,
            defaultAccountId,
            globalJavaPath,
            isolationPolicy,
            checkUpdatesOnStartup,
            HomeModulePlacement.CopyDefaults())
    {
    }

    public static LauncherSettings Default => new(
        1,
        ThemeMode.System,
        null,
        null,
        null,
        null,
        VersionIsolationPolicy.ModLoaderOrNonRelease,
        true,
        DefaultHomeModules);
}
