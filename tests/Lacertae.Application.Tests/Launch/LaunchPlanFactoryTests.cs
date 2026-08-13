using Lacertae.Application.Launch;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Tests.Launch;

public sealed class LaunchPlanFactoryTests
{
    [Fact]
    public void CreateFreezesVersionAccountJavaAndGameDirectory()
    {
        GameVersionDescriptor version = new(
            "root-1",
            "fixture-child",
            "Fixture Child",
            "release",
            "fixture-base",
            new JavaRequirement("java-runtime-gamma", 17));

        var result = new LaunchPlanFactory().Create(
            version,
            "account-1",
            @"C:\Games\.minecraft\versions\fixture-child",
            @"C:\Java\17\bin\javaw.exe",
            1024,
            4096,
            [],
            []);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("fixture-child", result.Value.VersionFolder);
        Assert.Equal("account-1", result.Value.AccountId);
        Assert.Equal(17, result.Value.RequiredJavaMajor);
    }

    [Fact]
    public void CreateRejectsInvalidMemoryRange()
    {
        GameVersionDescriptor version = new(
            "root-1",
            "fixture-child",
            "Fixture Child",
            "release",
            null,
            new JavaRequirement("java-runtime-gamma", 17));

        var result = new LaunchPlanFactory().Create(
            version,
            "account-1",
            @"C:\Games\.minecraft",
            @"C:\Java\17\bin\javaw.exe",
            4096,
            1024,
            [],
            []);

        Assert.False(result.IsSuccess);
        Assert.Equal("LAUNCH_PLAN_INVALID", result.Problem?.Code);
    }
}
