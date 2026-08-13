using Lacertae.Platform.Windows.Storage;

namespace Lacertae.Platform.Windows.Tests.Storage;

public sealed class WindowsPlatformPathsTests
{
    [Fact]
    public void PathsAreRootedAndExecutableDirectoryIsNormalized()
    {
        WindowsPlatformPaths paths = new();

        Assert.True(Path.IsPathRooted(paths.ExecutableDirectory));
        Assert.True(Path.IsPathRooted(paths.RoamingApplicationData));
        Assert.True(Path.IsPathRooted(paths.LocalApplicationData));
        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), paths.ExecutableDirectory);
    }
}
