namespace Lacertae.Infrastructure.Accounts.Microsoft;

internal interface IMicrosoftAuthBackend
{
    Task<MicrosoftAuthBackendResult> SignInInteractivelyAsync(
        MicrosoftAuthOptions options,
        CancellationToken cancellationToken);

    Task<MicrosoftAuthBackendResult> RefreshSilentlyAsync(
        MicrosoftAuthOptions options,
        ReadOnlyMemory<byte> serializedCache,
        CancellationToken cancellationToken);
}
