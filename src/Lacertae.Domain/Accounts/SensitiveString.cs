namespace Lacertae.Domain.Accounts;

/// <summary>
/// A short-lived secret that can only be revealed at the process boundary.
/// </summary>
public sealed class SensitiveString
{
    private readonly string value;

    public SensitiveString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Secret cannot be blank.", nameof(value));
        }

        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("Secret contains a forbidden control character.", nameof(value));
        }

        this.value = value;
    }

    public string Reveal() => value;

    public override string ToString() => "[SECRET]";
}
