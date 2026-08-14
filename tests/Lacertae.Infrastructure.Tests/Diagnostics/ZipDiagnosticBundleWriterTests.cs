using System.IO.Compression;
using Lacertae.Application.Diagnostics;
using Lacertae.Domain.Diagnostics;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Diagnostics;

namespace Lacertae.Infrastructure.Tests.Diagnostics;

public sealed class ZipDiagnosticBundleWriterTests
{
    [Fact]
    public async Task WriteAsyncRequiresConfirmationAndWritesOnlyIncludedDeterministicEntries()
    {
        string root = CreateTemporaryDirectory();
        DiagnosticBundleRequest request = new("1.0.0", "windows-x64")
        {
            LauncherLogContent = "safe log",
            SelectedGameLogContent = "selected game log",
            StagingDirectory = Path.Combine(root, "staging"),
        };
        Result<PreparedDiagnosticBundle> prepared = await new BuildDiagnosticBundle().PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        Assert.True(prepared.IsSuccess, prepared.Problem?.Code);

        DiagnosticBundleManifest manifest = prepared.Value.Manifest with
        {
            Entries = prepared.Value.Manifest.Entries
                .Select(entry => entry.LogicalName == "logs/game-selected.log" ? entry with { IsIncluded = false } : entry)
                .ToArray(),
        };
        manifest = StabilizeManifestSize(manifest);
        string output = Path.Combine(root, "diagnostics.zip");
        ZipDiagnosticBundleWriter writer = new(request.StagingDirectory!);

        Result<string> notConfirmed = await writer.WriteAsync(
            prepared.Value.Handle,
            manifest,
            output,
            confirmed: false,
            TestContext.Current.CancellationToken);
        Assert.False(notConfirmed.IsSuccess);
        Assert.Equal("DIAGNOSTIC_BUNDLE_CONFIRMATION_REQUIRED", notConfirmed.Problem?.Code);

        Result<string> result = await writer.WriteAsync(
            prepared.Value.Handle,
            manifest,
            output,
            confirmed: true,
            TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.True(File.Exists(output));

        using ZipArchive archive = ZipFile.OpenRead(output);
        string[] names = archive.Entries.Select(static entry => entry.FullName).ToArray();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Contains("manifest.json", names, StringComparer.Ordinal);
        Assert.Contains("launcher-version.json", names, StringComparer.Ordinal);
        Assert.DoesNotContain("logs/game-selected.log", names, StringComparer.Ordinal);
        Assert.All(archive.Entries, entry => Assert.Equal(1980, entry.LastWriteTime.Year));
    }

    [Fact]
    public async Task WriteAsyncRejectsManifestWithIncorrectSelfDeclaredSize()
    {
        string root = CreateTemporaryDirectory();
        DiagnosticBundleRequest request = new("1.0.0", "windows-x64")
        {
            LauncherLogContent = "safe log",
            StagingDirectory = Path.Combine(root, "staging"),
        };
        Result<PreparedDiagnosticBundle> prepared = await new BuildDiagnosticBundle().PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        Assert.True(prepared.IsSuccess, prepared.Problem?.Code);

        DiagnosticBundleManifest manifest = prepared.Value.Manifest with
        {
            Entries = prepared.Value.Manifest.Entries
                .Select(entry => entry.LogicalName == "manifest.json" ? entry with { Size = entry.Size + 1 } : entry)
                .ToArray(),
        };
        Result<string> result = await new ZipDiagnosticBundleWriter(request.StagingDirectory!).WriteAsync(
            prepared.Value.Handle,
            manifest,
            Path.Combine(root, "diagnostics.zip"),
            confirmed: true,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DIAGNOSTIC_BUNDLE_INVALID", result.Problem?.Code);
    }

    [Fact]
    public async Task WriteAsyncRejectsReparsePointAncestorInsideStaging()
    {
        string root = CreateTemporaryDirectory();
        DiagnosticBundleRequest request = new("1.0.0", "windows-x64")
        {
            LauncherLogContent = "safe log",
            StagingDirectory = Path.Combine(root, "staging"),
        };
        Result<PreparedDiagnosticBundle> prepared = await new BuildDiagnosticBundle().PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        Assert.True(prepared.IsSuccess, prepared.Problem?.Code);

        string stagingPath = Path.Combine(request.StagingDirectory!, prepared.Value.Handle.Id);
        string realLogs = Path.Combine(root, "real-logs");
        Directory.CreateDirectory(realLogs);
        string logsPath = Path.Combine(stagingPath, "logs");
        Directory.Delete(logsPath, recursive: true);
        try
        {
            Directory.CreateSymbolicLink(logsPath, realLogs);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Result<string> result = await new ZipDiagnosticBundleWriter(request.StagingDirectory!).WriteAsync(
            prepared.Value.Handle,
            prepared.Value.Manifest,
            Path.Combine(root, "diagnostics.zip"),
            confirmed: true,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DIAGNOSTIC_BUNDLE_REPARSE_POINT", result.Problem?.Code);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lacertae-diagnostics-infra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static DiagnosticBundleManifest StabilizeManifestSize(DiagnosticBundleManifest manifest)
    {
        long size = manifest.Entries.Single(entry => entry.LogicalName == "manifest.json").Size;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            manifest = manifest with
            {
                Entries = manifest.Entries
                    .Select(entry => entry.LogicalName == "manifest.json" ? entry with { Size = size } : entry)
                    .ToArray(),
            };
            long actual = BuildDiagnosticBundle.SerializeManifest(manifest).LongLength;
            if (actual == size)
            {
                return manifest;
            }

            size = actual;
        }

        throw new InvalidOperationException("Manifest size did not converge.");
    }
}
