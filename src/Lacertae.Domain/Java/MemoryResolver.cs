using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Domain.Java;

public static class MemoryResolver
{
    private const int FixedMinimumMb = 512;
    private const int AutomaticMinimumMb = 1024;
    private const int SystemReserveMb = 1024;
    private const int MemoryStepMb = 256;
    private const int VanillaTargetMb = 2048;
    private const int ModLoaderTargetIncrementMb = 1024;
    private const int LargeModPackTargetIncrementMb = 1024;
    private const int LargeModPackThreshold = 100;

    public static Result<MemoryAllocation> Resolve(
        MemoryRequest request,
        long totalPhysicalMb,
        long availableMb)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (totalPhysicalMb <= 0 || availableMb < 0 || availableMb > totalPhysicalMb || request.ModCount < 0)
        {
            return Result<MemoryAllocation>.Failure(Problem("MEMORY_INVALID", request, totalPhysicalMb, availableMb));
        }

        return request.Mode switch
        {
            MemoryMode.Fixed => ResolveFixed(request, totalPhysicalMb, availableMb),
            MemoryMode.Automatic => ResolveAutomatic(request, totalPhysicalMb, availableMb),
            _ => Result<MemoryAllocation>.Failure(Problem("MEMORY_INVALID", request, totalPhysicalMb, availableMb)),
        };
    }

    private static Result<MemoryAllocation> ResolveFixed(
        MemoryRequest request,
        long totalPhysicalMb,
        long availableMb)
    {
        if (request.MinimumMb is not int minimumMb || request.MaximumMb is not int maximumMb ||
            minimumMb < FixedMinimumMb || maximumMb < minimumMb)
        {
            return Result<MemoryAllocation>.Failure(Problem("MEMORY_INVALID", request, totalPhysicalMb, availableMb));
        }

        long availableForGameMb = availableMb - SystemReserveMb;
        if (availableForGameMb < 0 || maximumMb > availableForGameMb)
        {
            return Result<MemoryAllocation>.Failure(Problem("MEMORY_INSUFFICIENT", request, totalPhysicalMb, availableMb));
        }

        return Result<MemoryAllocation>.Success(new MemoryAllocation(minimumMb, maximumMb, MemoryMode.Fixed));
    }

    private static Result<MemoryAllocation> ResolveAutomatic(
        MemoryRequest request,
        long totalPhysicalMb,
        long availableMb)
    {
        long targetMb = VanillaTargetMb;
        if (request.HasModLoader)
        {
            targetMb += ModLoaderTargetIncrementMb;
        }

        if (request.ModCount > LargeModPackThreshold)
        {
            targetMb += LargeModPackTargetIncrementMb;
        }

        long availableForGameMb = availableMb - SystemReserveMb;
        long maximumSafeMb = Math.Min(totalPhysicalMb / 2, availableForGameMb);
        if (maximumSafeMb < AutomaticMinimumMb)
        {
            return Result<MemoryAllocation>.Failure(Problem("MEMORY_INSUFFICIENT", request, totalPhysicalMb, availableMb));
        }

        long allocationMb = Math.Min(targetMb, maximumSafeMb);
        allocationMb = allocationMb / MemoryStepMb * MemoryStepMb;
        if (allocationMb < AutomaticMinimumMb)
        {
            return Result<MemoryAllocation>.Failure(Problem("MEMORY_INSUFFICIENT", request, totalPhysicalMb, availableMb));
        }

        int allocation = checked((int)allocationMb);
        return Result<MemoryAllocation>.Success(new MemoryAllocation(allocation, allocation, MemoryMode.Automatic));
    }

    private static Problem Problem(
        string code,
        MemoryRequest request,
        long totalPhysicalMb,
        long availableMb) => new(
            code,
            ProblemStage.JavaResolution,
            "problem.memory.resolve_failed",
            code == "MEMORY_INSUFFICIENT",
            Guid.NewGuid().ToString("N"),
            ["action.java.review_memory"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = request.Mode.ToString().ToLowerInvariant(),
                ["totalPhysicalMb"] = totalPhysicalMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["availableMb"] = availableMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
}
