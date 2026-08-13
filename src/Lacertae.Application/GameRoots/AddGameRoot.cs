using Lacertae.Application.Storage;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.GameRoots;

public sealed class AddGameRoot(IGameRootRepository repository, IFileSystem fileSystem)
{
    public async Task<Result<GameRoot>> ExecuteAsync(
        string path,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        string normalizedPath = Normalize(path);
        if (!fileSystem.DirectoryExists(normalizedPath))
        {
            return Result<GameRoot>.Failure(Problem("GAME_ROOT_NOT_FOUND"));
        }

        bool hasMinecraftDirectories =
            fileSystem.DirectoryExists(Path.Combine(normalizedPath, "versions")) &&
            fileSystem.DirectoryExists(Path.Combine(normalizedPath, "assets")) &&
            fileSystem.DirectoryExists(Path.Combine(normalizedPath, "libraries"));
        if (!allowEmpty && !hasMinecraftDirectories)
        {
            return Result<GameRoot>.Failure(Problem("GAME_ROOT_EMPTY_NOT_ALLOWED"));
        }

        if (await repository.FindByNormalizedPathAsync(normalizedPath, cancellationToken) is not null)
        {
            return Result<GameRoot>.Failure(Problem("GAME_ROOT_DUPLICATE"));
        }

        string displayName = new DirectoryInfo(normalizedPath).Name;
        GameRoot root = new(
            Guid.NewGuid().ToString("N"),
            normalizedPath,
            displayName,
            GameRootAvailability.Available,
            DateTimeOffset.UtcNow);
        Result<Domain.Common.Unit> saved = await repository.UpsertAsync(root, cancellationToken);
        return saved.IsSuccess ? Result<GameRoot>.Success(root) : Result<GameRoot>.Failure(saved.Problem!);
    }

    private string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = fileSystem.GetFullPath(path);
        while (normalized.Length > 1 && normalized.EndsWith(Path.DirectorySeparatorChar))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.Storage,
        "problem.game_root.invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.game_root.review_path"]);
}
