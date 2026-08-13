using Lacertae.Application.Storage;

namespace Lacertae.Platform.Windows.Storage;

public sealed class WindowsPlatformPaths : IPlatformPaths
{
    public string ExecutableDirectory => Path.GetFullPath(AppContext.BaseDirectory);

    public string RoamingApplicationData =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string LocalApplicationData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
