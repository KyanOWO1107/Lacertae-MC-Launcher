using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Versions;

public sealed class SaveVersionOverride(IVersionOverrideRepository repository)
{
    public async Task<Result<Unit>> ExecuteAsync(
        VersionOverride versionOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versionOverride);
        if (!IsValid(versionOverride))
        {
            return Result.Failure(InvalidProblem());
        }

        return await repository.UpsertAsync(versionOverride, cancellationToken);
    }

    private static bool IsValid(VersionOverride versionOverride) =>
        !string.IsNullOrWhiteSpace(versionOverride.GameRootId) &&
        IsVersionFolder(versionOverride.VersionFolder) &&
        (versionOverride.DisplayName is null || !string.IsNullOrWhiteSpace(versionOverride.DisplayName)) &&
        (versionOverride.JavaPath is null || !string.IsNullOrWhiteSpace(versionOverride.JavaPath)) &&
        IsValidMemory(versionOverride.MinimumMemoryMb) &&
        IsValidMemory(versionOverride.MaximumMemoryMb) &&
        (versionOverride.MinimumMemoryMb is null ||
            versionOverride.MaximumMemoryMb is null ||
            versionOverride.MaximumMemoryMb >= versionOverride.MinimumMemoryMb) &&
        Enum.IsDefined(versionOverride.Isolation) &&
        (versionOverride.GcProfile is null || Enum.IsDefined(versionOverride.GcProfile.Value)) &&
        IsValidArguments(versionOverride.JvmArguments) &&
        IsValidArguments(versionOverride.GameArguments);

    private static bool IsVersionFolder(string folder) =>
        !string.IsNullOrWhiteSpace(folder) &&
        folder is not "." and not ".." &&
        folder.IndexOfAny(['/', '\\', '\0']) < 0;

    private static bool IsValidMemory(int? memoryMb) => memoryMb is null or >= 512;

    private static bool IsValidArguments(IReadOnlyList<string> arguments) =>
        arguments is not null && arguments.All(static argument =>
            !string.IsNullOrWhiteSpace(argument) &&
            argument.IndexOfAny(['\0', '\r', '\n']) < 0 &&
            System.Text.Encoding.UTF8.GetByteCount(argument) <= 8 * 1024);

    private static Problem InvalidProblem() => new(
        "VERSION_OVERRIDE_INVALID",
        ProblemStage.VersionResolution,
        "problem.version.override_invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.version.review_settings"]);
}
