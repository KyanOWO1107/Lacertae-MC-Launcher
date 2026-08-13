namespace Lacertae.Domain.Versions;

public sealed record VersionOverride(
    string GameRootId,
    string VersionFolder,
    string? DisplayName,
    IsolationOverride Isolation,
    string? AccountId,
    string? JavaPath,
    int? MinimumMemoryMb,
    int? MaximumMemoryMb,
    GcProfile? GcProfile,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> GameArguments);
