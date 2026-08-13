using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Domain.Accounts;

public sealed partial class OfflineAccountFactory
{
#pragma warning disable CA1822, CA5351
    public Result<Account> Create(string playerName, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || !NameRegex().IsMatch(playerName))
        {
            return Result<Account>.Failure(new Problem(
                "AUTH_OFFLINE_NAME_INVALID",
                ProblemStage.Authentication,
                "problem.auth.offline_name_invalid",
                false,
                string.IsNullOrWhiteSpace(correlationId) ? "unknown" : correlationId,
                ["action.auth.choose_valid_name"]));
        }

        byte[] digest = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        digest[6] = (byte)((digest[6] & 0x0F) | 0x30);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        string hex = Convert.ToHexString(digest).ToLowerInvariant();
        string profileUuid = string.Create(36, hex, static (buffer, value) =>
        {
            value.AsSpan(0, 8).CopyTo(buffer);
            buffer[8] = '-';
            value.AsSpan(8, 4).CopyTo(buffer[9..]);
            buffer[13] = '-';
            value.AsSpan(12, 4).CopyTo(buffer[14..]);
            buffer[18] = '-';
            value.AsSpan(16, 4).CopyTo(buffer[19..]);
            buffer[23] = '-';
            value.AsSpan(20).CopyTo(buffer[24..]);
        });

        return Result<Account>.Success(new Account(
            Guid.NewGuid().ToString("N"),
            new AccountIdentity(AccountIdentity.OfflineProviderId, profileUuid),
            AccountType.Offline,
            playerName,
            null,
            null,
            AccountStatus.Active,
            null));
    }

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
#pragma warning restore CA1822, CA5351
}
