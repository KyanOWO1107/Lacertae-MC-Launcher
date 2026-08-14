namespace Lacertae.Domain.Diagnostics;

public sealed record GameCrashReport(
    int ExitCode,
    IReadOnlyList<DiagnosticFinding> Findings,
    string SanitizedLogPath,
    string CorrelationId);
