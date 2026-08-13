namespace Lacertae.Domain.Launch;

public sealed record LaunchPlan(
    string GameRootId,
    string VersionFolder,
    string AccountId,
    string GameDirectory,
    string JavaPath,
    int RequiredJavaMajor,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> GameArguments);
