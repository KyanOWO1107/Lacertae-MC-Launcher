using Lacertae.Domain.Java;
using Lacertae.Domain.Versions;

namespace Lacertae.Domain.Tests.Java;

public sealed class JvmArgumentResolverTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(21)]
    public void AutomaticProfileSelectsG1(int javaMajor)
    {
        var result = Resolve(GcProfile.Automatic, javaMajor, JavaArchitecture.X64);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(["-XX:+UseG1GC"], result.Value.GarbageCollectorArguments);
    }

    [Fact]
    public void ExplicitZgcRequiresJava17OrNewer()
    {
        var result = Resolve(GcProfile.Zgc, 16, JavaArchitecture.X64);

        Assert.False(result.IsSuccess);
        Assert.Equal("JVM_GC_INCOMPATIBLE", result.Problem?.Code);
    }

    [Fact]
    public void ExplicitZgcRequiresNative64BitArchitecture()
    {
        var result = Resolve(GcProfile.Zgc, 17, JavaArchitecture.X86);

        Assert.False(result.IsSuccess);
        Assert.Equal("JVM_GC_INCOMPATIBLE", result.Problem?.Code);
    }

    [Fact]
    public void ExplicitZgcIsAvailableOnArm64()
    {
        var result = Resolve(GcProfile.Zgc, 17, JavaArchitecture.Arm64);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(["-XX:+UseZGC"], result.Value.GarbageCollectorArguments);
    }

    [Fact]
    public void NoneProfileAddsNoCollectorArgument()
    {
        var result = Resolve(GcProfile.None, 8, JavaArchitecture.X64);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Empty(result.Value.GarbageCollectorArguments);
    }

    [Fact]
    public void ArgumentsAreGroupedInMemoryCollectorUserOrder()
    {
        var result = Resolve(GcProfile.G1, 21, JavaArchitecture.X64, "-Dfoo=bar");

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(
            ["-Xms1024M", "-Xmx4096M", "-XX:+UseG1GC", "-Dfoo=bar"],
            result.Value.Flatten());
    }

    [Theory]
    [InlineData("-Xms2048M")]
    [InlineData("-Xmx8192M")]
    [InlineData("-XX:+UseG1GC")]
    [InlineData("-XX:+UseZGC")]
    public void UserArgumentsCannotOverrideStructuredMemoryOrCollector(string argument)
    {
        var result = Resolve(GcProfile.Automatic, 21, JavaArchitecture.X64, argument);

        Assert.False(result.IsSuccess);
        Assert.Equal("JVM_ARGUMENT_CONFLICT", result.Problem?.Code);
        Assert.Equal("0", result.Problem?.SafeContext["index"]);
        Assert.DoesNotContain(argument, result.Problem?.SafeContext.Values ?? []);
    }

    [Fact]
    public void UserArgumentsRejectNulAndNewlineWithoutEchoingTheToken()
    {
        const string argument = "-Dunsafe=one\n-two\0";

        var result = Resolve(GcProfile.None, 21, JavaArchitecture.X64, argument);

        Assert.False(result.IsSuccess);
        Assert.Equal("JVM_ARGUMENT_CONFLICT", result.Problem?.Code);
        Assert.DoesNotContain(argument, result.Problem?.SafeContext.Values ?? []);
    }

    [Fact]
    public void BlankAndOversizedArgumentsAreRejected()
    {
        var blankResult = Resolve(GcProfile.None, 21, JavaArchitecture.X64, "   ");
        var oversizedResult = Resolve(GcProfile.None, 21, JavaArchitecture.X64, new string('x', 8193));

        Assert.False(blankResult.IsSuccess);
        Assert.Equal("JVM_ARGUMENT_CONFLICT", blankResult.Problem?.Code);
        Assert.False(oversizedResult.IsSuccess);
        Assert.Equal("JVM_ARGUMENT_CONFLICT", oversizedResult.Problem?.Code);
    }

    [Fact]
    public void ExactDuplicateUserArgumentsAreRemovedWithoutReorderingOthers()
    {
        var result = Resolve(
            GcProfile.None,
            21,
            JavaArchitecture.X64,
            "-Da=1",
            "-Db=2",
            "-Da=1",
            "-Da=1 ");

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(["-Da=1", "-Db=2", "-Da=1 "], result.Value.UserArguments);
    }

    [Fact]
    public void ResolverDoesNotInjectVersionIndependentLog4jFlag()
    {
        var result = Resolve(GcProfile.None, 8, JavaArchitecture.X64);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.DoesNotContain(
            result.Value.Flatten(),
            static argument => argument.StartsWith("-Dlog4j2.formatMsgNoLookups", StringComparison.Ordinal));
    }

    [Fact]
    public void G1RequiresJava8OrNewer()
    {
        var result = Resolve(GcProfile.G1, 7, JavaArchitecture.X64);

        Assert.False(result.IsSuccess);
        Assert.Equal("JVM_GC_INCOMPATIBLE", result.Problem?.Code);
    }

    private static Lacertae.Domain.Results.Result<JvmArgumentSet> Resolve(
        GcProfile profile,
        int javaMajor,
        JavaArchitecture architecture,
        params string[] userArguments) =>
        JvmArgumentResolver.Resolve(
            profile,
            javaMajor,
            architecture,
            new MemoryAllocation(1024, 4096, MemoryMode.Fixed),
            userArguments);
}
