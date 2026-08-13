namespace Lacertae.Domain.Versions;

public enum VersionIsolationPolicy
{
    Disabled,
    ModLoaderOnly,
    NonReleaseOnly,
    ModLoaderOrNonRelease,
    All,
}
