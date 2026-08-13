using System.Security.Cryptography;
using System.Text;
using Lacertae.Infrastructure.Games;

namespace Lacertae.Infrastructure.Tests.Games;

public sealed class CmlLibGameEngineTests
{
    [Fact]
    public async Task InspectLocalVersionsMapsInheritanceWithoutWritingFixtures()
    {
        string gameRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "minecraft");
        string beforeHash = HashDirectory(gameRoot);
        CmlLibGameEngine engine = new(gameRoot);

        var result = await engine.InspectLocalVersionsAsync(TestContext.Current.CancellationToken);

        string afterHash = HashDirectory(gameRoot);
        Assert.True(result.IsSuccess, result.Problem?.Code);
        var child = Assert.Single(result.Value, version => version.FolderName == "fixture-child");
        Assert.Equal("fixture-base", child.InheritsFrom);
        Assert.Equal(17, child.Java.MajorVersion);
        Assert.Equal("release", child.VersionType);
        Assert.Equal(beforeHash, afterHash);
    }

    private static string HashDirectory(string directory)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, path)));
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
