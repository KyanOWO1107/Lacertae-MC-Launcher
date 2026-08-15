using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lacertae.Domain.Updates;
using Lacertae.Updater;

namespace Lacertae.Updater.Tests;

public sealed class UpdateApplierTests
{
    [Fact]
    public async Task SuccessfulApplyBacksUpOnlyManifestFilesAndRemovesStaging()
    {
        using TestRoot root = new();
        string oldExecutable = "old-launcher";
        string newExecutable = "new-launcher";
        await root.SeedAsync(oldExecutable, newExecutable, TestContext.Current.CancellationToken);
        FakeProcessLauncher launcher = new(root.HealthPath, root.Nonce, validHealth: true);

        UpdateApplyResult result = await TestRoot.CreateApplier(launcher).ApplyAsync(
            root.Plan,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.FailureCode);
        Assert.False(result.RolledBack);
        Assert.Equal(newExecutable, await File.ReadAllTextAsync(root.InstallFile, TestContext.Current.CancellationToken));
        Assert.Equal("keep", await File.ReadAllTextAsync(root.UnknownFile, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(root.PortableMarker));
        Assert.True(Directory.Exists(root.UserDataDirectory));
        Assert.False(Directory.Exists(root.StagingDirectory));
        Assert.Equal(oldExecutable, await File.ReadAllTextAsync(Path.Combine(root.BackupDirectory, "app.exe"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(root.BackupDirectory, "package-manifest.json")));
        Assert.True(File.Exists(result.JournalPath));
    }

    [Fact]
    public async Task InvalidHealthRestoresByteIdenticalOldFiles()
    {
        using TestRoot root = new();
        string oldExecutable = "old-launcher";
        string newExecutable = "new-launcher";
        await root.SeedAsync(oldExecutable, newExecutable, TestContext.Current.CancellationToken);
        FakeProcessLauncher launcher = new(root.HealthPath, root.Nonce, validHealth: false);

        UpdateApplyResult result = await TestRoot.CreateApplier(launcher).ApplyAsync(
            root.Plan,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal("UPDATE_HEALTH_FAILED", result.FailureCode);
        Assert.Equal(oldExecutable, await File.ReadAllTextAsync(root.InstallFile, TestContext.Current.CancellationToken));
        Assert.Equal(root.OldManifest, await File.ReadAllTextAsync(root.InstallManifest, TestContext.Current.CancellationToken));
        Assert.Equal("keep", await File.ReadAllTextAsync(root.UnknownFile, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(root.PortableMarker));
        Assert.True(Directory.Exists(root.UserDataDirectory));
    }

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "lacertae-updater-test-" + Guid.NewGuid().ToString("N"));
            InstallDirectory = Path.Combine(Root, "install");
            StagingDirectory = Path.Combine(Root, "staging");
            BackupDirectory = Path.Combine(Root, "backup");
            UpdatesDirectory = Path.Combine(Root, "updates");
            Directory.CreateDirectory(InstallDirectory);
            Directory.CreateDirectory(StagingDirectory);
            Directory.CreateDirectory(UpdatesDirectory);
            Directory.CreateDirectory(Path.Combine(UpdatesDirectory, "health"));
            Nonce = "nonce-" + Guid.NewGuid().ToString("N");
            HealthPath = Path.Combine(UpdatesDirectory, "health", Nonce + ".json");
            ParentExecutablePath = Path.GetFullPath(Environment.ProcessPath!);
            InstallFile = Path.Combine(InstallDirectory, "app.exe");
            InstallManifest = Path.Combine(InstallDirectory, "package-manifest.json");
            UnknownFile = Path.Combine(InstallDirectory, "user.txt");
            PortableMarker = Path.Combine(InstallDirectory, "lacertae.portable");
            UserDataDirectory = Path.Combine(InstallDirectory, "LacertaeData");
        }

        public string Root { get; }
        public string InstallDirectory { get; }
        public string StagingDirectory { get; }
        public string BackupDirectory { get; }
        public string UpdatesDirectory { get; }
        public string Nonce { get; }
        public string HealthPath { get; }
        public string ParentExecutablePath { get; }
        public string InstallFile { get; }
        public string InstallManifest { get; }
        public string UnknownFile { get; }
        public string PortableMarker { get; }
        public string UserDataDirectory { get; }
        public string OldManifest { get; private set; } = string.Empty;

        public UpdateApplyPlan Plan => new(
            Environment.ProcessId,
            ParentExecutablePath,
            InstallDirectory,
            StagingDirectory,
            BackupDirectory,
            "app.exe",
            HealthPath,
            Nonce,
            TimeSpan.FromSeconds(2),
            ["app.exe"],
            ["app.exe"]);

        public async Task SeedAsync(string oldExecutable, string newExecutable, CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(InstallFile, oldExecutable, cancellationToken);
            await File.WriteAllTextAsync(UnknownFile, "keep", cancellationToken);
            await File.WriteAllTextAsync(PortableMarker, "marker", cancellationToken);
            Directory.CreateDirectory(UserDataDirectory);
            await File.WriteAllTextAsync(Path.Combine(UserDataDirectory, "settings.json"), "user", cancellationToken);
            OldManifest = ManifestJson(oldExecutable);
            await File.WriteAllTextAsync(InstallManifest, OldManifest, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(StagingDirectory, "app.exe"), newExecutable, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(StagingDirectory, "package-manifest.json"), ManifestJson(newExecutable), cancellationToken);
        }

        public static UpdateApplier CreateApplier(FakeProcessLauncher launcher) =>
            new(new FakeParentWaiter(), launcher);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string ManifestJson(string executable)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(executable);
            return "{\"schemaVersion\":1,\"files\":[{\"path\":\"app.exe\",\"size\":" + bytes.Length + ",\"sha256\":\"" + Hash(bytes) + "\"}]}";
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class FakeParentWaiter : IUpdateParentWaiter
    {
        public Task<ProcessWaitResult> WaitForExitAsync(int processId, string expectedExecutablePath, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(ProcessWaitResult.Success());
    }

    private sealed class FakeProcessLauncher(string healthPath, string nonce, bool validHealth) : IUpdateProcessLauncher
    {
        public IUpdateProcess Start(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
        {
            FakeProcess process = new();
            string writtenNonce = validHealth ? nonce : "wrong-nonce";
            File.WriteAllText(
                healthPath,
                JsonSerializer.Serialize(new { schemaVersion = 1, nonce = writtenNonce, processId = process.Id }));
            return process;
        }
    }

    private sealed class FakeProcess : IUpdateProcess
    {
        public int Id => 42;

        public bool HasExited { get; private set; }

        public void Kill() => HasExited = true;

        public void Dispose() { }
    }
}
