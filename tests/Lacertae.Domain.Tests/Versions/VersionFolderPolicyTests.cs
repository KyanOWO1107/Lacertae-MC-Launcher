using Lacertae.Domain.Versions;

namespace Lacertae.Domain.Tests.Versions;

public sealed class VersionFolderPolicyTests
{
    [Theory]
    [InlineData("1.14.2 Pre-Release 4")]
    [InlineData("3D Shareware v1.34")]
    public void AcceptsOfficialHistoricalIdsWithInternalSpaces(string value)
    {
        Assert.True(VersionFolderPolicy.IsSafe(value));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("trailing.")]
    [InlineData(".")]
    [InlineData("..")]
    public void RejectsPathAmbiguousVersionIds(string value)
    {
        Assert.False(VersionFolderPolicy.IsSafe(value));
    }
}
