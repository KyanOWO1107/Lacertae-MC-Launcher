using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace Lacertae.Infrastructure.Accounts.Microsoft;

/// <summary>
/// Bridges one account's serialized MSAL cache into callbacks without writing it to a shared file.
/// </summary>
internal sealed class MsalCacheBridge : IDisposable
{
    private byte[]? initialCache;
    private byte[]? latestCache;
    private bool loaded;

    internal MsalCacheBridge(ReadOnlyMemory<byte> initialCache)
    {
        this.initialCache = initialCache.IsEmpty ? null : initialCache.ToArray();
    }

    internal void Attach(ITokenCache tokenCache)
    {
        ArgumentNullException.ThrowIfNull(tokenCache);
        tokenCache.SetBeforeAccess(args =>
        {
            if (loaded || initialCache is null)
            {
                return;
            }

            args.TokenCache.DeserializeMsalV3(initialCache, shouldClearExistingCache: true);
            loaded = true;
        });
        tokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
            {
                return;
            }

            byte[] serialized = args.TokenCache.SerializeMsalV3();
            Replace(ref latestCache, serialized);
        });
    }

    internal byte[]? TakeLatestCache()
    {
        byte[]? source = latestCache ?? initialCache;
        return source is null ? null : source.ToArray();
    }

    public void Dispose()
    {
        Replace(ref initialCache, null);
        Replace(ref latestCache, null);
    }

    private static void Replace(ref byte[]? target, byte[]? replacement)
    {
        if (target is not null)
        {
            CryptographicOperations.ZeroMemory(target);
        }

        target = replacement;
    }
}
