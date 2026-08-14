using Lacertae.Application.Java;
using Lacertae.Application.Launch;
using Lacertae.Domain.Java;
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
            JavaSettings(17, "java-17", @"C:\Java\17\bin\javaw.exe"),
            ["--demo"]);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("fixture-child", result.Value.VersionFolder);
        Assert.Equal("account-1", result.Value.AccountId);
        Assert.Equal(17, result.Value.RequiredJavaMajor);
        Assert.Equal("java-17", result.Value.JavaInstallationId);
        Assert.Equal(["-Xms1024M", "-Xmx4096M"], result.Value.JvmArguments.MemoryArguments);
        Assert.Equal(["--demo"], result.Value.GameArguments);
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
            JavaSettings(17, "java-17", @"C:\Java\17\bin\javaw.exe", new MemoryAllocation(4096, 1024, MemoryMode.Fixed)),
            []);

        Assert.False(result.IsSuccess);
        Assert.Equal("LAUNCH_PLAN_INVALID", result.Problem?.Code);
    }

    private static ResolvedJavaLaunchSettings JavaSettings(
        int major,
        string id,
        string path,
        MemoryAllocation? memory = null) => new(
        new JavaInstallation(id, path, major, $"{major}.0.1", "Vendor", JavaArchitecture.X64, JavaSource.Managed, true),
        memory ?? new MemoryAllocation(1024, 4096, MemoryMode.Fixed),
        new JvmArgumentSet(["-Xms1024M", "-Xmx4096M"], [], []));
}
