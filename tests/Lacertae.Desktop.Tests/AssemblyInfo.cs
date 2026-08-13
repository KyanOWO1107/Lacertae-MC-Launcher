global using Avalonia.Headless.XUnit;
global using Xunit;

using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Lacertae.Desktop.Tests.AppFixture))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]
