using Lacertae.Application.Storage;
using Lacertae.Domain.Storage;

namespace Lacertae.Application.Tests.Storage;

public sealed class DataRootResolverTests
{
    [Theory]
    [InlineData(false, DataRootMode.UserProfile)]
    [InlineData(true, DataRootMode.LocalToExecutable)]
    public void ResolveSelectsExactlyOneMode(bool markerExists, DataRootMode expectedMode)
    {
        FakeFileSystem fileSystem = new();
        FakePlatformPaths paths = new(@"C:\Apps\Lacertae", @"C:\Roaming", @"C:\Local");
        fileSystem.SetFileExists(@"C:\Apps\Lacertae\lacertae.portable", markerExists);

        var result = new DataRootResolver(paths, fileSystem).Resolve();

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(expectedMode, result.Value.Mode);
        Assert.Equal(
            markerExists ? @"C:\Apps\Lacertae\LacertaeData\Roaming" : @"C:\Roaming\Lacertae",
            result.Value.RoamingPath);
        Assert.Equal(
            markerExists ? @"C:\Apps\Lacertae\LacertaeData\Local" : @"C:\Local\Lacertae",
            result.Value.LocalPath);
    }

    [Fact]
    public void ResolveDoesNotSwitchModeForStraySettingsFile()
    {
        FakeFileSystem fileSystem = new();
        fileSystem.SetFileExists(@"C:\Apps\Lacertae\settings.json", true);
        FakePlatformPaths paths = new(@"C:\Apps\Lacertae", @"C:\Roaming", @"C:\Local");

        var result = new DataRootResolver(paths, fileSystem).Resolve();

        Assert.Equal(DataRootMode.UserProfile, result.Value.Mode);
    }

    private sealed record FakePlatformPaths(
        string ExecutableDirectory,
        string RoamingApplicationData,
        string LocalApplicationData) : IPlatformPaths;

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, bool> files = new(StringComparer.OrdinalIgnoreCase);

        public void SetFileExists(string path, bool exists) => files[path] = exists;

        public bool FileExists(string path) => files.TryGetValue(path, out bool exists) && exists;
        public bool DirectoryExists(string path) => true;
        public void CreateDirectory(string path)
        {
        }

        public bool IsDirectoryWritable(string path) => true;
        public string GetFullPath(string path) => path;
    }
}
