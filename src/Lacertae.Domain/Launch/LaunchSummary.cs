namespace Lacertae.Domain.Launch;

/// <summary>
/// Safe, persistable launch diagnostics. It intentionally has no AuthSession.
/// </summary>
public sealed record LaunchSummary(
    string CorrelationId,
    string GameRootId,
    string VersionFolder,
    string AccountId,
    string JavaInstallationId,
    DateTimeOffset CreatedUtc)
{
    public static LaunchSummary From(LaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new LaunchSummary(
            plan.CorrelationId,
            plan.GameRootId,
            plan.VersionFolder,
            plan.AccountId,
            plan.JavaInstallationId,
            plan.CreatedUtc);
    }
}
