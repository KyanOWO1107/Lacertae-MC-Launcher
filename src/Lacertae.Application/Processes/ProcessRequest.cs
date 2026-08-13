namespace Lacertae.Application.Processes;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> ArgumentList,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    bool CreateNoWindow);
