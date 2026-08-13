namespace Lacertae.Domain.Versions;

public static class VersionIsolationResolver
{
    public static IsolationDecision Resolve(
        VersionIsolationPolicy policy,
        VersionCharacteristics characteristics,
        IsolationOverride isolationOverride = IsolationOverride.Inherit)
    {
        ArgumentNullException.ThrowIfNull(characteristics);

        bool isRelease = string.Equals(characteristics.VersionType, "release", StringComparison.OrdinalIgnoreCase);
        bool isKnownType = IsKnownVersionType(characteristics.VersionType);
        bool requiresUserNotice = !isKnownType;

        bool isIsolated = isolationOverride switch
        {
            IsolationOverride.Inherit => ResolvePolicy(policy, characteristics.HasModLoader, isRelease),
            IsolationOverride.ForceIsolated => true,
            IsolationOverride.ForceShared => false,
            _ => throw new ArgumentOutOfRangeException(nameof(isolationOverride), isolationOverride, "Unknown isolation override."),
        };

        string reasonKey = isolationOverride switch
        {
            IsolationOverride.ForceIsolated => "isolation.override.force_isolated",
            IsolationOverride.ForceShared => "isolation.override.force_shared",
            IsolationOverride.Inherit when requiresUserNotice => "isolation.unknown_version_type",
            IsolationOverride.Inherit => GetPolicyReasonKey(policy),
            _ => throw new ArgumentOutOfRangeException(nameof(isolationOverride), isolationOverride, "Unknown isolation override."),
        };

        return new IsolationDecision(isIsolated, requiresUserNotice, reasonKey);
    }

    private static bool ResolvePolicy(
        VersionIsolationPolicy policy,
        bool hasModLoader,
        bool isRelease) => policy switch
        {
            VersionIsolationPolicy.Disabled => false,
            VersionIsolationPolicy.ModLoaderOnly => hasModLoader,
            VersionIsolationPolicy.NonReleaseOnly => !isRelease,
            VersionIsolationPolicy.ModLoaderOrNonRelease => hasModLoader || !isRelease,
            VersionIsolationPolicy.All => true,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown version isolation policy."),
        };

    private static string GetPolicyReasonKey(VersionIsolationPolicy policy) => policy switch
    {
        VersionIsolationPolicy.Disabled => "isolation.policy.disabled",
        VersionIsolationPolicy.ModLoaderOnly => "isolation.policy.mod_loader_only",
        VersionIsolationPolicy.NonReleaseOnly => "isolation.policy.non_release_only",
        VersionIsolationPolicy.ModLoaderOrNonRelease => "isolation.policy.mod_loader_or_non_release",
        VersionIsolationPolicy.All => "isolation.policy.all",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown version isolation policy."),
    };

    private static bool IsKnownVersionType(string? versionType) =>
        string.Equals(versionType, "release", StringComparison.OrdinalIgnoreCase) ||
        versionType is "snapshot" or "old_beta" or "old_alpha";
}
