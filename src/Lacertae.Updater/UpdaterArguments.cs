namespace Lacertae.Updater;

/// <summary>
/// Strict command-line contract for the standalone updater. Keeping the
/// updater to one absolute plan path prevents command injection and makes the
/// staged update auditable.
/// </summary>
public sealed record UpdaterArguments(string PlanPath)
{
    public static UpdaterArguments Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 2 || !string.Equals(arguments[0], "--plan", StringComparison.Ordinal))
        {
            throw new ArgumentException("Updater accepts exactly '--plan <absolute-path>'.", nameof(arguments));
        }

        string planPath = arguments[1];
        if (string.IsNullOrWhiteSpace(planPath) ||
            !Path.IsPathFullyQualified(planPath) ||
            !string.Equals(Path.GetFullPath(planPath), planPath, GetPathComparison()))
        {
            throw new ArgumentException("The update plan path must be an absolute, normalized path.", nameof(arguments));
        }

        return new UpdaterArguments(planPath);
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
