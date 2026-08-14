using Lacertae.Domain.Launch;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Launch;

public interface ILaunchPreflight
{
    Task<Result<LaunchPreflightResult>> ExecuteAsync(
        LaunchPlan plan,
        CancellationToken cancellationToken);
}

public sealed record LaunchPreflightResult(
    bool IsReady,
    IReadOnlyList<string> MissingOrDamagedArtifactIds,
    IReadOnlyList<string> FailureCodes,
    IReadOnlyList<string> SuggestedActionKeys,
    long AvailableFreeBytes)
{
    public static LaunchPreflightResult Ready(long availableFreeBytes) => new(
        true,
        [],
        [],
        [],
        availableFreeBytes);
}
