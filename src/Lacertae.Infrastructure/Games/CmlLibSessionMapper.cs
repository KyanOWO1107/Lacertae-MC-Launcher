using CmlLib.Core.Auth;
using Lacertae.Domain.Accounts;

namespace Lacertae.Infrastructure.Games;

public static class CmlLibSessionMapper
{
    public static MSession Map(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        MSession mapped = new(session.PlayerName, session.AccessToken.Reveal(), session.ProfileUuid)
        {
            UserType = session.UserType,
            Xuid = session.Xuid,
        };
        return mapped;
    }

    public static MSession MapSession(AuthSession session) => Map(session);
}
