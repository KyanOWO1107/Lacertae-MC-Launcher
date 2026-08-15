using Lacertae.Domain.Common;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Accounts;

public interface ISecretVault
{
    Task<Result<Unit>> WriteAsync(
        string secretRef,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken);

    Task<Result<byte[]>> ReadAsync(
        string secretRef,
        CancellationToken cancellationToken);

    Task<Result<Unit>> DeleteAsync(
        string secretRef,
        CancellationToken cancellationToken);
}
