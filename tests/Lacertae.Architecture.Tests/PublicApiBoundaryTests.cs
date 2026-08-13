using System.Reflection;

namespace Lacertae.Architecture.Tests;

public sealed class PublicApiBoundaryTests
{
    [Fact]
    public void DomainAndApplicationPublicApiDoNotExposeThirdPartyTypes()
    {
        Assembly[] assemblies =
        [
            typeof(Lacertae.Domain.AssemblyMarker).Assembly,
            typeof(Lacertae.Application.AssemblyMarker).Assembly,
        ];

        string[] forbiddenPrefixes =
        [
            "Avalonia", "CmlLib", "Microsoft.Data.Sqlite", "Serilog", "XboxAuthNet",
            "Microsoft.Identity.Client",
        ];

        foreach (Type type in assemblies.SelectMany(static assembly => assembly.GetExportedTypes()))
        {
            IEnumerable<Type> exposed = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                .SelectMany(static method => method.GetParameters().Select(static parameter => parameter.ParameterType)
                    .Append(method.ReturnType))
                .Append(type.BaseType ?? typeof(object));

            Assert.DoesNotContain(exposed, candidate => forbiddenPrefixes.Any(prefix =>
                (candidate.FullName ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal)));
        }
    }
}
