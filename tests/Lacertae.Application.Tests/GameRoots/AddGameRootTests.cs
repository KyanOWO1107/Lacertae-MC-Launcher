using System.Security.Cryptography;
using Lacertae.Application.GameRoots;
using Lacertae.Application.Storage;
using Lacertae.Domain.Common;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.GameRoots;

public sealed class AddGameRootTests
{
    [Fact]
    public async Task ExecuteAsyncRejectsMissingDirectory()
    {
        FakeGameRootRepository repository = new();
        AddGameRoot useCase = new(repository, new TestFileSystem());

        var result = await useCase.ExecuteAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            allowEmpty: true,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("GAME_ROOT_NOT_FOUND", result.Problem?.Code);
    }

    [Fact]
    public async Task ExecuteAsyncRequiresExplicitAllowEmpty()
    {
        string directory = CreateDirectory();
        try
        {
            var result = await new AddGameRoot(new FakeGameRootRepository(), new TestFileSystem()).ExecuteAsync(
                directory,
                allowEmpty: false,
                TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal("GAME_ROOT_EMPTY_NOT_ALLOWED", result.Problem?.Code);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExecuteAsyncAddsValidRootWithoutWritingDirectory()
    {
        string directory = CreateDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "versions"));
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        Directory.CreateDirectory(Path.Combine(directory, "libraries"));
        File.WriteAllText(Path.Combine(directory, "options.txt"), "key:value");
        string before = HashDirectory(directory);
        FakeGameRootRepository repository = new();

        try
        {
            var result = await new AddGameRoot(repository, new TestFileSystem()).ExecuteAsync(
                directory,
                allowEmpty: false,
                TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess, result.Problem?.Code);
            Assert.Equal(Path.GetFullPath(directory), result.Value.NormalizedPath);
            Assert.Equal(before, HashDirectory(directory));
            Assert.Same(result.Value, repository.Stored);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExecuteAsyncRejectsNormalizedDuplicatePath()
    {
        string directory = CreateDirectory();
        try
        {
            FakeGameRootRepository repository = new();
            AddGameRoot useCase = new(repository, new TestFileSystem());
            Assert.True((await useCase.ExecuteAsync(directory, true, TestContext.Current.CancellationToken)).IsSuccess);

            var duplicate = await useCase.ExecuteAsync(
                Path.Combine(directory, "."),
                true,
                TestContext.Current.CancellationToken);

            Assert.False(duplicate.IsSuccess);
            Assert.Equal("GAME_ROOT_DUPLICATE", duplicate.Problem?.Code);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeGameRootRepository : IGameRootRepository
    {
        public GameRoot? Stored { get; private set; }

        public Task<IReadOnlyList<GameRoot>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GameRoot>>(Stored is null ? [] : [Stored]);

        public Task<GameRoot?> FindByNormalizedPathAsync(string normalizedPath, CancellationToken cancellationToken) =>
            Task.FromResult(Stored?.NormalizedPath == normalizedPath ? Stored : null);

        public Task<Result<Unit>> UpsertAsync(GameRoot gameRoot, CancellationToken cancellationToken)
        {
            Stored = gameRoot;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<Unit>> RemoveAsync(string id, CancellationToken cancellationToken)
        {
            Stored = null;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class TestFileSystem : IFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public bool IsDirectoryWritable(string path) => true;

        public string GetFullPath(string path) => Path.GetFullPath(path);
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lacertae-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string HashDirectory(string directory)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
