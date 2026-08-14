using Lacertae.Application.Java;
using Lacertae.Domain.Java;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Tests.Java;

public sealed class ResolveJavaForVersionTests
{
    [Fact]
    public void ExecuteResolvesAutomaticJavaMemoryAndJvmArgumentsInOrder()
    {
        GameVersionDescriptor version = Version(21, hasModLoader: true);
        JavaInstallation installation = Installation(
            "managed-21",
            @"C:\Managed\21\bin\javaw.exe",
            21,
            JavaSource.Managed,
            managed: true);
        VersionOverride settings = Override(
            version,
            javaPath: null,
            minimumMemoryMb: null,
            maximumMemoryMb: null,
            gcProfile: null,
            jvmArguments: ["-Dexample=true"]);

        var result = ResolveJavaForVersion.Execute(
            version,
            settings,
            LauncherSettings.Default,
            new JavaDiscoveryResult([installation], []),
            totalPhysicalMb: 16_384,
            availableMb: 12_288,
            JavaArchitecture.X64);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("managed-21", result.Value.Installation.Id);
        Assert.Equal(new MemoryAllocation(3072, 3072, MemoryMode.Automatic), result.Value.Memory);
        Assert.Equal(
            ["-Xms3072M", "-Xmx3072M", "-XX:+UseG1GC", "-Dexample=true"],
            result.Value.FlattenedJvmArguments);
    }

    [Fact]
    public void ExecuteUsesVersionManualJavaAndDoesNotFallbackWhenIncompatible()
    {
        GameVersionDescriptor version = Version(21);
        JavaInstallation incompatible = Installation(
            "manual-17",
            @"C:\Java\17\bin\java.exe",
            17,
            JavaSource.Path,
            managed: false);
        JavaInstallation compatible = Installation(
            "managed-21",
            @"C:\Managed\21\bin\java.exe",
            21,
            JavaSource.Managed,
            managed: true);
        VersionOverride settings = Override(version, incompatible.ExecutablePath, null, null, null, []);

        var result = ResolveJavaForVersion.Execute(
            version,
            settings,
            LauncherSettings.Default,
            new JavaDiscoveryResult([incompatible, compatible], []),
            16_384,
            12_288,
            JavaArchitecture.X64);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_MANUAL_INCOMPATIBLE", result.Problem?.Code);
    }

    [Fact]
    public void ExecuteUsesGlobalManualJavaWhenVersionOverrideIsAbsent()
    {
        GameVersionDescriptor version = Version(17);
        JavaInstallation installation = Installation(
            "global-17",
            @"C:\Java\17\bin\java.exe",
            17,
            JavaSource.Path,
            managed: false);
        VersionOverride settings = Override(version, null, null, null, null, []);
        LauncherSettings launcher = LauncherSettings.Default with { GlobalJavaPath = installation.ExecutablePath };

        var result = ResolveJavaForVersion.Execute(
            version,
            settings,
            launcher,
            new JavaDiscoveryResult([installation], []),
            16_384,
            12_288,
            JavaArchitecture.X64);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("global-17", result.Value.Installation.Id);
    }

    [Fact]
    public void ExecuteReportsMissingJavaWithInstallActionAndSafeContext()
    {
        GameVersionDescriptor version = Version(21);

        var result = ResolveJavaForVersion.Execute(
            version,
            Override(version, null, null, null, null, []),
            LauncherSettings.Default,
            new JavaDiscoveryResult([], []),
            16_384,
            12_288,
            JavaArchitecture.X64);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_MISSING", result.Problem?.Code);
        Assert.Contains("action.java.install-managed", result.Problem?.SuggestedActionKeys ?? []);
        Assert.Equal("21", result.Problem?.SafeContext["requiredMajor"]);
        Assert.Equal("x64", result.Problem?.SafeContext["architecture"]);
    }

    [Fact]
    public void ExecuteSupportsFixedMemoryAndExplicitZgc()
    {
        GameVersionDescriptor version = Version(21);
        JavaInstallation installation = Installation(
            "managed-21",
            @"C:\Managed\21\bin\java.exe",
            21,
            JavaSource.Managed,
            managed: true);
        VersionOverride settings = Override(version, null, 1024, 4096, GcProfile.Zgc, []);

        var result = ResolveJavaForVersion.Execute(
            version,
            settings,
            LauncherSettings.Default,
            new JavaDiscoveryResult([installation], []),
            16_384,
            12_288,
            JavaArchitecture.X64);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(new MemoryAllocation(1024, 4096, MemoryMode.Fixed), result.Value.Memory);
        Assert.Equal(["-XX:+UseZGC"], result.Value.JvmArguments.GarbageCollectorArguments);
    }

    [Fact]
    public void ExecutePropagatesJvmArgumentConflict()
    {
        GameVersionDescriptor version = Version(21);
        JavaInstallation installation = Installation(
            "managed-21",
            @"C:\Managed\21\bin\java.exe",
            21,
            JavaSource.Managed,
            managed: true);

        var result = ResolveJavaForVersion.Execute(
            version,
            Override(version, null, null, null, null, ["-Xmx9999M"]),
            LauncherSettings.Default,
            new JavaDiscoveryResult([installation], []),
            16_384,
            12_288,
            JavaArchitecture.X64);

        Assert.False(result.IsSuccess);
        Assert.Equal("JVM_ARGUMENT_CONFLICT", result.Problem?.Code);
    }

    private static GameVersionDescriptor Version(int major, bool hasModLoader = false) => new(
        "root-1",
        $"version-{major}",
        $"Version {major}",
        "release",
        null,
        new JavaRequirement("java-runtime", major),
        hasModLoader);

    private static VersionOverride Override(
        GameVersionDescriptor version,
        string? javaPath,
        int? minimumMemoryMb,
        int? maximumMemoryMb,
        GcProfile? gcProfile,
        IReadOnlyList<string> jvmArguments) => new(
        version.GameRootId,
        version.FolderName,
        null,
        IsolationOverride.Inherit,
        null,
        javaPath,
        minimumMemoryMb,
        maximumMemoryMb,
        gcProfile,
        jvmArguments,
        []);

    private static JavaInstallation Installation(
        string id,
        string path,
        int major,
        JavaSource source,
        bool managed) => new(
        id,
        path,
        major,
        $"{major}.0.1",
        "Vendor",
        JavaArchitecture.X64,
        source,
        managed);
}
