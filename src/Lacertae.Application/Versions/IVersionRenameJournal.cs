using Lacertae.Domain.Common;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Application.Versions;

public interface IVersionRenameJournal
{
    Task<Result<Unit>> WriteAsync(
        VersionRenameJournalEntry entry,
        CancellationToken cancellationToken);

    Task<Result<VersionRenameJournalEntry?>> ReadAsync(
        CancellationToken cancellationToken);

    Task<Result<Unit>> DeleteAsync(CancellationToken cancellationToken);
}
