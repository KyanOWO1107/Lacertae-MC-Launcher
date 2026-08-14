namespace Lacertae.Domain.Launch;

public enum GameProcessState
{
    Starting,
    Running,
    Exited,
    StartFailed,
    UserTerminated,
}

public sealed record GameLogLine(
    DateTimeOffset Timestamp,
    bool IsStandardError,
    string SanitizedText);
