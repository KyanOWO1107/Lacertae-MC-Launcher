using System.Text;
using System.Text.RegularExpressions;
using Lacertae.Application.Diagnostics;
using Lacertae.Domain.Diagnostics;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Infrastructure.Diagnostics;

public sealed partial class GameCrashAnalyzer : IGameCrashAnalyzer
{
    private const int MaximumLines = 20_000;
    private const int MaximumBytes = 5 * 1024 * 1024;

    public async Task<Result<GameCrashReport>> AnalyzeAsync(
        LaunchPlan plan,
        GameExitResult gameExit,
        string sanitizedLogPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(gameExit);
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedLogPath);
        if (!Path.IsPathFullyQualified(sanitizedLogPath) || gameExit.ExitCode is null)
        {
            return Result<GameCrashReport>.Failure(InvalidProblem());
        }

        List<BoundedLine> lines = await ReadBoundedLinesAsync(sanitizedLogPath, cancellationToken);
        List<BoundedLine> nativeLines = await ReadNativeCrashLinesAsync(plan, gameExit.StartedUtc, cancellationToken);
        List<DiagnosticFinding> findings = AnalyzeLines(lines, nativeLines, gameExit.ExitCode.Value);
        return Result<GameCrashReport>.Success(new GameCrashReport(
            gameExit.ExitCode.Value,
            findings,
            Path.GetFullPath(sanitizedLogPath),
            string.IsNullOrWhiteSpace(gameExit.CorrelationId) ? plan.CorrelationId : gameExit.CorrelationId));
    }

    private static List<DiagnosticFinding> AnalyzeLines(
        IReadOnlyList<BoundedLine> lines,
        IReadOnlyList<BoundedLine> nativeLines,
        int exitCode)
    {
        List<DiagnosticFinding> findings = [];
        AddFinding(
            findings,
            "JAVA_OOM",
            DiagnosticConfidence.Confirmed,
            "diagnostic.java.oom",
            ["action.launch.reduce_memory", "action.diagnostics.open_log"],
            lines,
            static line => OomRegex().IsMatch(line.Text));
        AddFinding(
            findings,
            "JAVA_CLASS_VERSION_UNSUPPORTED",
            DiagnosticConfidence.Confirmed,
            "diagnostic.java.class_version_unsupported",
            ["action.java.select_matching_runtime", "action.diagnostics.open_log"],
            lines,
            static line => ClassVersionRegex().IsMatch(line.Text));
        AddFinding(
            findings,
            "MINECRAFT_MAIN_CLASS_MISSING",
            DiagnosticConfidence.Confirmed,
            "diagnostic.minecraft.main_class_missing",
            ["action.version.repair", "action.diagnostics.open_log"],
            lines,
            static line => MainClassRegex().IsMatch(line.Text));
        AddFinding(
            findings,
            "PATH_ACCESS_DENIED",
            DiagnosticConfidence.Confirmed,
            "diagnostic.path.access_denied",
            ["action.launch.review_permissions", "action.diagnostics.open_log"],
            lines,
            static line => AccessDeniedRegex().IsMatch(line.Text));
        AddFinding(
            findings,
            "NATIVE_CRASH",
            DiagnosticConfidence.Confirmed,
            "diagnostic.native_crash",
            ["action.diagnostics.open_native_crash", "action.java.check_runtime"],
            nativeLines,
            static line => NativeCrashRegex().IsMatch(line.Text));

        if (exitCode != 0 && findings.Count == 0)
        {
            int evidence = lines.Count == 0 ? 0 : lines[^1].LineNumber;
            findings.Add(new DiagnosticFinding(
                "GAME_ABNORMAL_EXIT",
                DiagnosticConfidence.Unknown,
                "diagnostic.game.abnormal_exit",
                ["action.diagnostics.open_log"],
                evidence == 0 ? [] : [evidence]));
        }

        return findings;
    }

    private static void AddFinding(
        List<DiagnosticFinding> findings,
        string code,
        DiagnosticConfidence confidence,
        string messageKey,
        IReadOnlyList<string> actions,
        IReadOnlyList<BoundedLine> lines,
        Func<BoundedLine, bool> predicate)
    {
        int[] evidence = lines.Where(predicate).Select(static line => line.LineNumber).Distinct().Order().ToArray();
        if (evidence.Length > 0)
        {
            findings.Add(new DiagnosticFinding(code, confidence, messageKey, actions, evidence));
        }
    }

    private static async Task<List<BoundedLine>> ReadBoundedLinesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        long start;
        byte[] bytes;
        await using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            start = Math.Max(0, stream.Length - MaximumBytes);
            stream.Position = start;
            int length = checked((int)Math.Min(MaximumBytes, stream.Length - start));
            bytes = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(offset, length - offset), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            if (offset != length)
            {
                Array.Resize(ref bytes, offset);
            }
        }

        string text = Encoding.UTF8.GetString(bytes);
        int firstNewLine = start > 0 ? text.IndexOf('\n') : -1;
        if (firstNewLine >= 0)
        {
            text = text[(firstNewLine + 1)..];
        }

        string[] rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        int skip = Math.Max(0, rawLines.Length - MaximumLines);
        List<BoundedLine> lines = [];
        for (int index = skip; index < rawLines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == rawLines.Length - 1 && rawLines[index].Length == 0)
            {
                continue;
            }

            lines.Add(new BoundedLine(index - skip + 1, rawLines[index]));
        }

        return lines;
    }

    private static async Task<List<BoundedLine>> ReadNativeCrashLinesAsync(
        LaunchPlan plan,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(plan.GameDirectory))
        {
            return [];
        }

        List<BoundedLine> lines = [];
        foreach (string path in Directory.EnumerateFiles(plan.GameDirectory, "hs_err_pid*.log", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTime lastWrite = File.GetLastWriteTimeUtc(path);
            if (lastWrite < startedUtc.UtcDateTime)
            {
                continue;
            }

            List<BoundedLine> native = await ReadBoundedLinesAsync(path, cancellationToken);
            lines.AddRange(native);
        }

        return lines;
    }

    private static Problem InvalidProblem() => new(
        "GAME_CRASH_ANALYSIS_INVALID",
        ProblemStage.Process,
        "problem.diagnostics.analysis_invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.diagnostics.open_log"]);

    private sealed record BoundedLine(int LineNumber, string Text);

    [GeneratedRegex("(?i)OutOfMemoryError|Java heap space")]
    private static partial Regex OomRegex();

    [GeneratedRegex("(?i)UnsupportedClassVersionError|class file version")]
    private static partial Regex ClassVersionRegex();

    [GeneratedRegex("(?i)Could not find or load main class|main class .* not found")]
    private static partial Regex MainClassRegex();

    [GeneratedRegex("(?i)AccessDeniedException|access is denied|permission denied")]
    private static partial Regex AccessDeniedRegex();

    [GeneratedRegex("(?i)fatal error has been detected by the Java Runtime Environment|# A fatal error")]
    private static partial Regex NativeCrashRegex();
}
