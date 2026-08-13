using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Storage;

namespace Lacertae.Application.Storage;

public sealed class DataRootResolver(IPlatformPaths platformPaths, IFileSystem fileSystem)
{
    public Result<DataRoot> Resolve()
    {
        ArgumentNullException.ThrowIfNull(platformPaths);
        ArgumentNullException.ThrowIfNull(fileSystem);

        string executableDirectory = fileSystem.GetFullPath(platformPaths.ExecutableDirectory);
        bool portable = fileSystem.FileExists(Path.Combine(executableDirectory, "lacertae.portable"));
        DataRootMode mode = portable ? DataRootMode.LocalToExecutable : DataRootMode.UserProfile;
        string baseRoaming = portable
            ? Path.Combine(executableDirectory, "LacertaeData", "Roaming")
            : Path.Combine(platformPaths.RoamingApplicationData, "Lacertae");
        string baseLocal = portable
            ? Path.Combine(executableDirectory, "LacertaeData", "Local")
            : Path.Combine(platformPaths.LocalApplicationData, "Lacertae");
        string roamingPath = fileSystem.GetFullPath(baseRoaming);
        string localPath = fileSystem.GetFullPath(baseLocal);

        if (AreAliasedOrNested(roamingPath, localPath))
        {
            return Result<DataRoot>.Failure(InvalidRootProblem("DATA_ROOT_UNWRITABLE"));
        }

        try
        {
            fileSystem.CreateDirectory(roamingPath);
            fileSystem.CreateDirectory(localPath);
            if (!fileSystem.IsDirectoryWritable(roamingPath) || !fileSystem.IsDirectoryWritable(localPath))
            {
                return Result<DataRoot>.Failure(InvalidRootProblem("DATA_ROOT_UNWRITABLE"));
            }

            return Result<DataRoot>.Success(new DataRoot(mode, roamingPath, localPath));
        }
        catch (IOException)
        {
            return Result<DataRoot>.Failure(InvalidRootProblem("DATA_ROOT_UNWRITABLE"));
        }
        catch (UnauthorizedAccessException)
        {
            return Result<DataRoot>.Failure(InvalidRootProblem("DATA_ROOT_UNWRITABLE"));
        }
    }

    private static bool AreAliasedOrNested(string first, string second)
    {
        string firstWithSeparator = EnsureTrailingSeparator(first);
        string secondWithSeparator = EnsureTrailingSeparator(second);
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
               firstWithSeparator.StartsWith(secondWithSeparator, StringComparison.OrdinalIgnoreCase) ||
               secondWithSeparator.StartsWith(firstWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static Problem InvalidRootProblem(string code) => new(
        code,
        ProblemStage.Configuration,
        "problem.data_root.unwritable",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.data_root.choose_location"]);
}
