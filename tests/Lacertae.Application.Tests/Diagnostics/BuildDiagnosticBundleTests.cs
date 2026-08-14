using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lacertae.Application.Diagnostics;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Diagnostics;

public sealed class BuildDiagnosticBundleTests
{
    [Fact]
    public async Task PrepareAsyncIncludesOnlySelectedRedactedDiagnostics()
    {
        string root = CreateTemporaryDirectory();
        string staging = Path.Combine(root, "staging");
        string launcherLog = Path.Combine(root, "launcher.log");
        string selectedGameLog = Path.Combine(root, "selected.log");
        string unselectedGameLog = Path.Combine(root, "other.log");
        string settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(
            launcherLog,
            "Authorization: Bearer super-secret-token clientId=camel-secret user@example.test D:\\Users\\Bob\\AppData\\Local\\Lacertae C:\\Users\\Alice\\AppData\\Local\\Lacertae",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(selectedGameLog, "refresh_token=selected-secret\nhttps://auth.example.test/callback?code=oauth-code", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(unselectedGameLog, "unselected-secret", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(settings, "{\"globalJavaPath\":\"C:\\Users\\Alice\\java.exe\",\"theme\":\"dark\"}", TestContext.Current.CancellationToken);

        DiagnosticBundleRequest request = new("1.0.0", "windows-x64")
        {
            LauncherLogPath = launcherLog,
            SelectedGameLogPath = selectedGameLog,
            SettingsPath = settings,
            DataRootPath = Path.Combine(root, "LacertaeData"),
            UserProfilePath = @"C:\Users\Alice",
            StagingDirectory = staging,
            PrivatePathPrefixes = [@"C:\Users\Alice"],
        };

        Result<PreparedDiagnosticBundle> result = await new BuildDiagnosticBundle().PrepareAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.Contains(result.Value.Manifest.Entries, entry => entry.LogicalName == "launcher-version.json" && entry.IsIncluded);
        Assert.Contains(result.Value.Manifest.Entries, entry => entry.LogicalName == "logs/game-selected.log" && entry.IsIncluded);
        Assert.DoesNotContain(result.Value.Manifest.Entries, entry => entry.LogicalName.Contains("other", StringComparison.OrdinalIgnoreCase));

        string preparedRoot = Path.Combine(staging, result.Value.Handle.Id);
        string[] files = Directory.GetFiles(preparedRoot, "*", SearchOption.AllDirectories);
        string contents = string.Join("\n", files.Select(File.ReadAllText));
        Assert.DoesNotContain("super-secret-token", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("camel-secret", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-secret", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.test", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\Users\\Bob", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users\\Alice", contents, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manifest.json", files.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareAsyncRejectsReparsePointSourcesAndOversizedText()
    {
        string root = CreateTemporaryDirectory();
        string source = Path.Combine(root, "source.log");
        await File.WriteAllTextAsync(source, new string('x', 10 * 1024 * 1024 + 1), TestContext.Current.CancellationToken);

        DiagnosticBundleRequest request = new("1.0.0", "windows-x64")
        {
            LauncherLogPath = source,
            StagingDirectory = Path.Combine(root, "staging"),
        };

        Result<PreparedDiagnosticBundle> result = await new BuildDiagnosticBundle().PrepareAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DIAGNOSTIC_BUNDLE_LIMIT_EXCEEDED", result.Problem?.Code);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lacertae-diagnostics-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
