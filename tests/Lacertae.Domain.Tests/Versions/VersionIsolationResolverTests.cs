using Lacertae.Domain.Versions;

namespace Lacertae.Domain.Tests.Versions;

public sealed class VersionIsolationResolverTests
{
    public static TheoryData<VersionIsolationPolicy, bool, bool, bool> GlobalCases => new()
    {
        { VersionIsolationPolicy.Disabled, false, true, false },
        { VersionIsolationPolicy.Disabled, true, true, false },
        { VersionIsolationPolicy.Disabled, false, false, false },
        { VersionIsolationPolicy.Disabled, true, false, false },
        { VersionIsolationPolicy.ModLoaderOnly, false, true, false },
        { VersionIsolationPolicy.ModLoaderOnly, true, true, true },
        { VersionIsolationPolicy.ModLoaderOnly, false, false, false },
        { VersionIsolationPolicy.ModLoaderOnly, true, false, true },
        { VersionIsolationPolicy.NonReleaseOnly, false, true, false },
        { VersionIsolationPolicy.NonReleaseOnly, true, true, false },
        { VersionIsolationPolicy.NonReleaseOnly, false, false, true },
        { VersionIsolationPolicy.NonReleaseOnly, true, false, true },
        { VersionIsolationPolicy.ModLoaderOrNonRelease, false, true, false },
        { VersionIsolationPolicy.ModLoaderOrNonRelease, true, true, true },
        { VersionIsolationPolicy.ModLoaderOrNonRelease, false, false, true },
        { VersionIsolationPolicy.ModLoaderOrNonRelease, true, false, true },
        { VersionIsolationPolicy.All, false, true, true },
        { VersionIsolationPolicy.All, true, true, true },
        { VersionIsolationPolicy.All, false, false, true },
        { VersionIsolationPolicy.All, true, false, true },
    };

    [Theory]
    [MemberData(nameof(GlobalCases))]
    public void ResolveAppliesGlobalPolicy(
        VersionIsolationPolicy policy,
        bool hasModLoader,
        bool isRelease,
        bool expectedIsolated)
    {
        IsolationDecision decision = VersionIsolationResolver.Resolve(
            policy,
            new VersionCharacteristics(hasModLoader, isRelease ? "release" : "snapshot"));

        Assert.Equal(expectedIsolated, decision.IsIsolated);
        Assert.False(decision.RequiresUserNotice);
        Assert.False(string.IsNullOrWhiteSpace(decision.ReasonKey));
    }

    [Fact]
    public void ResolveForceIsolatedOverrideWinsOverGlobalPolicy()
    {
        IsolationDecision decision = VersionIsolationResolver.Resolve(
            VersionIsolationPolicy.Disabled,
            new VersionCharacteristics(false, "release"),
            IsolationOverride.ForceIsolated);

        Assert.True(decision.IsIsolated);
        Assert.False(decision.RequiresUserNotice);
        Assert.Equal("isolation.override.force_isolated", decision.ReasonKey);
    }

    [Fact]
    public void ResolveForceSharedOverrideWinsOverGlobalPolicy()
    {
        IsolationDecision decision = VersionIsolationResolver.Resolve(
            VersionIsolationPolicy.All,
            new VersionCharacteristics(true, "snapshot"),
            IsolationOverride.ForceShared);

        Assert.False(decision.IsIsolated);
        Assert.False(decision.RequiresUserNotice);
        Assert.Equal("isolation.override.force_shared", decision.ReasonKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("custom")]
    public void ResolveTreatsUnknownVersionTypeAsNonReleaseAndRequiresNotice(string? versionType)
    {
        IsolationDecision decision = VersionIsolationResolver.Resolve(
            VersionIsolationPolicy.NonReleaseOnly,
            new VersionCharacteristics(false, versionType));

        Assert.True(decision.IsIsolated);
        Assert.True(decision.RequiresUserNotice);
        Assert.Equal("isolation.unknown_version_type", decision.ReasonKey);
    }

    [Fact]
    public void ResolveMatchesReleaseTypeCaseInsensitively()
    {
        IsolationDecision decision = VersionIsolationResolver.Resolve(
            VersionIsolationPolicy.NonReleaseOnly,
            new VersionCharacteristics(false, "RELEASE"));

        Assert.False(decision.IsIsolated);
        Assert.False(decision.RequiresUserNotice);
    }
}
