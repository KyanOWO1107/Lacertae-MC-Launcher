namespace Lacertae.Domain.Storage;

public sealed record DataRoot(
    DataRootMode Mode,
    string RoamingPath,
    string LocalPath)
{
    public string SettingsPath => Path.Combine(RoamingPath, "settings.json");
    public string DatabasePath => Path.Combine(RoamingPath, "lacertae.db");
    public string LogsPath => Path.Combine(LocalPath, "logs");
    public string CachePath => Path.Combine(LocalPath, "cache");
    public string RuntimesPath => Path.Combine(LocalPath, "runtimes");
    public string UpdatesPath => Path.Combine(LocalPath, "updates");
    public string SecretsPath => Path.Combine(LocalPath, "secrets");
}
