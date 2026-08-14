using Lacertae.Application.Diagnostics;
using Lacertae.Desktop.ViewModels.Diagnostics;

namespace Lacertae.Desktop.Tests.Diagnostics;

public sealed class DiagnosticPreviewViewModelTests
{
    [Fact]
    public async Task PrepareAsyncExposesRedactedEntriesAndKeepsRequiredItemsIncluded()
    {
        string root = CreateTemporaryDirectory();
        DiagnosticBundleRequest request = new("1.0.0", "windows-x64")
        {
            LauncherLogContent = "safe",
            SelectedGameLogContent = "selected",
            StagingDirectory = Path.Combine(root, "staging"),
        };
        DiagnosticPreviewViewModel viewModel = new(new BuildDiagnosticBundle());

        var result = await viewModel.PrepareAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.True(viewModel.IsPreviewReady);
        DiagnosticPreviewEntryViewModel launcher = Assert.Single(
            viewModel.Entries,
            entry => entry.LogicalName == "launcher-version.json");
        launcher.IsIncluded = false;
        Assert.True(launcher.IsIncluded);
        DiagnosticPreviewEntryViewModel? selected = viewModel.Entries
            .FirstOrDefault(entry => entry.LogicalName == "logs/game-selected.log");
        Assert.NotNull(selected);
        selected!.IsIncluded = false;
        Assert.False(selected.IsIncluded);
        Assert.Contains("tokens", selected.RedactionSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveRequiresExplicitConfirmation()
    {
        string root = CreateTemporaryDirectory();
        DiagnosticBundleRequest request = new("1.0.0", "windows-x64")
        {
            LauncherLogContent = "safe",
            StagingDirectory = Path.Combine(root, "staging"),
        };
        DiagnosticPreviewViewModel viewModel = new(new BuildDiagnosticBundle());
        await viewModel.PrepareAsync(request, TestContext.Current.CancellationToken);

        string destination = Path.Combine(root, "diagnostics.zip");
        var result = await viewModel.SaveAsync(destination, confirmed: false, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("DIAGNOSTIC_BUNDLE_CONFIRMATION_REQUIRED", result.Problem?.Code);
        Assert.False(File.Exists(destination));
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lacertae-diagnostics-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
