using Lacertae.Infrastructure.Diagnostics;

namespace Lacertae.Infrastructure.Tests.Diagnostics;

public sealed class LoggingBootstrapTests
{
    [Fact]
    public void CreateFileLoggerWritesSanitizedRenderedMessages()
    {
        string logDirectory = Path.Combine(Path.GetTempPath(), "lacertae-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);

        try
        {
            using (var logger = LoggingBootstrap.CreateFileLogger(logDirectory, [@"C:\Users\Player"]))
            {
                logger.Information("Authorization: Bearer {Token}", "ey.secret.token");
                logger.Information("refresh_token={RefreshToken}", "abc123");
                logger.Information(@"failed {Path}", @"C:\Users\Player\AppData\x");
                logger.Information("https://auth.example/cb?code={Code}&state={State}", "abc", "xyz");
            }

            string[] logFiles = Directory.GetFiles(logDirectory, "lacertae-*.log");
            string log = File.ReadAllText(Assert.Single(logFiles));

            Assert.DoesNotContain("ey.secret.token", log, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", log, StringComparison.Ordinal);
            Assert.DoesNotContain("Player", log, StringComparison.Ordinal);
            Assert.DoesNotContain("code=abc", log, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", log, StringComparison.Ordinal);
            Assert.Contains("%USERPROFILE%", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }
}
