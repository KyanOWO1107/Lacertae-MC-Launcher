using System.Security.Cryptography;
using System.Text;
using Lacertae.Application.Java;
using Lacertae.Application.Processes;
using Lacertae.Domain.Java;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Processes;

namespace Lacertae.Infrastructure.Java;

public sealed class JavaProbe(IProcessRunner processRunner) : IJavaProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<Result<JavaInstallation>> ProbeAsync(
        string executablePath,
        JavaSource source,
        bool isManaged,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string normalizedPath = NormalizePath(executablePath);
        ProcessRequest request = new(
            executablePath,
            ["-XshowSettings:properties", "-version"],
            null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ProbeTimeout,
            true);

        Result<ProcessResult> processResult = await processRunner.RunAsync(request, cancellationToken);
        if (!processResult.IsSuccess)
        {
            return Result<JavaInstallation>.Failure(Problem(
                "JAVA_PROBE_FAILED",
                executablePath,
                processResult.Problem?.SafeContext));
        }

        ProcessResult process = processResult.Value;
        if (process.TimedOut)
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_PROBE_TIMEOUT", executablePath));
        }

        if (process.ExitCode != 0)
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_PROBE_FAILED", executablePath));
        }

        Dictionary<string, string> properties = ParseProperties(process.StandardOutput, process.StandardError);
        if (!properties.TryGetValue("java.version", out string? version) ||
            !properties.TryGetValue("java.vendor", out string? vendor) ||
            !properties.TryGetValue("os.arch", out string? architecture) ||
            !properties.TryGetValue("java.home", out string? javaHome) ||
            string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(vendor) ||
            string.IsNullOrWhiteSpace(architecture) ||
            string.IsNullOrWhiteSpace(javaHome))
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_PROBE_INVALID", executablePath));
        }

        if (!TryParseMajor(version, out int majorVersion))
        {
            return Result<JavaInstallation>.Failure(Problem("JAVA_PROBE_INVALID", executablePath));
        }

        return Result<JavaInstallation>.Success(new JavaInstallation(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath))).ToLowerInvariant(),
            normalizedPath,
            majorVersion,
            version,
            vendor,
            NormalizeArchitecture(architecture),
            source,
            isManaged));
    }

    private static Dictionary<string, string> ParseProperties(string standardOutput, string standardError)
    {
        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        string combined = string.Concat(standardOutput, Environment.NewLine, standardError);
        foreach (string rawLine in combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            if (key is not ("java.version" or "java.vendor" or "os.arch" or "java.home"))
            {
                continue;
            }

            properties[key] = line[(separator + 1)..].Trim();
        }

        return properties;
    }

    private static bool TryParseMajor(string version, out int major)
    {
        major = 0;
        string candidate = version.StartsWith("1.", StringComparison.Ordinal)
            ? version[2..]
            : version;
        if (version.StartsWith("1.", StringComparison.Ordinal))
        {
            int separator = candidate.IndexOf('.');
            candidate = separator >= 0 ? candidate[..separator] : candidate;
        }

        int digitCount = 0;
        while (digitCount < candidate.Length && char.IsAsciiDigit(candidate[digitCount]))
        {
            digitCount++;
        }

        return digitCount > 0 &&
               int.TryParse(candidate[..digitCount], out major) &&
               major >= 1;
    }

    private static JavaArchitecture NormalizeArchitecture(string architecture) =>
        architecture.Trim().ToLowerInvariant() switch
        {
            "x86" or "i386" or "i486" or "i586" or "i686" => JavaArchitecture.X86,
            "amd64" or "x86_64" or "x86-64" or "x64" => JavaArchitecture.X64,
            "aarch64" or "arm64" => JavaArchitecture.Arm64,
            _ => JavaArchitecture.Unknown,
        };

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static Problem Problem(
        string code,
        string executablePath,
        IReadOnlyDictionary<string, string>? processContext = null)
    {
        Dictionary<string, string> context = new(StringComparer.Ordinal)
        {
            ["executable"] = SystemProcessRunner.GetSafeBasename(executablePath),
        };
        if (processContext is not null &&
            processContext.TryGetValue("executable", out string? safeExecutable) &&
            !string.IsNullOrWhiteSpace(safeExecutable))
        {
            context["executable"] = SystemProcessRunner.GetSafeBasename(safeExecutable);
        }

        return new Problem(
            code,
            ProblemStage.JavaResolution,
            "problem.java.probe_failed",
            code is "JAVA_PROBE_TIMEOUT" or "JAVA_PROBE_FAILED",
            Guid.NewGuid().ToString("N"),
            ["action.java.check_runtime"],
            context);
    }
}
