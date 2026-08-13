namespace Lacertae.Domain.Versions;

public sealed record IsolationDecision(
    bool IsIsolated,
    bool RequiresUserNotice,
    string ReasonKey);
