using System.Globalization;
using System.Text.RegularExpressions;

namespace Lacertae.Domain.Updates;

/// <summary>
/// Strict, detached-signature update metadata. The signature is transported
/// separately and is deliberately not part of this object.
/// </summary>
public sealed record UpdateManifest(
    int SchemaVersion,
    string KeyId,
    UpdateChannel Channel,
    string Version,
    DateTimeOffset PublishedUtc,
    string MinimumLauncherVersion,
    IReadOnlyDictionary<string, string> ReleaseNotes,
    Uri ReleaseNotesUrl,
    UpdatePackage Package)
{
    public const int CurrentSchemaVersion = 1;
    public const string SupportedRuntime = "win-x64";

    private static readonly Regex SemanticVersionRegex = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidSemanticVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SemanticVersionRegex.IsMatch(value);

    public static int CompareSemanticVersions(string left, string right)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        if (!TryParseSemanticVersion(left, out SemanticVersion leftVersion) ||
            !TryParseSemanticVersion(right, out SemanticVersion rightVersion))
        {
            throw new ArgumentException("Both values must be semantic versions.");
        }

        int core = CompareCore(leftVersion, rightVersion);
        if (core != 0)
        {
            return core;
        }

        if (leftVersion.PreRelease.Count == 0 && rightVersion.PreRelease.Count == 0)
        {
            return 0;
        }

        if (leftVersion.PreRelease.Count == 0)
        {
            return 1;
        }

        if (rightVersion.PreRelease.Count == 0)
        {
            return -1;
        }

        for (int index = 0; index < Math.Max(leftVersion.PreRelease.Count, rightVersion.PreRelease.Count); index++)
        {
            if (index >= leftVersion.PreRelease.Count)
            {
                return -1;
            }

            if (index >= rightVersion.PreRelease.Count)
            {
                return 1;
            }

            string leftPart = leftVersion.PreRelease[index];
            string rightPart = rightVersion.PreRelease[index];
            bool leftNumeric = int.TryParse(leftPart, NumberStyles.None, CultureInfo.InvariantCulture, out int leftNumber);
            bool rightNumeric = int.TryParse(rightPart, NumberStyles.None, CultureInfo.InvariantCulture, out int rightNumber);
            if (leftNumeric && rightNumeric)
            {
                int numeric = leftNumber.CompareTo(rightNumber);
                if (numeric != 0)
                {
                    return numeric;
                }

                continue;
            }

            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }

            int lexical = string.CompareOrdinal(leftPart, rightPart);
            if (lexical != 0)
            {
                return lexical;
            }
        }

        return 0;
    }

    private static int CompareCore(SemanticVersion left, SemanticVersion right)
    {
        int major = left.Major.CompareTo(right.Major);
        if (major != 0)
        {
            return major;
        }

        int minor = left.Minor.CompareTo(right.Minor);
        return minor != 0 ? minor : left.Patch.CompareTo(right.Patch);
    }

    private static bool TryParseSemanticVersion(string value, out SemanticVersion version)
    {
        Match match = SemanticVersionRegex.Match(value);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            version = default;
            return false;
        }

        string prerelease = match.Groups[4].Value;
        version = new SemanticVersion(
            major,
            minor,
            patch,
            prerelease.Length == 0 ? [] : prerelease.Split('.').ToArray());
        return true;
    }

    private readonly record struct SemanticVersion(
        int Major,
        int Minor,
        int Patch,
        IReadOnlyList<string> PreRelease);
}
