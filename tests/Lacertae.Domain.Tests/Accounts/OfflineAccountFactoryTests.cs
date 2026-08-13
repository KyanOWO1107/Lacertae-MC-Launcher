using Lacertae.Domain.Accounts;

namespace Lacertae.Domain.Tests.Accounts;

public sealed class OfflineAccountFactoryTests
{
    [Theory]
    [InlineData("ab")]
    [InlineData("seventeen_chars_1")]
    [InlineData("name-with-dash")]
    [InlineData("名字")]
    public void CreateRejectsInvalidJavaEditionNames(string name)
    {
        var result = new OfflineAccountFactory().Create(name, "corr-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_OFFLINE_NAME_INVALID", result.Problem?.Code);
    }

    [Fact]
    public void CreateMatchesTheJavaNameUuidAlgorithm()
    {
        var result = new OfflineAccountFactory().Create("Steve", "corr-1");

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Equal("5627dd98-e6be-3c21-b8a8-e92344183641", result.Value.Identity.ProfileUuid);
        Assert.Equal(AccountIdentity.OfflineProviderId, result.Value.Identity.ProviderId);
    }

    [Fact]
    public void CreateIsStableForTheSameExactName()
    {
        Account first = new OfflineAccountFactory().Create("Player_1", "a").Value;
        Account second = new OfflineAccountFactory().Create("Player_1", "b").Value;

        Assert.Equal(first.Identity, second.Identity);
    }
}
