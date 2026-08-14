using Lacertae.Domain.Common;
using Lacertae.Domain.Install;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Install;

public sealed record InstallJournalRecord(
    VanillaInstallPlan Plan,
    InstallJournal Journal);

public interface IInstallJournalRepository
{
    Task<Result<Unit>> SaveAsync(
        VanillaInstallPlan plan,
        InstallJournal journal,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<InstallJournalRecord>>> GetRecoverableAsync(
        CancellationToken cancellationToken);

    Task<Result<Unit>> RemoveAsync(
        string operationId,
        CancellationToken cancellationToken);
}
