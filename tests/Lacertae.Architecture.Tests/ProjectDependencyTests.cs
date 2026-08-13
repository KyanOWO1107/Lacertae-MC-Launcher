using System.Reflection;

namespace Lacertae.Architecture.Tests;

public sealed class ProjectDependencyTests
{
    [Fact]
    public void DomainHasNoForbiddenDependencies()
    {
        AssertNoReferences(
            typeof(Lacertae.Domain.AssemblyMarker).Assembly,
            "Avalonia", "CmlLib", "Microsoft.Data.Sqlite", "Serilog", "Lacertae.Application",
            "Lacertae.Infrastructure", "Lacertae.Platform.Windows", "Lacertae.Desktop");
    }

    [Fact]
    public void ApplicationDependsOnDomainOnlyAmongProductionProjects()
    {
        AssertNoReferences(
            typeof(Lacertae.Application.AssemblyMarker).Assembly,
            "Avalonia", "CmlLib", "Microsoft.Data.Sqlite", "Serilog",
            "Lacertae.Infrastructure", "Lacertae.Platform.Windows", "Lacertae.Desktop");
    }

    [Fact]
    public void WindowsPlatformDoesNotDependOnInfrastructureOrDesktop()
    {
        AssertNoReferences(
            typeof(Lacertae.Platform.Windows.AssemblyMarker).Assembly,
            "Avalonia", "CmlLib", "Microsoft.Data.Sqlite", "Serilog",
            "Lacertae.Infrastructure", "Lacertae.Desktop");
    }

    private static void AssertNoReferences(Assembly assembly, params string[] forbiddenPrefixes)
    {
        string[] references = assembly.GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .ToArray();

        foreach (string prefix in forbiddenPrefixes)
        {
            Assert.DoesNotContain(references, reference => reference.StartsWith(prefix, StringComparison.Ordinal));
        }
    }
}
