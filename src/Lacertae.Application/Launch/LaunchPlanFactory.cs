using Lacertae.Application.Java;
using Lacertae.Domain.Java;
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
        ResolvedJavaLaunchSettings javaSettings,
        IReadOnlyList<string> gameArguments)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(javaSettings);
        ArgumentNullException.ThrowIfNull(gameArguments);

        if (!IsValid(version, accountId, gameDirectory, javaSettings, gameArguments))
        {
            return Result<LaunchPlan>.Failure(InvalidPlanProblem());
        }

        try
        {
            return Result<LaunchPlan>.Success(new LaunchPlan(
                version.GameRootId,
                version.FolderName,
                accountId,
                Path.GetFullPath(gameDirectory),
                javaSettings.Installation.Id,
                Path.GetFullPath(javaSettings.Installation.ExecutablePath),
                version.Java.MajorVersion,
                javaSettings.Memory,
                javaSettings.JvmArguments,
                gameArguments));
        }
        catch (ArgumentException)
        {
            return Result<LaunchPlan>.Failure(InvalidPlanProblem());
        }
        catch (NotSupportedException)
        {
            return Result<LaunchPlan>.Failure(InvalidPlanProblem());
        }
    }

    private static bool IsValid(
        GameVersionDescriptor version,
        string accountId,
        string gameDirectory,
        ResolvedJavaLaunchSettings javaSettings,
        IReadOnlyList<string> gameArguments) =>
        !string.IsNullOrWhiteSpace(version.GameRootId) &&
        !string.IsNullOrWhiteSpace(version.FolderName) &&
        !string.IsNullOrWhiteSpace(accountId) &&
        !string.IsNullOrWhiteSpace(gameDirectory) &&
        version.Java is not null &&
        version.Java.MajorVersion >= 1 &&
        javaSettings.Installation is not null &&
        !string.IsNullOrWhiteSpace(javaSettings.Installation.Id) &&
        !string.IsNullOrWhiteSpace(javaSettings.Installation.ExecutablePath) &&
        javaSettings.Installation.MajorVersion == version.Java.MajorVersion &&
        javaSettings.Memory is not null &&
        javaSettings.Memory.MinimumMb >= 512 &&
        javaSettings.Memory.MaximumMb >= javaSettings.Memory.MinimumMb &&
        Enum.IsDefined(javaSettings.Memory.Mode) &&
        javaSettings.JvmArguments is not null &&
        IsValidArguments(javaSettings.JvmArguments.MemoryArguments) &&
        IsValidArguments(javaSettings.JvmArguments.GarbageCollectorArguments) &&
        IsValidArguments(javaSettings.JvmArguments.UserArguments) &&
        IsValidArguments(gameArguments);

    private static bool IsValidArguments(IReadOnlyList<string>? arguments) =>
        arguments is not null && arguments.All(static argument =>
            !string.IsNullOrWhiteSpace(argument) && argument.IndexOfAny(['\0', '\r', '\n']) < 0);

    private static Problem InvalidPlanProblem() => new(
        "LAUNCH_PLAN_INVALID",
        ProblemStage.LaunchPlanning,
        "problem.launch.plan.invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.launch.review_settings"]);
#pragma warning restore CA1822
}
