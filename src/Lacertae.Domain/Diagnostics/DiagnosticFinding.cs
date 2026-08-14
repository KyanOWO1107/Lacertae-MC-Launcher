namespace Lacertae.Domain.Diagnostics;

public enum DiagnosticConfidence
{
    Confirmed,
    Likely,
    Unknown,
}

public sealed record DiagnosticFinding(
    string Code,
    DiagnosticConfidence Confidence,
    string MessageKey,
    IReadOnlyList<string> SuggestedActionKeys,
    IReadOnlyList<int> EvidenceLineNumbers);
