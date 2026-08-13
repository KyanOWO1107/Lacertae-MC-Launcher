using Lacertae.Domain.Versions;
using Lacertae.Infrastructure.Versions;

namespace Lacertae.Infrastructure.Tests.Versions;

public sealed class JsonVersionRenameJournalTests
{
    [Fact]
    public async Task WriteReadAndDeleteRoundTripFlushesJournal()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lacertae-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "rename.json");
            JsonVersionRenameJournal journal = new(path);
            VersionRenamePlan plan = new(
                "op-1", "root-1", "source", "target",
                @"C:\Games\.minecraft\versions\source",
                @"C:\Games\.minecraft\versions\target",
                @"C:\Games\.minecraft\versions\source\source.json",
                @"C:\Games\.minecraft\versions\target\target.json",
                null, null, false);

            Assert.True((await journal.WriteAsync(new VersionRenameJournalEntry(plan, VersionRenameJournalState.DirectoryMoved), CancellationToken.None)).IsSuccess);
            Assert.True(File.Exists(path));
            var read = await journal.ReadAsync(CancellationToken.None);
            Assert.True(read.IsSuccess, read.Problem?.Code);
            Assert.Equal(VersionRenameJournalState.DirectoryMoved, read.Value?.State);
            Assert.Equal(plan, read.Value?.Plan);
            Assert.True((await journal.DeleteAsync(CancellationToken.None)).IsSuccess);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
