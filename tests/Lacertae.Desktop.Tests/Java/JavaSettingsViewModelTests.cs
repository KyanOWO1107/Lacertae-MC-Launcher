using Lacertae.Application.Java;
using Lacertae.Desktop.ViewModels.Java;
using Lacertae.Domain.Java;

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
}
