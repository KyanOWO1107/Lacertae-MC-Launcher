using Lacertae.Application.Operations;
using Lacertae.Domain.GameRoots;
using Lacertae.Domain.Install;

namespace Lacertae.Application.Install;

public sealed class RepairVanillaOperation : VanillaOperationBase
{
    public RepairVanillaOperation(
        PlanVanillaInstall planner,
        ExecuteVanillaInstall executor,
        GameRoot gameRoot,
        string versionId,
        VanillaPlatform platform,
        IBackgroundTaskStore store)
        : base(planner, executor, gameRoot, versionId, platform, InstallAction.Repair, "vanilla-repair", store)
    {
    }
}
