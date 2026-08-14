using Lacertae.Domain.Java;

namespace Lacertae.Domain.Tests.Java;

public sealed class MemoryResolverTests
{
    [Fact]
    public void FixedModePreservesValidBounds()
    {
        var result = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Fixed, 1024, 4096, false, 0),
            totalPhysicalMb: 16_384,
            availableMb: 8_192);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(new MemoryAllocation(1024, 4096, MemoryMode.Fixed), result.Value);
    }

    [Fact]
    public void FixedModeRejectsMinimumBelow512MiB()
    {
        var result = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Fixed, 511, 4096, false, 0),
            totalPhysicalMb: 16_384,
            availableMb: 8_192);

        Assert.False(result.IsSuccess);
        Assert.Equal("MEMORY_INVALID", result.Problem?.Code);
    }

    [Fact]
    public void FixedModeRejectsMinimumAboveMaximum()
    {
        var result = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Fixed, 4097, 4096, false, 0),
            totalPhysicalMb: 16_384,
            availableMb: 8_192);

        Assert.False(result.IsSuccess);
        Assert.Equal("MEMORY_INVALID", result.Problem?.Code);
    }

    [Fact]
    public void FixedModeRejectsMaximumAboveAvailableMemoryAfterReserve()
    {
        var result = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Fixed, 1024, 7169, false, 0),
            totalPhysicalMb: 16_384,
            availableMb: 8192);

        Assert.False(result.IsSuccess);
        Assert.Equal("MEMORY_INSUFFICIENT", result.Problem?.Code);
    }

    [Fact]
    public void AutomaticModeUsesVanillaBaseTarget()
    {
        var result = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Automatic, null, null, false, 0),
            totalPhysicalMb: 16_384,
            availableMb: 12_288);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(new MemoryAllocation(2048, 2048, MemoryMode.Automatic), result.Value);
    }

    [Fact]
    public void AutomaticModeAddsLoaderAndLargeModPackBudgets()
    {
        var result = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Automatic, null, null, true, 101),
            totalPhysicalMb: 16_384,
            availableMb: 12_288);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(new MemoryAllocation(4096, 4096, MemoryMode.Automatic), result.Value);
    }

    [Fact]
    public void AutomaticModeDoesNotAddLargeModPackBudgetAtExactly100Mods()
    {
        var result = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Automatic, null, null, true, 100),
            totalPhysicalMb: 16_384,
            availableMb: 12_288);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(new MemoryAllocation(3072, 3072, MemoryMode.Automatic), result.Value);
    }

    [Fact]
    public void AutomaticModeCapsAtHalfPhysicalMemoryAndAvailableMemory()
    {
        var halfPhysicalResult = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Automatic, null, null, true, 101),
            totalPhysicalMb: 4096,
            availableMb: 4096);
        var availableResult = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Automatic, null, null, true, 101),
            totalPhysicalMb: 16_384,
            availableMb: 4096);

        Assert.True(halfPhysicalResult.IsSuccess, halfPhysicalResult.Problem?.Code);
        Assert.Equal(2048, halfPhysicalResult.Value.MaximumMb);
        Assert.True(availableResult.IsSuccess, availableResult.Problem?.Code);
        Assert.Equal(3072, availableResult.Value.MaximumMb);
    }

    [Fact]
    public void AutomaticModeFloorsAt1024AndRoundsDownTo256()
    {
        var result = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Automatic, null, null, false, 0),
            totalPhysicalMb: 16_384,
            availableMb: 2100);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal(new MemoryAllocation(1024, 1024, MemoryMode.Automatic), result.Value);
    }

    [Fact]
    public void AutomaticModeReportsInsufficientWhen1024MiBFloorCannotBeMet()
    {
        var result = MemoryResolver.Resolve(
            new MemoryRequest(MemoryMode.Automatic, null, null, false, 0),
            totalPhysicalMb: 2047,
            availableMb: 2047);

        Assert.False(result.IsSuccess);
        Assert.Equal("MEMORY_INSUFFICIENT", result.Problem?.Code);
    }
}
