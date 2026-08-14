namespace Lacertae.Domain.Install;

public enum InstallJournalState
{
    Planned,
    Staging,
    Verified,
    Committing,
    Completed,
    RollbackRequired,
}
