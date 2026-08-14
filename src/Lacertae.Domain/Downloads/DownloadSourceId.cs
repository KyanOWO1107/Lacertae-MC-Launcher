namespace Lacertae.Domain.Downloads;

public sealed record DownloadSourceId
{
    public DownloadSourceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Download source ID cannot be blank.", nameof(value));
        }

        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 64 || !normalized.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            throw new ArgumentException("Download source ID contains unsupported characters.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
