namespace Lacertae.Domain.Versions;

/// <summary>
/// Validates a Minecraft version folder name before it is used in a path.
/// Mojang's official manifest contains historical IDs with spaces, so spaces
/// are allowed only as internal ASCII characters.
/// </summary>
public static class VersionFolderPolicy
{
    public static bool IsSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            value is "." or ".." ||
            value[0] == ' ' ||
            value[^1] is ' ' or '.')
        {
            return false;
        }

        return value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or ' ');
    }
}
