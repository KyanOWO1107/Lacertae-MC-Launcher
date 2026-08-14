using Lacertae.Application.Operations;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Install;

namespace Lacertae.Application.Install;

public sealed class InstallVanillaOperation : VanillaOperationBase
{
    public InstallVanillaOperation(
        PlanVanillaInstall planner,
        ExecuteVanillaInstall executor,
        GameRoot gameRoot,
        string versionId,
        VanillaPlatform platform,
        InstallAction action,
        IBackgroundTaskStore store)
        : base(planner, executor, gameRoot, versionId, platform, action, "vanilla-install", store)
    {
    }
}
