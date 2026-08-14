using System.Globalization;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Domain.Java;

public static class JvmArgumentResolver
{
    private const int MaximumArgumentLength = 8 * 1024;
    private const int MinimumSupportedG1Major = 8;
    private const int MinimumSupportedZgcMajor = 17;

    public static Result<JvmArgumentSet> Resolve(
        GcProfile profile,
        int javaMajor,
        JavaArchitecture architecture,
        MemoryAllocation memory,
        IReadOnlyList<string> userArguments)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(userArguments);

        if (!Enum.IsDefined(profile) || !Enum.IsDefined(architecture) || javaMajor < 1 ||
            memory.MinimumMb < 1 || memory.MaximumMb < memory.MinimumMb || !Enum.IsDefined(memory.Mode))
        {
            return Result<JvmArgumentSet>.Failure(Problem("JVM_ARGUMENT_INVALID", profile, javaMajor, architecture));
        }

        Result<IReadOnlyList<string>> collectorResult = ResolveCollector(profile, javaMajor, architecture);
        if (!collectorResult.IsSuccess)
        {
            return Result<JvmArgumentSet>.Failure(collectorResult.Problem!);
        }

        List<string> distinctUserArguments = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 0; index < userArguments.Count; index++)
        {
            string? argument = userArguments[index];
            if (string.IsNullOrWhiteSpace(argument) || argument.Length > MaximumArgumentLength ||
                argument.IndexOfAny(['\0', '\r', '\n']) >= 0 || IsStructuredOverride(argument))
            {
                return Result<JvmArgumentSet>.Failure(ArgumentConflictProblem(profile, javaMajor, architecture, index));
            }

            if (seen.Add(argument))
            {
                distinctUserArguments.Add(argument);
            }
        }

        string[] memoryArguments =
        [
            $"-Xms{memory.MinimumMb.ToString(CultureInfo.InvariantCulture)}M",
            $"-Xmx{memory.MaximumMb.ToString(CultureInfo.InvariantCulture)}M",
        ];
        return Result<JvmArgumentSet>.Success(new JvmArgumentSet(
            memoryArguments,
            collectorResult.Value,
            distinctUserArguments));
    }

    private static Result<IReadOnlyList<string>> ResolveCollector(
        GcProfile profile,
        int javaMajor,
        JavaArchitecture architecture) => profile switch
        {
            GcProfile.Automatic or GcProfile.G1 when javaMajor >= MinimumSupportedG1Major =>
                Result<IReadOnlyList<string>>.Success(["-XX:+UseG1GC"]),
            GcProfile.Zgc when javaMajor >= MinimumSupportedZgcMajor &&
                architecture is JavaArchitecture.X64 or JavaArchitecture.Arm64 =>
                Result<IReadOnlyList<string>>.Success(["-XX:+UseZGC"]),
            GcProfile.None => Result<IReadOnlyList<string>>.Success([]),
            _ => Result<IReadOnlyList<string>>.Failure(Problem("JVM_GC_INCOMPATIBLE", profile, javaMajor, architecture)),
        };

    private static bool IsStructuredOverride(string argument) =>
        argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase) ||
        (argument.StartsWith("-XX:+Use", StringComparison.OrdinalIgnoreCase) &&
            argument.EndsWith("GC", StringComparison.OrdinalIgnoreCase));

    private static Problem ArgumentConflictProblem(
        GcProfile profile,
        int javaMajor,
        JavaArchitecture architecture,
        int index) => new(
        "JVM_ARGUMENT_CONFLICT",
        ProblemStage.JavaResolution,
        "problem.jvm.argument_conflict",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.java.review_jvm_arguments"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile"] = profile.ToString().ToLowerInvariant(),
            ["javaMajor"] = javaMajor.ToString(CultureInfo.InvariantCulture),
            ["architecture"] = architecture.ToString().ToLowerInvariant(),
            ["index"] = index.ToString(CultureInfo.InvariantCulture),
            ["line"] = (index + 1).ToString(CultureInfo.InvariantCulture),
        });

    private static Problem Problem(
        string code,
        GcProfile profile,
        int javaMajor,
        JavaArchitecture architecture) => new(
        code,
        ProblemStage.JavaResolution,
        "problem.jvm.resolve_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.java.review_jvm_arguments"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile"] = profile.ToString().ToLowerInvariant(),
            ["javaMajor"] = javaMajor.ToString(CultureInfo.InvariantCulture),
            ["architecture"] = architecture.ToString().ToLowerInvariant(),
        });
}
