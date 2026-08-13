namespace Lacertae.Domain.Java;

public sealed record JavaInstallation(
    string Id,
    string ExecutablePath,
    int MajorVersion,
    string FullVersion,
    string Vendor,
    JavaArchitecture Architecture,
    JavaSource Source,
    bool IsManaged);
