using Lacertae.Domain.Accounts;
using Lacertae.Domain.Java;
using Lacertae.Domain.Launch;
using Lacertae.Infrastructure.Games;

namespace Lacertae.Infrastructure.Tests.Games;

public sealed class CmlLibProcessFactoryTests
{
    [Theory]
    [InlineData("launch-modern", "--modern-required")]
    [InlineData("launch-legacy", "--legacy-required")]
    public async Task BuildProcessSpecMapsVersionArgumentsWithoutCmlLibDefaults(
        string fixtureName,
        string requiredArgument)
    {
        string root = CopyFixtureToTemporaryRoot(fixtureName);
        string javaPath = Path.Combine(root, "runtime", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        File.WriteAllText(javaPath, "fixture-java");
        LaunchPlan plan = CreatePlan(root, fixtureName, javaPath, requiredArgument);

        var result = await new CmlLibProcessFactory().BuildProcessSpecAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        var spec = result.Value;
        Assert.Equal(Path.GetFullPath(javaPath), spec.FileName);
        Assert.Equal(plan.GameDirectory, spec.WorkingDirectory);
        Assert.Contains(requiredArgument, Reveal(spec.ArgumentList));
        Assert.Contains("--username", Reveal(spec.ArgumentList));
        Assert.Contains("Player", Reveal(spec.ArgumentList));
        Assert.Equal(2, Reveal(spec.ArgumentList).Count(argument => argument == "--duplicate"));
        Assert.Equal(1, Reveal(spec.ArgumentList).Count(argument => argument == "-Duser.extra=true"));
        Assert.Equal(1, Reveal(spec.ArgumentList).Count(argument => argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, Reveal(spec.ArgumentList).Count(argument => argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("-XX:+UseZGC", Reveal(spec.ArgumentList));
        Assert.DoesNotContain("-XX:+UseG1GC", Reveal(spec.ArgumentList));
        Assert.DoesNotContain("log4j2.formatMsgNoLookups", Reveal(spec.ArgumentList));
        Assert.DoesNotContain("access-token-secret", spec.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildProcessSpecKeepsEachUserTokenEvenWhenItContainsSpaces()
    {
        string root = CopyFixtureToTemporaryRoot("launch-modern");
        string javaPath = Path.Combine(root, "java.exe");
        File.WriteAllText(javaPath, "fixture-java");
        LaunchPlan plan = CreatePlan(root, "launch-modern", javaPath, "--modern-required") with
        {
            // A user token is intentionally one argument even though it contains a space.
        };

        var result = await new CmlLibProcessFactory().BuildProcessSpecAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Contains("value with spaces", Reveal(result.Value.ArgumentList));
    }

    private static LaunchPlan CreatePlan(string root, string version, string javaPath, string requiredArgument) => new(
        "corr-1",
        "root-1",
        version,
        version,
        root,
        root,
        "java-17",
        javaPath,
        17,
        "account-1",
        AccountType.Offline,
        "Player",
        "5627dd98-e6be-3c21-b8a8-e92344183641",
        new AuthSession(
            "Player",
            "5627dd98-e6be-3c21-b8a8-e92344183641",
            new SensitiveString("access-token-secret"),
            "legacy",
            null,
            null),
        1024,
        4096,
        ["-Xms1024M", "-Xmx4096M", "-XX:+UseZGC"],
        ["-Duser.extra=true", "value with spaces"],
        [requiredArgument, "--duplicate", "--duplicate"],
        [],
        LaunchDisposition.KeepLauncherOpen,
        DateTimeOffset.UtcNow);

    private static string CopyFixtureToTemporaryRoot(string fixtureName)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "minecraft", fixtureName + ".json");
        string root = Path.Combine(Path.GetTempPath(), "lacertae cml 中文 launch " + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "versions", fixtureName, fixtureName + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        return root;
    }

    private static string[] Reveal(IReadOnlyList<SensitiveString> arguments) =>
        arguments.Select(static argument => argument.Reveal()).ToArray();
}
