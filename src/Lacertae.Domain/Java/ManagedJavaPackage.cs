using Lacertae.Domain.Downloads;

namespace Lacertae.Domain.Java;

public sealed record ManagedJavaPackage(
    string Component,
    int MajorVersion,
    JavaArchitecture Architecture,
    string PackageVersion,
    IReadOnlyList<string> Directories,
    IReadOnlyList<DownloadArtifact> Files,
    string ExecutableRelativePath);
