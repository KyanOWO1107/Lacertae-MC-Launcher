using System.Security.Cryptography;
using System.Text;
using Lacertae.Application.Processes;
using Lacertae.Domain.Java;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Java;
using Lacertae.Testing.Processes;

namespace Lacertae.Infrastructure.Tests.Java;

public sealed class JavaProbeTests
{
    [Theory]
    [InlineData("1.8.0_442", 8)]
    [InlineData("21.0.7", 21)]
    [InlineData("17", 17)]
    public async Task ProbeAsyncParsesJavaMajor(string version, int expectedMajor)
    {
        FakeProcessRunner runner = new();
        runner.Response = Result<ProcessResult>.Success(new ProcessResult(
            0,
            string.Empty,
            Properties(version, "Eclipse Adoptium", "amd64", @"C:\Java\jdk"),
            false));

        var result = await new JavaProbe(runner).ProbeAsync(
            @"C:\Java\jdk\bin\javaw.exe",
            JavaSource.Manual,
            false,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(expectedMajor, result.Value.MajorVersion);
        Assert.Equal("Eclipse Adoptium", result.Value.Vendor);
        Assert.Equal(JavaSource.Manual, result.Value.Source);
        Assert.False(result.Value.IsManaged);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(@"C:\Java\jdk\bin\javaw.exe"))).ToLowerInvariant(),
            result.Value.Id);
    }

    [Theory]
    [InlineData("amd64", JavaArchitecture.X64)]
    [InlineData("x86_64", JavaArchitecture.X64)]
    [InlineData("x86", JavaArchitecture.X86)]
    [InlineData("aarch64", JavaArchitecture.Arm64)]
    public async Task ProbeAsyncParsesArchitecture(string osArch, JavaArchitecture expected)
    {
        FakeProcessRunner runner = new();
        runner.Response = Result<ProcessResult>.Success(new ProcessResult(
            0,
            string.Empty,
            Properties("21.0.7", "Eclipse Adoptium", osArch, @"C:\Java\jdk"),
            false));

        var result = await new JavaProbe(runner).ProbeAsync(
            @"C:\Java\jdk\bin\java.exe",
            JavaSource.Path,
            false,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(expected, result.Value.Architecture);
    }

    [Fact]
    public async Task ProbeAsyncUsesStrictFiveSecondProcessRequest()
    {
        FakeProcessRunner runner = new();
        runner.Response = Result<ProcessResult>.Success(new ProcessResult(
            0,
            string.Empty,
            Properties("21.0.7", "Eclipse Adoptium", "amd64", @"C:\Java\jdk"),
            false));

        var result = await new JavaProbe(runner).ProbeAsync(
            @"C:\Java\jdk\bin\java.exe",
            JavaSource.Path,
            false,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        ProcessRequest request = Assert.IsType<ProcessRequest>(runner.LastRequest);
        Assert.Equal(@"C:\Java\jdk\bin\java.exe", request.FileName);
        Assert.Equal(["-XshowSettings:properties", "-version"], request.ArgumentList);
        Assert.Equal(TimeSpan.FromSeconds(5), request.Timeout);
        Assert.True(request.CreateNoWindow);
    }

    [Fact]
    public async Task ProbeAsyncReturnsTimeoutProblemWhenProcessExceedsLimit()
    {
        FakeProcessRunner runner = new();
        runner.Response = Result<ProcessResult>.Success(new ProcessResult(124, string.Empty, string.Empty, true));

        var result = await new JavaProbe(runner).ProbeAsync(
            @"C:\Java\jdk\bin\java.exe",
            JavaSource.Path,
            false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_PROBE_TIMEOUT", result.Problem?.Code);
    }

    [Fact]
    public async Task ProbeAsyncReturnsInvalidProblemWhenRequiredPropertiesAreMissing()
    {
        FakeProcessRunner runner = new();
        runner.Response = Result<ProcessResult>.Success(new ProcessResult(
            0,
            string.Empty,
            "java.version = 21.0.7\nos.arch = amd64\n",
            false));

        var result = await new JavaProbe(runner).ProbeAsync(
            @"C:\Java\jdk\bin\java.exe",
            JavaSource.Path,
            false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_PROBE_INVALID", result.Problem?.Code);
    }

    [Fact]
    public async Task ProbeAsyncMapsProcessFailureWithoutExposingFullPath()
    {
        FakeProcessRunner runner = new();
        runner.Response = Result<ProcessResult>.Failure(new Lacertae.Domain.Problems.Problem(
                "PROCESS_START_FAILED",
                Lacertae.Domain.Problems.ProblemStage.Process,
                "problem.process.start_failed",
                false,
                "test",
                []));

        var result = await new JavaProbe(runner).ProbeAsync(
            @"C:\Java\jdk\bin\java.exe",
            JavaSource.Path,
            false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_PROBE_FAILED", result.Problem?.Code);
        Assert.Equal("java.exe", result.Problem?.SafeContext["executable"]);
        Assert.DoesNotContain(@"C:\Java\jdk", string.Join("|", result.Problem?.SafeContext.Values ?? []));
    }

    private static string Properties(string version, string vendor, string architecture, string javaHome) =>
        $"    java.version = {version}\n    java.vendor = {vendor}\n    os.arch = {architecture}\n    java.home = {javaHome}\nopenjdk version \"{version}\"";
}
