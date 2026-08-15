using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public interface IMicrosoftIdentityClient
{
    Task<Result<MicrosoftLoginResult>> SignInInteractivelyAsync(CancellationToken cancellationToken);

    Task<Result<MicrosoftLoginResult>> RefreshSilentlyAsync(
        ReadOnlyMemory<byte> serializedCache,
        CancellationToken cancellationToken);
}
