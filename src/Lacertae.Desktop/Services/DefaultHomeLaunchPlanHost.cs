using Lacertae.Application.Home;
using Lacertae.Application.Launch;
using Lacertae.Domain.Accounts;
using Lacertae.Domain.Launch;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.Services;

public sealed class DefaultHomeLaunchPlanHost : IHomeLaunchPlanHost
{
    private readonly FreezeLaunchPlan freezeLaunchPlan = new();

    public Task<Result<LaunchPlan>> FreezeAsync(
        HomeLaunchContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.AccountType != AccountType.Offline)
        {
            return Task.FromResult(Result<LaunchPlan>.Failure(new Problem(
                "AUTH_NOT_CONFIGURED",
                ProblemStage.Authentication,
                "problem.auth.not_configured",
                false,
                Guid.NewGuid().ToString("N"),
                ["action.auth.sign_in_again"])));
        }

        Account account = new(
            context.AccountId,
            context.AccountIdentity,
            context.AccountType,
            context.AccountPlayerName,
            null,
            null,
            AccountStatus.Active,
            null);
        AuthSession session = new(
            context.AccountPlayerName,
            context.AccountIdentity.ProfileUuid,
            new SensitiveString("offline-session"),
            "legacy",
            null,
            null);
        LaunchFreezeRequest request = new(
            context.GameRoot,
            context.Version.Descriptor,
            context.Version.Settings,
            context.GlobalSettings,
            account,
            session,
            context.JavaSettings,
            []);
        return freezeLaunchPlan.ExecuteAsync(request, cancellationToken);
    }
}
