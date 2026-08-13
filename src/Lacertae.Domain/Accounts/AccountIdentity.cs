namespace Lacertae.Domain.Accounts;

public sealed record AccountIdentity(string ProviderId, string ProfileUuid)
{
    public const string OfflineProviderId = "offline";
    public const string MicrosoftProviderId = "microsoft";
}
