using Lacertae.Application.Storage;
using Lacertae.Platform.Windows.Java;

namespace Lacertae.Platform.Windows.Tests.Java;

public sealed class CommonDirectoryJavaCandidateSourceTests
{
    [Fact]
    public async Task FindCandidatesAsyncDoesNotDescendBeyondConfiguredDepth()
    {
        string root = Path.Combine(Path.GetTempPath(), "lacertae-common-java-" + Guid.NewGuid().ToString("N"));
        try
        {
            string depthTwoHome = Path.Combine(root, "one", "two");
            string depthThreeHome = Path.Combine(root, "one", "two", "three");
            string depthFourHome = Path.Combine(depthThreeHome, "four");
            CreateJavaExecutable(depthTwoHome);
            CreateJavaExecutable(depthThreeHome);
            CreateJavaExecutable(depthFourHome);

            CommonDirectoryJavaCandidateSource source = new([root], new SystemFileSystemForTest(), maximumDepth: 3);
            List<string> paths = [];
            await foreach (var candidate in source.FindCandidatesAsync(TestContext.Current.CancellationToken))
            {
                paths.Add(candidate.ExecutablePath);
            }

            Assert.Contains(Path.GetFullPath(Path.Combine(depthTwoHome, "bin", "javaw.exe")), paths);
            Assert.Contains(Path.GetFullPath(Path.Combine(depthThreeHome, "bin", "javaw.exe")), paths);
            Assert.DoesNotContain(Path.GetFullPath(Path.Combine(depthFourHome, "bin", "javaw.exe")), paths);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CreateJavaExecutable(string javaHome)
    {
        Directory.CreateDirectory(Path.Combine(javaHome, "bin"));
        File.WriteAllText(Path.Combine(javaHome, "bin", "javaw.exe"), "fixture");
    }

    private sealed class SystemFileSystemForTest : IFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public bool IsDirectoryWritable(string path) => true;
        public string GetFullPath(string path) => Path.GetFullPath(path);
    }
}
