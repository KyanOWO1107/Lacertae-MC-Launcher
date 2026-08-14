using Lacertae.Domain.Home;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Infrastructure.Settings;

internal sealed record SettingsDocumentV1(
    int SchemaVersion,
    ThemeMode Theme,
    string? SelectedGameRootId,
    string? SelectedVersionFolder,
    string? DefaultAccountId,
    string? GlobalJavaPath,
    VersionIsolationPolicy IsolationPolicy,
    bool CheckUpdatesOnStartup,
    IReadOnlyList<HomeModulePlacement>? HomeModules)
{
    public static SettingsDocumentV1 FromDomain(LauncherSettings settings) => new(
        settings.SchemaVersion,
        settings.Theme,
        settings.SelectedGameRootId,
        settings.SelectedVersionFolder,
        settings.DefaultAccountId,
        settings.GlobalJavaPath,
        settings.IsolationPolicy,
        settings.CheckUpdatesOnStartup,
        settings.HomeModules);

    public LauncherSettings ToDomain() => new(
        SchemaVersion,
        Theme,
        SelectedGameRootId,
        SelectedVersionFolder,
        DefaultAccountId,
        GlobalJavaPath,
        IsolationPolicy,
        CheckUpdatesOnStartup,
        HomeModules!);
}
