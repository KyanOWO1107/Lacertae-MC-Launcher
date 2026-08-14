using Lacertae.Domain.Java;

namespace Lacertae.Domain.Launch;

public sealed record LaunchPlan(
    string GameRootId,
    string VersionFolder,
    string AccountId,
    string GameDirectory,
    string JavaInstallationId,
    string JavaPath,
    int RequiredJavaMajor,
    MemoryAllocation Memory,
    JvmArgumentSet JvmArguments,
    IReadOnlyList<string> GameArguments)
{
    public int MinimumMemoryMb => Memory.MinimumMb;

    public int MaximumMemoryMb => Memory.MaximumMb;

    public IReadOnlyList<string> FlattenedJvmArguments => JvmArguments.Flatten();
}
