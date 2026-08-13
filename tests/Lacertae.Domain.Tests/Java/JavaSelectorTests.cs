using Lacertae.Domain.Java;

namespace Lacertae.Domain.Tests.Java;

public sealed class JavaSelectorTests
{
    [Fact]
    public void ManualVersionPathWinsWhenCompatible()
    {
        JavaInstallation version = Installation("version", @"C:\Java\21\bin\java.exe", 21, "21.0.1", JavaSource.Path, false);
        JavaInstallation global = Installation("global", @"C:\Java\21-global\bin\java.exe", 21, "21.0.9", JavaSource.Path, false);

        var result = Select(
            requiredMajor: 21,
            preferredArchitecture: JavaArchitecture.X64,
            maximumMemoryMb: 4096,
            versionJavaPath: @"c:\java\21\bin\java.exe",
            globalJavaPath: global.ExecutablePath,
            installations: [version, global]);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("version", result.Value.Installation.Id);
        Assert.Equal(JavaSelectionMode.VersionManual, result.Value.Mode);
    }

    [Fact]
    public void IncompatibleManualVersionPathFailsWithoutFallback()
    {
        JavaInstallation incompatible = Installation("version", @"C:\Java\17\bin\java.exe", 17, "17.0.12", JavaSource.Path, false);
        JavaInstallation compatibleGlobal = Installation("global", @"C:\Java\21\bin\java.exe", 21, "21.0.1", JavaSource.Path, false);

        var result = Select(
            requiredMajor: 21,
            preferredArchitecture: JavaArchitecture.X64,
            maximumMemoryMb: 4096,
            versionJavaPath: incompatible.ExecutablePath,
            globalJavaPath: compatibleGlobal.ExecutablePath,
            installations: [incompatible, compatibleGlobal]);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_MANUAL_INCOMPATIBLE", result.Problem?.Code);
        Assert.Equal("21", result.Problem?.SafeContext["requiredMajor"]);
    }

    [Fact]
    public void GlobalManualPathIsUsedWhenVersionPathIsAbsent()
    {
        JavaInstallation global = Installation("global", @"C:\Java\21\bin\java.exe", 21, "21.0.1", JavaSource.Path, false);

        var result = Select(
            requiredMajor: 21,
            preferredArchitecture: JavaArchitecture.X64,
            maximumMemoryMb: 4096,
            versionJavaPath: null,
            globalJavaPath: @"C:\Java\21\bin\java.exe\",
            installations: [global]);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("global", result.Value.Installation.Id);
        Assert.Equal(JavaSelectionMode.GlobalManual, result.Value.Mode);
    }

    [Fact]
    public void IncompatibleGlobalManualPathFailsWithoutFallback()
    {
        JavaInstallation incompatible = Installation("global", @"C:\Java\17\bin\java.exe", 17, "17.0.12", JavaSource.Path, false);
        JavaInstallation automaticCandidate = Installation("automatic", @"C:\Java\21\bin\java.exe", 21, "21.0.1", JavaSource.Path, true);

        var result = Select(
            requiredMajor: 21,
            preferredArchitecture: JavaArchitecture.X64,
            maximumMemoryMb: 4096,
            versionJavaPath: null,
            globalJavaPath: incompatible.ExecutablePath,
            installations: [incompatible, automaticCandidate]);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_MANUAL_INCOMPATIBLE", result.Problem?.Code);
    }

    [Fact]
    public void AutomaticPrefersNativeArchitectureThenManagedRuntime()
    {
        JavaInstallation managedWrongArchitecture = Installation("managed-x86", @"C:\Managed\21-x86\bin\java.exe", 21, "21.0.9", JavaSource.Managed, true, JavaArchitecture.X86);
        JavaInstallation unmanagedNative = Installation("native", @"C:\Java\21\bin\java.exe", 21, "21.0.9", JavaSource.Path, false);
        JavaInstallation managedNative = Installation("managed-native", @"C:\Managed\21\bin\java.exe", 21, "21.0.1", JavaSource.Managed, true);

        var result = Select(
            requiredMajor: 21,
            preferredArchitecture: JavaArchitecture.X64,
            maximumMemoryMb: 4096,
            versionJavaPath: null,
            globalJavaPath: null,
            installations: [managedWrongArchitecture, unmanagedNative, managedNative]);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("managed-native", result.Value.Installation.Id);
        Assert.Equal(JavaSelectionMode.Automatic, result.Value.Mode);
    }

    [Fact]
    public void AutomaticUsesUnmanagedExactMajorWhenNoManagedCandidateExists()
    {
        JavaInstallation candidate = Installation("unmanaged", @"C:\Java\17\bin\java.exe", 17, "17.0.12", JavaSource.Path, false);

        var result = Select(
            requiredMajor: 17,
            preferredArchitecture: JavaArchitecture.X64,
            maximumMemoryMb: 2048,
            versionJavaPath: null,
            globalJavaPath: null,
            installations: [candidate]);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("unmanaged", result.Value.Installation.Id);
    }

    [Fact]
    public void AutomaticReturnsJavaMissingWhenNoExactMajorExists()
    {
        JavaInstallation newer = Installation("newer", @"C:\Java\21\bin\java.exe", 21, "21.0.1", JavaSource.Path, false);

        var result = Select(
            requiredMajor: 17,
            preferredArchitecture: JavaArchitecture.X64,
            maximumMemoryMb: 2048,
            versionJavaPath: null,
            globalJavaPath: null,
            installations: [newer]);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_MISSING", result.Problem?.Code);
        Assert.Equal("17", result.Problem?.SafeContext["requiredMajor"]);
        Assert.Equal("x64", result.Problem?.SafeContext["architecture"]);
    }

    [Fact]
    public void X86RuntimeIsRejectedForMemoryAbove1536Mib()
    {
        JavaInstallation x86 = Installation("x86", @"C:\Java\21-x86\bin\java.exe", 21, "21.0.1", JavaSource.Path, false, JavaArchitecture.X86);

        var result = Select(
            requiredMajor: 21,
            preferredArchitecture: JavaArchitecture.X86,
            maximumMemoryMb: 1537,
            versionJavaPath: null,
            globalJavaPath: null,
            installations: [x86]);

        Assert.False(result.IsSuccess);
        Assert.Equal("JAVA_ARCH_INCOMPATIBLE", result.Problem?.Code);
    }

    private static Lacertae.Domain.Results.Result<JavaSelection> Select(
        int requiredMajor,
        JavaArchitecture preferredArchitecture,
        int maximumMemoryMb,
        string? versionJavaPath,
        string? globalJavaPath,
        IReadOnlyList<JavaInstallation> installations) =>
        JavaSelector.Select(new JavaSelectionRequest(
            requiredMajor,
            preferredArchitecture,
            maximumMemoryMb,
            versionJavaPath,
            globalJavaPath,
            installations));

    private static JavaInstallation Installation(
        string id,
        string path,
        int major,
        string fullVersion,
        JavaSource source,
        bool managed,
        JavaArchitecture architecture = JavaArchitecture.X64) =>
        new(id, path, major, fullVersion, "Vendor", architecture, source, managed);
}
