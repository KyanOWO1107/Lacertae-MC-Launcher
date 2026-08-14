using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Versions;

public static class ResolveVersionDirectory
{
    public static Result<string> Execute(GameRoot gameRoot, string versionFolder)
    {
        ArgumentNullException.ThrowIfNull(gameRoot);
        if (gameRoot.Availability != GameRootAvailability.Available ||
            string.IsNullOrWhiteSpace(gameRoot.NormalizedPath) ||
            !IsValidFolder(versionFolder))
        {
            return Result<string>.Failure(Problem("VERSION_DIRECTORY_NOT_ALLOWED"));
        }

        try
        {
            string versionsRoot = Path.GetFullPath(Path.Combine(gameRoot.NormalizedPath, "versions"));
            string path = Path.GetFullPath(Path.Combine(versionsRoot, versionFolder));
            string prefix = Path.TrimEndingDirectorySeparator(versionsRoot) + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return path.StartsWith(prefix, comparison)
                ? Result<string>.Success(path)
                : Result<string>.Failure(Problem("VERSION_DIRECTORY_NOT_ALLOWED"));
        }
        catch (ArgumentException)
        {
            return Result<string>.Failure(Problem("VERSION_DIRECTORY_NOT_ALLOWED"));
        }
    }

    private static bool IsValidFolder(string folder) =>
        !string.IsNullOrWhiteSpace(folder) &&
        folder is not "." and not ".." &&
        folder.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) < 0;

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.VersionResolution,
        "problem.version.directory_not_allowed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.version.review_settings"]);
}
