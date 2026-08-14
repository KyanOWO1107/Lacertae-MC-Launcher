namespace Lacertae.Domain.Home;

public sealed record HomeModulePlacement(HomeModuleId Module, int Order, bool IsVisible)
{
    private static readonly HomeModulePlacement[] DefaultEntries =
    [
        new(HomeModuleId.RecentVersions, 0, true),
        new(HomeModuleId.ActiveTasks, 1, true),
        new(HomeModuleId.QuickActions, 2, true),
        new(HomeModuleId.ReleaseNotes, 3, true),
    ];

    public static IReadOnlyList<HomeModulePlacement> Defaults { get; } =
        Array.AsReadOnly(DefaultEntries);

    public static bool IsValid(IReadOnlyList<HomeModulePlacement>? placements)
    {
        if (placements is null || placements.Count != Defaults.Count)
        {
            return false;
        }

        HashSet<HomeModuleId> modules = [];
        HashSet<int> orders = [];
        foreach (HomeModulePlacement? placement in placements)
        {
            if (placement is null ||
                !Enum.IsDefined(placement.Module) ||
                placement.Order is < 0 or > 99 ||
                !modules.Add(placement.Module) ||
                !orders.Add(placement.Order))
            {
                return false;
            }
        }

        return modules.Count == Defaults.Count &&
            Enum.GetValues<HomeModuleId>().All(modules.Contains);
    }

    public static IReadOnlyList<HomeModulePlacement> CopyDefaults() =>
        Array.AsReadOnly(DefaultEntries.Select(static placement => placement with { }).ToArray());
}
