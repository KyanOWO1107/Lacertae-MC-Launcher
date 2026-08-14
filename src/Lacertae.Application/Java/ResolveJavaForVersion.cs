using Lacertae.Domain.Java;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Java;

public sealed record ResolvedJavaLaunchSettings(
    JavaInstallation Installation,
    MemoryAllocation Memory,
    JvmArgumentSet JvmArguments)
{
    public IReadOnlyList<string> FlattenedJvmArguments => JvmArguments.Flatten();
}

public static class ResolveJavaForVersion
{
    public static Result<ResolvedJavaLaunchSettings> Execute(
        GameVersionDescriptor version,
        VersionOverride versionOverride,
        LauncherSettings launcherSettings,
        JavaDiscoveryResult discovery,
        long totalPhysicalMb,
        long availableMb,
        JavaArchitecture preferredArchitecture)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(versionOverride);
        ArgumentNullException.ThrowIfNull(launcherSettings);
        ArgumentNullException.ThrowIfNull(discovery);

        if (version.Java is null || discovery.Installations is null || versionOverride.JvmArguments is null ||
            !Enum.IsDefined(preferredArchitecture))
        {
            return Result<ResolvedJavaLaunchSettings>.Failure(
                JavaSettingsProblem("JAVA_SETTINGS_INVALID", version, preferredArchitecture));
        }

        int maximumMemoryForJavaSelection = versionOverride.MaximumMemoryMb ?? 2048;
        Result<JavaSelection> javaResult = JavaSelector.Select(new JavaSelectionRequest(
            version.Java.MajorVersion,
            preferredArchitecture,
            maximumMemoryForJavaSelection,
            versionOverride.JavaPath,
            launcherSettings.GlobalJavaPath,
            discovery.Installations));
        if (!javaResult.IsSuccess)
        {
            return Result<ResolvedJavaLaunchSettings>.Failure(javaResult.Problem!);
        }

        MemoryMode memoryMode = versionOverride.MinimumMemoryMb is null && versionOverride.MaximumMemoryMb is null
            ? MemoryMode.Automatic
            : MemoryMode.Fixed;
        Result<MemoryAllocation> memoryResult = MemoryResolver.Resolve(new MemoryRequest(
            memoryMode,
            versionOverride.MinimumMemoryMb,
            versionOverride.MaximumMemoryMb,
            version.HasModLoader,
            0), totalPhysicalMb, availableMb);
        if (!memoryResult.IsSuccess)
        {
            return Result<ResolvedJavaLaunchSettings>.Failure(memoryResult.Problem!);
        }

        Result<JvmArgumentSet> jvmResult = JvmArgumentResolver.Resolve(
            versionOverride.GcProfile ?? GcProfile.Automatic,
            javaResult.Value.Installation.MajorVersion,
            javaResult.Value.Installation.Architecture,
            memoryResult.Value,
            versionOverride.JvmArguments);
        if (!jvmResult.IsSuccess)
        {
            return Result<ResolvedJavaLaunchSettings>.Failure(jvmResult.Problem!);
        }

        return Result<ResolvedJavaLaunchSettings>.Success(new ResolvedJavaLaunchSettings(
            javaResult.Value.Installation,
            memoryResult.Value,
            jvmResult.Value));
    }

    private static Lacertae.Domain.Problems.Problem JavaSettingsProblem(
        string code,
        GameVersionDescriptor version,
        JavaArchitecture architecture) => new(
        code,
        Lacertae.Domain.Problems.ProblemStage.JavaResolution,
        "problem.java.settings_invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.java.review_settings"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["requiredMajor"] = version.Java?.MajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            ["architecture"] = architecture.ToString().ToLowerInvariant(),
        });
}
