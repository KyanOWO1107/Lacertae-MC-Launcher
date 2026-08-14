using Lacertae.Application.Resources;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Tests.Resources;

public sealed class ResolveLocalResourceFoldersTests
{
    [Fact]
    public void ResolvesOnlyStandardFoldersBelowEffectiveRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "lacertae-resources-" + Guid.NewGuid().ToString("N"));
        try
        {
            Result<LocalResourceFolders> result = ResolveLocalResourceFolders.Execute(root);
            Assert.True(result.IsSuccess);
            Assert.Equal(["mods", "resourcepacks", "shaderpacks", "saves", "screenshots", "logs"], result.Value.Folders.Select(f => f.Name));
            Assert.All(result.Value.Folders, folder => Assert.StartsWith(result.Value.RootPath + Path.DirectorySeparatorChar, folder.NormalizedPath, StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(root, "mods")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RequiresExplicitConfirmationBeforeCreation()
    {
        string root = Path.Combine(Path.GetTempPath(), "lacertae-resources-" + Guid.NewGuid().ToString("N"));
        try
        {
            Result<LocalResourceFolder> result = new ResolveLocalResourceFolders().Create(root, null, "mods", confirmed: false);
            Assert.False(result.IsSuccess);
            Assert.Equal("RESOURCE_CONFIRMATION_REQUIRED", result.Problem!.Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
