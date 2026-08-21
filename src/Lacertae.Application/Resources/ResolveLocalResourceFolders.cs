using Lacertae.Application.Storage;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Resources;

public sealed record LocalResourceFolder(string Name, string NormalizedPath, bool Exists);

public sealed record LocalResourceFolders(string RootPath, IReadOnlyList<LocalResourceFolder> Folders)
{
    public LocalResourceFolder this[string name] => Folders.First(folder => string.Equals(folder.Name, name, StringComparison.Ordinal));
}

public sealed class ResolveLocalResourceFolders
{
    private readonly IFileSystem? fileSystem;

    public ResolveLocalResourceFolders(IFileSystem? fileSystem = null) => this.fileSystem = fileSystem;
    public static readonly IReadOnlyList<string> StandardFolderNames =
        ["mods", "resourcepacks", "shaderpacks", "saves", "screenshots", "logs"];

    public Result<LocalResourceFolders> Resolve(string sharedGameDirectory, string? isolatedGameDirectory = null)
    {
        string? root = SelectRoot(sharedGameDirectory, isolatedGameDirectory);
        if (root is null) return Result<LocalResourceFolders>.Failure(Problem("RESOURCE_ROOT_INVALID"));
        try
        {
            string shared = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sharedGameDirectory));
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (isolatedGameDirectory is not null && !IsWithin(normalized, shared))
            {
                return Result<LocalResourceFolders>.Failure(Problem("RESOURCE_ROOT_INVALID"));
            }

            if ((Directory.Exists(shared) && !SecureFileSystem.IsSafeDirectory(shared)) ||
                (Directory.Exists(normalized) && !SecureFileSystem.IsSafeDirectory(normalized, shared)))
            {
                return Result<LocalResourceFolders>.Failure(Problem("RESOURCE_ROOT_INVALID"));
            }
            LocalResourceFolder[] folders = StandardFolderNames
                .Select(name => new LocalResourceFolder(name, Path.GetFullPath(Path.Combine(normalized, name)), Exists(Path.Combine(normalized, name))))
                .ToArray();
            return Result<LocalResourceFolders>.Success(new LocalResourceFolders(normalized, folders));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return Result<LocalResourceFolders>.Failure(Problem("RESOURCE_ROOT_INVALID"));
        }
    }

    public static Result<LocalResourceFolders> Execute(string sharedGameDirectory, string? isolatedGameDirectory = null) =>
        new ResolveLocalResourceFolders().Resolve(sharedGameDirectory, isolatedGameDirectory);

    public static Result<LocalResourceFolder> CreateConfirmed(string sharedGameDirectory, string? isolatedGameDirectory, string folderName) =>
        new ResolveLocalResourceFolders().Create(sharedGameDirectory, isolatedGameDirectory, folderName, confirmed: true);

    public Result<LocalResourceFolder> Create(
        string sharedGameDirectory,
        string? isolatedGameDirectory,
        string folderName,
        bool confirmed)
    {
        if (!confirmed) return Result<LocalResourceFolder>.Failure(Problem("RESOURCE_CONFIRMATION_REQUIRED"));
        Result<LocalResourceFolders> resolved = Resolve(sharedGameDirectory, isolatedGameDirectory);
        if (!resolved.IsSuccess) return Result<LocalResourceFolder>.Failure(resolved.Problem!);
        string? standard = StandardFolderNames.FirstOrDefault(name => string.Equals(name, folderName, StringComparison.Ordinal));
        if (standard is null) return Result<LocalResourceFolder>.Failure(Problem("RESOURCE_FOLDER_NOT_ALLOWED"));
        LocalResourceFolder folder = resolved.Value[standard];
        try
        {
            if (fileSystem is null) SecureFileSystem.EnsureDirectory(folder.NormalizedPath, resolved.Value.RootPath);
            else fileSystem.CreateDirectory(folder.NormalizedPath);
            return Result<LocalResourceFolder>.Success(folder with { Exists = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result<LocalResourceFolder>.Failure(Problem("RESOURCE_FOLDER_UNAVAILABLE"));
        }
    }

    private static string? SelectRoot(string shared, string? isolated) =>
        !string.IsNullOrWhiteSpace(isolated) ? isolated : !string.IsNullOrWhiteSpace(shared) ? shared : null;

    private bool Exists(string path) => fileSystem?.DirectoryExists(path) ?? SecureFileSystem.IsSafeDirectory(path);

    private static bool IsWithin(string path, string root)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = root + Path.DirectorySeparatorChar;
        return string.Equals(path, root, comparison) || path.StartsWith(prefix, comparison);
    }

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.Storage,
        code == "RESOURCE_CONFIRMATION_REQUIRED" ? "problem.resource.confirmation_required" : "problem.resource.folder_unavailable",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.resource.review"]);
}
