using System.Text.Json;
using Lacertae.Application.Java;
using Lacertae.Application.Launch;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Downloads;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Java;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Tests.Launch;

public sealed class FreezeLaunchPlanTests
{
    [Fact]
    public async Task ExecuteAsyncCopiesEffectiveInputsAndRedactsSessionSecrets()
    {
        string rootPath = CreateTemporaryDirectory();
        List<string> overrideJvmArguments = ["-Dversion=true"];
        List<string> overrideGameArguments = ["--demo", "--demo"];
        VersionOverride versionOverride = new(
            "root-1",
            "fixture-child",
            "Renamed child",
            IsolationOverride.ForceIsolated,
            "account-1",
            null,
            null,
            null,
            GcProfile.G1,
            overrideJvmArguments,
            overrideGameArguments);
        Account account = new(
            "account-1",
            new AccountIdentity(AccountIdentity.OfflineProviderId, "5627dd98-e6be-3c21-b8a8-e92344183641"),
            AccountType.Offline,
            "Steve",
            null,
            null,
            AccountStatus.Active,
            null);
        AuthSession session = new(
            "Steve",
            account.Identity.ProfileUuid,
            new SensitiveString("token-secret"),
            "legacy",
            null,
            null);
        GameVersionDescriptor version = new(
            "root-1",
            "fixture-child",
            "Fixture Child",
            "release",
            null,
            new JavaRequirement("java-runtime", 17),
            true);
        ResolvedJavaLaunchSettings java = new(
            new JavaInstallation(
                "java-17",
                Path.Combine(rootPath, "java", "bin", "java.exe"),
                17,
                "17.0.1",
                "Fixture",
                JavaArchitecture.X64,
                JavaSource.Managed,
                true),
            new MemoryAllocation(1024, 4096, MemoryMode.Fixed),
            new JvmArgumentSet(["-Xms1024M", "-Xmx4096M"], ["-XX:+UseG1GC"], ["-Djava=true"]));

        var result = await new FreezeLaunchPlan().ExecuteAsync(
            new LaunchFreezeRequest(
                new GameRoot("root-1", rootPath, "Fixture", GameRootAvailability.Available, null),
                version,
                versionOverride,
                LauncherSettings.Default,
                account,
                session,
                java,
                [],
                ["-Dglobal=true"],
                ["--global"],
                LaunchDisposition.HideLauncher),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        LaunchPlan plan = result.Value;
        Assert.Equal("root-1", plan.GameRootId);
        Assert.Equal("fixture-child", plan.VersionFolder);
        Assert.Equal("Renamed child", plan.VersionDisplayName);
        Assert.Equal(account.Id, plan.AccountId);
        Assert.Equal(AccountType.Offline, plan.AccountType);
        Assert.Equal(Path.Combine(rootPath, "versions", "fixture-child"), plan.GameDirectory);
        Assert.Equal(["-Xms1024M", "-Xmx4096M", "-XX:+UseG1GC"], plan.StructuredJvmArguments);
        Assert.Equal(["-Dversion=true"], plan.UserJvmArguments);
        Assert.Equal(["--demo", "--demo"], plan.GameArguments);
        Assert.Equal(LaunchDisposition.HideLauncher, plan.Disposition);
        Assert.NotEqual("", plan.CorrelationId);
        Assert.DoesNotContain("token-secret", plan.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token-secret", JsonSerializer.Serialize(plan), StringComparison.Ordinal);

        overrideJvmArguments.Add("-Dmutated=true");
        overrideGameArguments[0] = "--mutated";
        Assert.Equal(["-Dversion=true"], plan.UserJvmArguments);
        Assert.Equal(["--demo", "--demo"], plan.GameArguments);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsEmptyInteriorAndNulGameArgumentLines()
    {
        LaunchFreezeRequest request = FreezeRequest(["--one\n\n--two"]);
        var emptyResult = await new FreezeLaunchPlan().ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.False(emptyResult.IsSuccess);
        Assert.Equal("LAUNCH_PLAN_INVALID_ARGUMENTS", emptyResult.Problem?.Code);

        var nulResult = await new FreezeLaunchPlan().ExecuteAsync(
            FreezeRequest(["--one\0two"]),
            TestContext.Current.CancellationToken);
        Assert.False(nulResult.IsSuccess);
        Assert.Equal("LAUNCH_PLAN_INVALID_ARGUMENTS", nulResult.Problem?.Code);
    }

    private static LaunchFreezeRequest FreezeRequest(IReadOnlyList<string> gameArguments)
    {
        string root = CreateTemporaryDirectory();
        Account account = new(
            "account-1",
            new AccountIdentity(AccountIdentity.OfflineProviderId, "5627dd98-e6be-3c21-b8a8-e92344183641"),
            AccountType.Offline,
            "Steve",
            null,
            null,
            AccountStatus.Active,
            null);
        return new LaunchFreezeRequest(
            new GameRoot("root-1", root, "Fixture", GameRootAvailability.Available, null),
            new GameVersionDescriptor("root-1", "fixture-child", "Fixture Child", "release", null, new JavaRequirement("java", 17)),
            new VersionOverride("root-1", "fixture-child", null, IsolationOverride.ForceShared, null, null, null, null, null, [], gameArguments),
            LauncherSettings.Default,
            account,
            new AuthSession("Steve", account.Identity.ProfileUuid, new SensitiveString("token"), "legacy", null, null),
            new ResolvedJavaLaunchSettings(
                new JavaInstallation("java", Path.Combine(root, "java.exe"), 17, "17", "Fixture", JavaArchitecture.X64, JavaSource.Managed, true),
                new MemoryAllocation(1024, 2048, MemoryMode.Fixed),
                new JvmArgumentSet([], [], [])),
            []);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lacertae-freeze-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
