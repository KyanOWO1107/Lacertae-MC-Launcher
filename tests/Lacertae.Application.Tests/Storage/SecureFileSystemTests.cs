using Lacertae.Application.Storage;

namespace Lacertae.Application.Tests.Storage;

public sealed class SecureFileSystemTests
{
    [Fact]
    public async Task AtomicWriteAsyncCreatesAndReplacesAFile()
    {
        using TestRoot root = new();
        string path = Path.Combine(root.Path, "nested", "state.json");

        await SecureFileSystem.WriteAtomicallyAsync(
            path,
            "first"u8.ToArray(),
            TestContext.Current.CancellationToken);
        await SecureFileSystem.WriteAtomicallyAsync(
            path,
            "second"u8.ToArray(),
            TestContext.Current.CancellationToken);

        Assert.Equal("second", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenReadAsyncKeepsTheFileAvailableThroughAStream()
    {
        using TestRoot root = new();
        string path = Path.Combine(root.Path, "value.txt");
        await File.WriteAllTextAsync(path, "payload", TestContext.Current.CancellationToken);

        await using Stream stream = SecureFileSystem.OpenRead(path, root.Path);
        using StreamReader reader = new(stream);

        Assert.Equal("payload", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OpenReadExclusiveDeniesConcurrentWriteOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TestRoot root = new();
        string path = System.IO.Path.Combine(root.Path, "executable.bin");
        File.WriteAllText(path, "trusted");

        using Stream lease = SecureFileSystem.OpenReadExclusive(path, root.Path);
        Assert.ThrowsAny<IOException>(() =>
        {
            using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        });
    }

    [Fact]
    public void ExistingDirectoryCanBeBoundToItsRoot()
    {
        using TestRoot root = new();
        string child = System.IO.Path.Combine(root.Path, "child");
        SecureFileSystem.EnsureDirectory(child, root.Path);

        Assert.True(SecureFileSystem.IsSafeDirectory(child, root.Path));
        Assert.True(SecureFileSystem.IsSafeDirectory(root.Path, root.Path));
    }

    [Fact]
    public void DirectoryDeleteRemovesAValidatedTree()
    {
        using TestRoot root = new();
        string child = System.IO.Path.Combine(root.Path, "child");
        Directory.CreateDirectory(System.IO.Path.Combine(child, "nested"));
        File.WriteAllText(System.IO.Path.Combine(child, "nested", "value.txt"), "value");

        SecureFileSystem.DeleteDirectory(child, root.Path);

        Assert.False(Directory.Exists(child));
    }

    [Fact]
    public void DirectoryMoveKeepsTheValidatedRootBoundary()
    {
        using TestRoot root = new();
        string source = System.IO.Path.Combine(root.Path, "source");
        string destination = System.IO.Path.Combine(root.Path, "destination");
        Directory.CreateDirectory(source);
        File.WriteAllText(System.IO.Path.Combine(source, "value.txt"), "value");

        SecureFileSystem.MoveDirectoryCreate(source, destination, root.Path);

        Assert.False(Directory.Exists(source));
        Assert.Equal("value", File.ReadAllText(System.IO.Path.Combine(destination, "value.txt")));
    }

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lacertae-secure-fs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
