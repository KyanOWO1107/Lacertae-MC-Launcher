using Lacertae.Application.Java;
using Lacertae.Desktop.ViewModels.Java;
using Lacertae.Domain.Common;
using Lacertae.Domain.Java;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Desktop.Tests.Java;

public sealed class JavaSettingsViewModelTests
{
    [Fact]
    public void RuntimePrimaryLabelDoesNotExposeFullPath()
    {
        const string path = @"C:\Users\Player\AppData\Local\Lacertae\runtimes\java-21\bin\javaw.exe";
        JavaDiscoveryResult discovery = new([
            new JavaInstallation("managed-21", path, 21, "21.0.7", "Temurin", JavaArchitecture.X64, JavaSource.Managed, true),
        ], []);

        JavaSettingsViewModel viewModel = new(discovery, 21, JavaArchitecture.X64);

        JavaRuntimeItem item = Assert.Single(viewModel.Runtimes);
        Assert.DoesNotContain(path, item.PrimaryLabel, StringComparison.Ordinal);
        Assert.Contains("21", item.PrimaryLabel, StringComparison.Ordinal);
        Assert.Equal(path, item.ExecutablePath);
        Assert.Equal("自动选择", viewModel.AutomaticOptionLabel);
        Assert.Equal("添加路径", viewModel.AddPathLabel);
        Assert.True(viewModel.CanAddPath);
    }

    [Fact]
    public void MissingRequiredMajorShowsManagedInstallAction()
    {
        JavaDiscoveryResult discovery = new([
            new JavaInstallation("java-17", @"C:\Java\17\bin\java.exe", 17, "17.0.12", "Vendor", JavaArchitecture.X64, JavaSource.Path, false),
        ], []);

        JavaSettingsViewModel viewModel = new(discovery, 21, JavaArchitecture.X64);

        Assert.True(viewModel.ShowInstallManagedAction);
        Assert.Contains("21", viewModel.MissingRuntimeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AvailableRequiredMajorHidesManagedInstallAction()
    {
        JavaDiscoveryResult discovery = new([
            new JavaInstallation("java-21", @"C:\Java\21\bin\java.exe", 21, "21.0.1", "Vendor", JavaArchitecture.X64, JavaSource.Path, false),
        ], []);

        JavaSettingsViewModel viewModel = new(discovery, 21, JavaArchitecture.X64);

        Assert.False(viewModel.ShowInstallManagedAction);
        Assert.Null(viewModel.MissingRuntimeMessage);
    }

    [Fact]
    public void IncompatibleSelectedRuntimeRemainsSelectedAndExposesActionableState()
    {
        JavaInstallation installation = new(
            "java-17",
            @"C:\Java\17\bin\java.exe",
            17,
            "17.0.12",
            "Vendor",
            JavaArchitecture.X64,
            JavaSource.Path,
            false);
        JavaSettingsViewModel viewModel = new(
            new JavaDiscoveryResult([installation], []),
            21,
            JavaArchitecture.X64);

        viewModel.SelectedRuntime = Assert.Single(viewModel.Runtimes);

        Assert.True(viewModel.IsManualSelection);
        Assert.True(viewModel.IsSelectedRuntimeIncompatible);
        Assert.Contains("21", viewModel.SelectionValidationMessage, StringComparison.Ordinal);
        Assert.Equal(installation.ExecutablePath, viewModel.SelectedRuntime.ExecutablePath);
    }

    [Fact]
    public async Task ManualProbeKeepsIncompatibleRuntimeSelectedUntilFixed()
    {
        JavaInstallation installation = new(
            "java-17",
            @"C:\Java\17\bin\java.exe",
            17,
            "17.0.12",
            "Vendor",
            JavaArchitecture.X64,
            JavaSource.Manual,
            false);
        JavaSettingsViewModel viewModel = new(
            new JavaDiscoveryResult([], []),
            21,
            JavaArchitecture.X64,
            new FakeProbe(installation));

        viewModel.ManualPathText = installation.ExecutablePath;
        await viewModel.UseManualPathAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsManualSelection);
        Assert.True(viewModel.IsSelectedRuntimeIncompatible);
        Assert.Equal(installation.ExecutablePath, viewModel.SelectedRuntime?.ExecutablePath);
        Assert.True(viewModel.HasSelectionValidation);
    }

    [Fact]
    public async Task CompatibleManualProbePersistsGlobalJavaPath()
    {
        const string path = @"C:\Java\21\bin\java.exe";
        List<string?> savedPaths = [];
        JavaSettingsViewModel viewModel = new(
            new JavaDiscoveryResult([], []),
            21,
            JavaArchitecture.X64,
            new FakeProbe(new JavaInstallation(
                "java-21",
                path,
                21,
                "21.0.1",
                "Vendor",
                JavaArchitecture.X64,
                JavaSource.Manual,
                false)),
            (savedPath, _) =>
            {
                savedPaths.Add(savedPath);
                return Task.FromResult(Result<Unit>.Success(Unit.Value));
            });

        viewModel.ManualPathText = path;
        await viewModel.UseManualPathAsync(TestContext.Current.CancellationToken);

        Assert.Equal([path], savedPaths);
        Assert.False(viewModel.HasSelectionValidation);
    }

    [Fact]
    public async Task SelectingAutomaticPersistsNullGlobalJavaPath()
    {
        List<string?> savedPaths = [];
        JavaSettingsViewModel viewModel = new(
            new JavaDiscoveryResult([], []),
            21,
            JavaArchitecture.X64,
            saveGlobalJavaPath: (savedPath, _) =>
            {
                savedPaths.Add(savedPath);
                return Task.FromResult(Result<Unit>.Success(Unit.Value));
            });

        await viewModel.SelectAutomaticAsync(TestContext.Current.CancellationToken);

        Assert.Equal([null], savedPaths);
        Assert.True(viewModel.IsAutomaticSelected);
    }

    [Fact]
    public async Task GlobalJavaPathSaveFailureRemainsVisibleAfterCompatibleProbe()
    {
        JavaSettingsViewModel viewModel = new(
            new JavaDiscoveryResult([], []),
            21,
            JavaArchitecture.X64,
            new FakeProbe(new JavaInstallation(
                "java-21",
                @"C:\Java\21\bin\java.exe",
                21,
                "21.0.1",
                "Vendor",
                JavaArchitecture.X64,
                JavaSource.Manual,
                false)),
            (_, _) => Task.FromResult(Result<Unit>.Failure(new Problem(
                "SETTINGS_SAVE_FAILED",
                ProblemStage.Configuration,
                "problem.settings.save_failed",
                true,
                "save-correlation",
                []))));

        viewModel.ManualPathText = @"C:\Java\21\bin\java.exe";
        await viewModel.UseManualPathAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsManualSelection);
        Assert.True(viewModel.HasSelectionValidation);
        Assert.Equal("SETTINGS_SAVE_FAILED", viewModel.SelectionValidationCode);
    }

