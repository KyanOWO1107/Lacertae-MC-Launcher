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
    bool CheckUpdatesOnStartup)
{
    public static LauncherSettings Default => new(
        1,
        ThemeMode.System,
        null,
        null,
        null,
        null,
        VersionIsolationPolicy.ModLoaderOrNonRelease,
        true);
}
