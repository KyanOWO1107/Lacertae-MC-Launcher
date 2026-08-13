using Lacertae.Infrastructure.Diagnostics;

namespace Lacertae.Infrastructure.Tests.Diagnostics;

public sealed class LogSanitizerTests
{
    private readonly LogSanitizer sanitizer = new([@"C:\Users\Player"]);

    [Theory]
    [InlineData("Authorization: Bearer ey.secret.token", "Authorization: Bearer [REDACTED]")]
    [InlineData("refresh_token=abc123", "refresh_token=[REDACTED]")]
    [InlineData(@"failed C:\Users\Player\AppData\x", @"failed %USERPROFILE%\AppData\x")]
    [InlineData("https://auth.example/cb?code=abc&state=xyz", "https://auth.example/cb?code=[REDACTED]&state=[REDACTED]")]
    public void SanitizeRemovesSensitiveValues(string input, string expected)
    {
        Assert.Equal(expected, sanitizer.Sanitize(input));
    }
}
