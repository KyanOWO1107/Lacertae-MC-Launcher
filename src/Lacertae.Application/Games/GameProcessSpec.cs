using Lacertae.Domain.Accounts;

namespace Lacertae.Application.Games;

public sealed class GameProcessSpec
{
    public GameProcessSpec(
        string fileName,
        IReadOnlyList<SensitiveString> argumentList,
        string workingDirectory,
        IReadOnlyDictionary<string, SensitiveString> environment)
    {
        FileName = string.IsNullOrWhiteSpace(fileName)
            ? throw new ArgumentException("Executable path cannot be blank.", nameof(fileName))
            : Path.GetFullPath(fileName);
        ArgumentList = argumentList?.ToArray() ?? throw new ArgumentNullException(nameof(argumentList));
        if (ArgumentList.Any(static argument => argument is null))
        {
            throw new ArgumentException("Argument list cannot contain null entries.", nameof(argumentList));
        }

        WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ArgumentException("Working directory cannot be blank.", nameof(workingDirectory))
            : Path.GetFullPath(workingDirectory);
        Environment = environment?.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal)
            ?? throw new ArgumentNullException(nameof(environment));
        if (Environment.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException("Environment entries are invalid.", nameof(environment));
        }
    }

    public string FileName { get; }

    public IReadOnlyList<SensitiveString> ArgumentList { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyDictionary<string, SensitiveString> Environment { get; }

    public override string ToString() =>
        $"GameProcessSpec({Path.GetFileName(FileName)}, [REDACTED ARGUMENTS])";
}
