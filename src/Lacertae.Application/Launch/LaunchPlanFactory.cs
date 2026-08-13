using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Launch;

public sealed class LaunchPlanFactory
{
#pragma warning disable CA1822
    public Result<LaunchPlan> Create(
        GameVersionDescriptor version,
        string accountId,
        string gameDirectory,
        string javaPath,
        int minimumMemoryMb,
        int maximumMemoryMb,
        IReadOnlyList<string> jvmArguments,
        IReadOnlyList<string> gameArguments)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(jvmArguments);
        ArgumentNullException.ThrowIfNull(gameArguments);

        if (string.IsNullOrWhiteSpace(version.GameRootId) ||
            string.IsNullOrWhiteSpace(version.FolderName) ||
            string.IsNullOrWhiteSpace(accountId) ||
            string.IsNullOrWhiteSpace(gameDirectory) ||
            string.IsNullOrWhiteSpace(javaPath) ||
            version.Java.MajorVersion < 1 ||
            minimumMemoryMb < 256 ||
            maximumMemoryMb < minimumMemoryMb)
        {
            return Result<LaunchPlan>.Failure(InvalidPlanProblem());
        }

        return Result<LaunchPlan>.Success(new LaunchPlan(
            version.GameRootId,
            version.FolderName,
            accountId,
            Path.GetFullPath(gameDirectory),
            Path.GetFullPath(javaPath),
            version.Java.MajorVersion,
            minimumMemoryMb,
            maximumMemoryMb,
            jvmArguments,
            gameArguments));
    }

    private static Problem InvalidPlanProblem() => new(
        "LAUNCH_PLAN_INVALID",
        ProblemStage.LaunchPlanning,
        "problem.launch.plan.invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.launch.review_settings"]);
#pragma warning restore CA1822
}
