using Lacertae.Application.Java;
using Lacertae.Application.Storage;
using Lacertae.Domain.Java;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Java;

public sealed class DiscoverJavaInstallationsTests
{
    [Fact]
    public async Task ExecuteAsyncProbesEachUniqueCandidateOnceAndKeepsDiagnostics()
    {
        FakeSource source = new([
            new JavaCandidate(@"C:\Java\21\bin\java.exe", JavaSource.Path, false),
            new JavaCandidate(@"c:\java\21\bin\java.exe", JavaSource.Registry, false),
            new JavaCandidate(@"C:\Java\17\bin\java.exe", JavaSource.Managed, true),
        ]);
        FakeProbe probe = new()
        {
            Installations = new Dictionary<string, JavaInstallation>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Java\21\bin\java.exe"] = new("java-21", @"C:\Java\21\bin\java.exe", 21, "21.0.7", "Vendor", JavaArchitecture.X64, JavaSource.Path, false),
            },
        };
        probe.Failures.Add(@"C:\Java\17\bin\java.exe", new Problem(
            "JAVA_PROBE_INVALID",
            ProblemStage.JavaResolution,
            "problem.java.probe_failed",
            false,
            "test",
            []));

        var result = await new DiscoverJavaInstallations(
            [source],
            probe,
            new AlwaysExistingFileSystem(),
            new WindowsPathComparer()).ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Single(result.Value.Installations);
        Assert.Equal("java-21", result.Value.Installations[0].Id);
        Assert.Single(result.Value.Diagnostics);
        Assert.Equal("JAVA_PROBE_INVALID", result.Value.Diagnostics[0].Code);
        Assert.Equal(2, probe.Calls.Count);
    }

    [Fact]
    public async Task ExecuteAsyncSortsManagedBeforeUnmanagedThenByDescendingVersion()
    {
        FakeSource source = new([
            new JavaCandidate(@"C:\Java\17\bin\java.exe", JavaSource.Path, false),
            new JavaCandidate(@"C:\Managed\21\bin\java.exe", JavaSource.Managed, true),
            new JavaCandidate(@"C:\Java\21\bin\java.exe", JavaSource.Path, false),
        ]);
        FakeProbe probe = new();
        probe.Installations = new Dictionary<string, JavaInstallation>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Java\17\bin\java.exe"] = new("17", @"C:\Java\17\bin\java.exe", 17, "17.0.12", "Vendor", JavaArchitecture.X64, JavaSource.Path, false),
            [@"C:\Managed\21\bin\java.exe"] = new("managed", @"C:\Managed\21\bin\java.exe", 21, "21.0.1", "Vendor", JavaArchitecture.X64, JavaSource.Managed, true),
            [@"C:\Java\21\bin\java.exe"] = new("21", @"C:\Java\21\bin\java.exe", 21, "21.0.7", "Vendor", JavaArchitecture.X64, JavaSource.Path, false),
        };

        var result = await new DiscoverJavaInstallations(
            [source],
            probe,
            new AlwaysExistingFileSystem(),
            new WindowsPathComparer()).ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(["managed", "21", "17"], result.Value.Installations.Select(static installation => installation.Id));
    }

    [Fact]
    public async Task ExecuteAsyncReturnsFailureWhenEverySourceFailsToEnumerate()
    {
        FakeSource source = new([], throwOnEnumeration: true);

        var result = await new DiscoverJavaInstallations(
            [source],
            new FakeProbe(),
            new AlwaysExistingFileSystem(),
            new WindowsPathComparer()).ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_DISCOVERY_FAILED", result.Problem?.Code);
    }

    private sealed class FakeSource(IReadOnlyList<JavaCandidate> candidates, bool throwOnEnumeration = false) : IJavaCandidateSource
    {
        public async IAsyncEnumerable<JavaCandidate> FindCandidatesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (throwOnEnumeration)
            {
                throw new IOException("fixture enumeration failed");
            }

            foreach (JavaCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return candidate;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeProbe : IJavaProbe
    {
        public Dictionary<string, JavaInstallation> Installations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Problem> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];

        public Task<Result<JavaInstallation>> ProbeAsync(
            string executablePath,
            JavaSource source,
            bool isManaged,
            CancellationToken cancellationToken)
        {
            Calls.Add(executablePath);
            return Task.FromResult(
                Failures.TryGetValue(executablePath, out Problem? failure)
                    ? Result<JavaInstallation>.Failure(failure)
                    : Installations.TryGetValue(executablePath, out JavaInstallation? installation)
                        ? Result<JavaInstallation>.Success(installation)
                        : Result<JavaInstallation>.Failure(new Problem(
                            "JAVA_PROBE_INVALID",
                            ProblemStage.JavaResolution,
                            "problem.java.probe_failed",
                            false,
                            "test",
                            [])));
        }
    }

    private sealed class AlwaysExistingFileSystem : IFileSystem
    {
        public bool FileExists(string path) => true;
        public bool DirectoryExists(string path) => true;
        public void CreateDirectory(string path)
        {
        }

        public bool IsDirectoryWritable(string path) => true;
        public string GetFullPath(string path) => path;
    }

    private sealed class WindowsPathComparer : IPathComparer
    {
        public string Normalize(string path) => path.TrimEnd('\\', '/');
        public bool Equals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
