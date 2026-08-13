namespace Lacertae.Domain.Java;

public sealed record JavaSelectionRequest(
    int RequiredMajor,
    JavaArchitecture PreferredArchitecture,
    int MaximumMemoryMb,
    string? VersionJavaPath,
    string? GlobalJavaPath,
    IReadOnlyList<JavaInstallation> Installations);
