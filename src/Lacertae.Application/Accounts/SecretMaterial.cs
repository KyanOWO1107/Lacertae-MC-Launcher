using System.Security.Cryptography;

namespace Lacertae.Application.Accounts;

/// <summary>
/// Owns a short-lived serialized credential payload and clears it when disposed.
/// </summary>
public sealed class SecretMaterial : IDisposable
{
    private byte[] bytes;

    public SecretMaterial(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new ArgumentException("Secret material cannot be empty.", nameof(value));
        }

        bytes = value.ToArray();
    }

    public ReadOnlyMemory<byte> Bytes => bytes;

    public void Dispose()
    {
        if (bytes.Length == 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(bytes);
        bytes = [];
    }

    public override string ToString() => "[SECRET]";
}
