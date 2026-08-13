using Lacertae.Application.Storage;
using Lacertae.Platform.Windows.Java;

namespace Lacertae.Platform.Windows.Tests.Java;

public sealed class RegistryJavaCandidateSourceTests
{
    [Fact]
    public async Task FindCandidatesAsyncAcceptsOnlyExecutablesUnderDeclaredJavaHome()
    {
        string root = Path.Combine(Path.GetTempPath(), "lacertae-registry-java");
        string validHome = Path.Combine(root, "valid");
        string invalidHome = Path.Combine(root, "invalid");
        string outside = Path.Combine(root, "outside");
        FakeFileSystem fileSystem = new();
        fileSystem.Add(Path.Combine(validHome, "bin", "javaw.exe"));
        fileSystem.Add(Path.Combine(invalidHome, "bin", "java.exe"));
        fileSystem.Add(Path.Combine(outside, "java.exe"));
        FakeRegistryReader registry = new([
            new JavaRegistryEntry(validHome),
            new JavaRegistryEntry(invalidHome, Path.Combine(outside, "java.exe")),
        ]);

        RegistryJavaCandidateSource source = new(registry, fileSystem);
        List<string> paths = [];
        await foreach (var candidate in source.FindCandidatesAsync(TestContext.Current.CancellationToken))
        {
            paths.Add(candidate.ExecutablePath);
        }

        Assert.Equal([Path.GetFullPath(Path.Combine(validHome, "bin", "javaw.exe"))], paths);
    }

    private sealed class FakeRegistryReader(IReadOnlyList<JavaRegistryEntry> entries) : IJavaRegistryReader
    {
        public IEnumerable<JavaRegistryEntry> ReadEntries() => entries;
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
