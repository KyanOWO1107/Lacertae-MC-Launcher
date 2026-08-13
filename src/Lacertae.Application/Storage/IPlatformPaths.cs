namespace Lacertae.Application.Storage;

public interface IPlatformPaths
{
    string ExecutableDirectory { get; }
    string RoamingApplicationData { get; }
    string LocalApplicationData { get; }
}