    [Fact]
    public async Task IncompatibleManualProbeDoesNotPersistGlobalJavaPath()
    {
        List<string?> savedPaths = [];
        const string path = @"C:\Java\17\bin\java.exe";
        JavaSettingsViewModel viewModel = new(
            new JavaDiscoveryResult([], []),
            21,
            JavaArchitecture.X64,
            new FakeProbe(new JavaInstallation(
                "java-17",
                path,
                17,
                "17.0.12",
                "Vendor",
                JavaArchitecture.X64,
                JavaSource.Manual,
                false)),
            (savedPath, _) =>
            {
                savedPaths.Add(savedPath);
                return Task.FromResult(Result<Unit>.Success(Unit.Value));
            });

        viewModel.ManualPathText = path;
        await viewModel.UseManualPathAsync(TestContext.Current.CancellationToken);

        Assert.Empty(savedPaths);
        Assert.True(viewModel.IsSelectedRuntimeIncompatible);
    }

    [Fact]
    public async Task MissingManagedInstallerShowsUnavailableStateInsteadOfSuccess()
    {
        JavaSettingsViewModel viewModel = new(
            new JavaDiscoveryResult([], []),
            21,
            JavaArchitecture.X64);

        await viewModel.InstallManagedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("JAVA_MANAGED_INSTALL_UNAVAILABLE", viewModel.SelectionValidationCode);
        Assert.True(viewModel.HasSelectionValidation);
    }

    private sealed class FakeProbe(JavaInstallation installation) : IJavaProbe
    {
        public Task<Result<JavaInstallation>> ProbeAsync(
            string executablePath,
            JavaSource source,
            bool isManaged,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<JavaInstallation>.Success(installation with
            {
                ExecutablePath = executablePath,
                Source = source,
                IsManaged = isManaged,
            }));
    }
}
