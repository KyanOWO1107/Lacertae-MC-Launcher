using Lacertae.Application.Storage;
using Lacertae.Platform.Windows.Java;

namespace Lacertae.Platform.Windows.Tests.Java;

public sealed class PathJavaCandidateSourceTests
{
    [Fact]
    public async Task FindCandidatesAsyncUsesJavawBeforeJavaAndDeduplicatesQuotedPathEntries()
    {
        FakeFileSystem fileSystem = new();
        string first = Path.Combine(Path.GetTempPath(), "lacertae-java-path-one");
        string second = Path.Combine(Path.GetTempPath(), "lacertae-java-path-two");
        fileSystem.Add(Path.Combine(first, "javaw.exe"));
        fileSystem.Add(Path.Combine(first, "java.exe"));
        fileSystem.Add(Path.Combine(second, "java.exe"));

        PathJavaCandidateSource source = new(
            $"\"{first}\";{first};{second}",
            fileSystem);

        List<string> paths = [];
        await foreach (var candidate in source.FindCandidatesAsync(TestContext.Current.CancellationToken))
        {
            paths.Add(candidate.ExecutablePath);
        }

        Assert.Equal(
            [Path.GetFullPath(Path.Combine(first, "javaw.exe")), Path.GetFullPath(Path.Combine(first, "java.exe")), Path.GetFullPath(Path.Combine(second, "java.exe"))],
            paths);
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path) => files.Add(Path.GetFullPath(path));
        public bool FileExists(string path) => files.Contains(Path.GetFullPath(path));
        public bool DirectoryExists(string path) => true;
        public void CreateDirectory(string path)
        {
        }

        public bool IsDirectoryWritable(string path) => true;
        public string GetFullPath(string path) => Path.GetFullPath(path);
    }
}
