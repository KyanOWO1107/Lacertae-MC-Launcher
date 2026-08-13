using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Domain.Java;

public static class JavaSelector
{
    public static Result<JavaSelection> Select(JavaSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequiredMajor < 1 || request.Installations is null)
        {
            throw new ArgumentException("Java selection request is invalid.", nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.VersionJavaPath))
        {
            return SelectManual(request, request.VersionJavaPath, JavaSelectionMode.VersionManual);
        }

        if (!string.IsNullOrWhiteSpace(request.GlobalJavaPath))
        {
            return SelectManual(request, request.GlobalJavaPath, JavaSelectionMode.GlobalManual);
        }

        JavaInstallation[] exactMajor = request.Installations
            .Where(installation => installation.MajorVersion == request.RequiredMajor)
            .ToArray();
        if (exactMajor.Length == 0)
        {
            return Result<JavaSelection>.Failure(Problem("JAVA_MISSING", request, "action.java.install_managed"));
        }

        JavaInstallation[] compatible = exactMajor
            .Where(installation => installation.Architecture != JavaArchitecture.X86 || request.MaximumMemoryMb <= 1536)
            .OrderByDescending(installation => installation.Architecture == request.PreferredArchitecture)
            .ThenByDescending(static installation => installation.IsManaged)
            .ThenByDescending(static installation => ParseVersion(installation.FullVersion))
            .ThenBy(static installation => installation.Id, StringComparer.Ordinal)
            .ToArray();
        if (compatible.Length == 0)
        {
            return Result<JavaSelection>.Failure(Problem("JAVA_ARCH_INCOMPATIBLE", request, "action.java.select_x64"));
        }

        return Result<JavaSelection>.Success(new JavaSelection(compatible[0], JavaSelectionMode.Automatic));
    }

    private static Result<JavaSelection> SelectManual(
        JavaSelectionRequest request,
        string configuredPath,
        JavaSelectionMode mode)
    {
        JavaInstallation? installation = request.Installations.FirstOrDefault(candidate =>
            PathsEqual(candidate.ExecutablePath, configuredPath));
        if (installation is null)
        {
            return Result<JavaSelection>.Failure(Problem("JAVA_MANUAL_NOT_FOUND", request, "action.java.choose_runtime"));
        }

        if (installation.MajorVersion != request.RequiredMajor)
        {
            return Result<JavaSelection>.Failure(Problem("JAVA_MANUAL_INCOMPATIBLE", request, "action.java.choose_runtime"));
        }

        if (installation.Architecture == JavaArchitecture.X86 && request.MaximumMemoryMb > 1536)
        {
            return Result<JavaSelection>.Failure(Problem("JAVA_ARCH_INCOMPATIBLE", request, "action.java.select_x64"));
        }

        return Result<JavaSelection>.Success(new JavaSelection(installation, mode));
    }

    private static bool PathsEqual(string left, string right)
    {
        string normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        string normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return OperatingSystem.IsWindows()
            ? string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase)
            : string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static Version ParseVersion(string value)
    {
        string[] parts = value.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        int[] numbers = parts.Select(static part => int.TryParse(part, out int number) ? number : 0).ToArray();
        return new Version(numbers.Length > 0 ? numbers[0] : 0, numbers.Length > 1 ? numbers[1] : 0,
            numbers.Length > 2 ? numbers[2] : 0, numbers.Length > 3 ? numbers[3] : 0);
    }

    private static Problem Problem(string code, JavaSelectionRequest request, string action) => new(
        code,
        ProblemStage.JavaResolution,
        "problem.java.selection_failed",
        code is "JAVA_MISSING",
        Guid.NewGuid().ToString("N"),
        [action],
        new Dictionary<string, string>
        {
            ["requiredMajor"] = request.RequiredMajor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["architecture"] = request.PreferredArchitecture.ToString().ToLowerInvariant(),
        });
}
