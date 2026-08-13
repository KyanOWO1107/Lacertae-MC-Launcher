using System.Text.RegularExpressions;
using Lacertae.Application.Diagnostics;

namespace Lacertae.Infrastructure.Diagnostics;

public sealed partial class LogSanitizer : ILogSanitizer
{
    private readonly string[] prefixes;

    public LogSanitizer(IEnumerable<string> privatePathPrefixes)
    {
        ArgumentNullException.ThrowIfNull(privatePathPrefixes);
        prefixes = privatePathPrefixes
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .OrderByDescending(static value => value.Length)
            .ToArray();
    }

    public string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string sanitized = BearerRegex().Replace(value, "$1[REDACTED]");
        sanitized = SecretQueryRegex().Replace(sanitized, "$1[REDACTED]");
        foreach (string prefix in prefixes)
        {
            sanitized = sanitized.Replace(prefix, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }

    [GeneratedRegex("(?i)(Authorization:\\s*Bearer\\s+)[^\\s]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)((?:code|state|access_token|refresh_token)=)[^&\\s]+")]
    private static partial Regex SecretQueryRegex();
}
