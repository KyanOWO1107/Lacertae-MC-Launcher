using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Lacertae.Application.Archives;
using Lacertae.Domain.Common;
using Lacertae.Domain.Operations;
using Lacertae.Domain.Results;
using Lacertae.Infrastructure.Archives;

namespace Lacertae.Infrastructure.Tests.Archives;

public sealed class BoundedArchiveExtractorTests
{
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("\\\\server\\share\\unc.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("\\\\.\\pipe\\device")]
    [InlineData("folder:stream.txt")]
    [InlineData("CON.txt")]
    public async Task RejectsUnsafeEntryNames(string entryName)
    {
        using ArchiveTestRoot root = new();
        string outside = Path.Combine(root.Path, "escape.txt");
        await File.WriteAllTextAsync(outside, "sentinel", TestContext.Current.CancellationToken);
        string archive = root.WriteArchive("unsafe.zip", CreateZip((entryName, "payload")));

        Result<Unit> result = await CreateExtractor().ExtractAsync(
            Request(archive, root.Destination),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("ARCHIVE_ENTRY_INVALID", result.Problem?.Code);
        Assert.Equal("sentinel", await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(root.Path, "rooted.txt")));
    }

    [Fact]
    public async Task RejectsDuplicateNormalizedPathsAndCaseInsensitiveCollisions()
    {
        using ArchiveTestRoot root = new();
        string duplicateArchive = root.WriteArchive("duplicate.zip", CreateZip(
            ("file.txt", "one"),
            ("file.txt", "two")));

        Result<Unit> duplicate = await CreateExtractor().ExtractAsync(
            Request(duplicateArchive, root.Destination),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(duplicate.IsSuccess);
        Assert.Equal("ARCHIVE_ENTRY_CONFLICT", duplicate.Problem?.Code);

        string collisionArchive = root.WriteArchive("collision.zip", CreateZip(
            ("Folder/File.txt", "one"),
            ("folder/file.TXT", "two")));
        Result<Unit> collision = await CreateExtractor().ExtractAsync(
            Request(collisionArchive, root.Destination),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(collision.IsSuccess);
        Assert.Equal("ARCHIVE_ENTRY_CONFLICT", collision.Problem?.Code);
    }

    [Fact]
    public async Task RejectsWindowsReservedSegmentsAndAlternateDataStreams()
    {
        using ArchiveTestRoot root = new();
        string archive = root.WriteArchive("reserved.tar", CreateTar(
            (TarEntryType.RegularFile, "safe/aux", "payload")));

        Result<Unit> result = await CreateExtractor().ExtractAsync(
            Request(archive, root.Destination),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("ARCHIVE_ENTRY_INVALID", result.Problem?.Code);
    }

    [Fact]
    public async Task RejectsSymlinkAndHardlinkEntriesWhenLinksAreDisabled()
    {
        using ArchiveTestRoot root = new();
        string archive = root.WriteArchive("links.tar", CreateTar(
            (TarEntryType.SymbolicLink, "link", "target.txt"),
            (TarEntryType.HardLink, "hard", "target.txt")));

        Result<Unit> result = await CreateExtractor().ExtractAsync(
            Request(archive, root.Destination),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("ARCHIVE_LINK_NOT_ALLOWED", result.Problem?.Code);
        Assert.False(File.Exists(Path.Combine(root.Destination, "link")));
        Assert.False(File.Exists(Path.Combine(root.Destination, "hard")));
    }

    [Fact]
    public async Task RejectsEntryCountAndExpandedByteLimitsBeforeWriting()
    {
        using ArchiveTestRoot root = new();
        string archive = root.WriteArchive("many.zip", CreateZip(
            ("one.txt", "1"),
            ("two.txt", "2")));

        Result<Unit> count = await CreateExtractor().ExtractAsync(
            Request(archive, root.Destination, maximumEntries: 1),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(count.IsSuccess);
        Assert.Equal("ARCHIVE_LIMIT_EXCEEDED", count.Problem?.Code);
        AssertNoExtractedFiles(root.Destination);

        string largeArchive = root.WriteArchive("large.zip", CreateZip(("large.txt", "12345")));
        Result<Unit> bytes = await CreateExtractor().ExtractAsync(
            Request(largeArchive, root.Destination, maximumExpandedBytes: 4),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(bytes.IsSuccess);
        Assert.Equal("ARCHIVE_LIMIT_EXCEEDED", bytes.Problem?.Code);
        AssertNoExtractedFiles(root.Destination);
    }

    [Fact]
    public async Task RejectsExcessiveCompressionExpansionRatio()
    {
        using ArchiveTestRoot root = new();
        string archive = root.WriteArchive(
            "bomb.zip",
            CreateZip(new[] { ("repeated.txt", new string('A', 100_000)) }, CompressionLevel.SmallestSize));

        Result<Unit> result = await CreateExtractor().ExtractAsync(
            Request(archive, root.Destination, maximumExpansionRatio: 2),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("ARCHIVE_LIMIT_EXCEEDED", result.Problem?.Code);
        AssertNoExtractedFiles(root.Destination);
    }

    [Fact]
    public async Task RejectsReparsePointParentBeforeCreatingFiles()
    {
        using ArchiveTestRoot root = new();
        string target = Path.Combine(root.Path, "real");
        Directory.CreateDirectory(target);
        string link = Path.Combine(root.Destination, "linked");
        Directory.CreateDirectory(root.Destination);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        string archive = root.WriteArchive("reparse.zip", CreateZip(("linked/file.txt", "payload")));
        Result<Unit> result = await CreateExtractor().ExtractAsync(
            Request(archive, root.Destination),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("ARCHIVE_REPARSE_POINT", result.Problem?.Code);
        Assert.False(File.Exists(Path.Combine(target, "file.txt")));
    }

    [Fact]
    public async Task ExtractsZipAndTarWithinTheDestination()
    {
        using ArchiveTestRoot root = new();
        string zip = root.WriteArchive("content.zip", CreateZip(
            ("nested/file.txt", "zip-content"),
            ("empty/", string.Empty)));
        Result<Unit> zipResult = await CreateExtractor().ExtractAsync(
            Request(zip, Path.Combine(root.Path, "zip-output")),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(zipResult.IsSuccess, zipResult.Problem?.Code);
        Assert.Equal("zip-content", await File.ReadAllTextAsync(
            Path.Combine(root.Path, "zip-output", "nested", "file.txt"),
            TestContext.Current.CancellationToken));

        string tar = root.WriteArchive("content.tar", CreateTar(
            (TarEntryType.Directory, "nested", string.Empty),
            (TarEntryType.RegularFile, "nested/file.txt", "tar-content")));
        Result<Unit> tarResult = await CreateExtractor().ExtractAsync(
            Request(tar, Path.Combine(root.Path, "tar-output")),
            new Progress<OperationProgress>(),
            TestContext.Current.CancellationToken);

        Assert.True(tarResult.IsSuccess, tarResult.Problem?.Code);
        Assert.Equal("tar-content", await File.ReadAllTextAsync(
            Path.Combine(root.Path, "tar-output", "nested", "file.txt"),
            TestContext.Current.CancellationToken));
    }

    private static BoundedArchiveExtractor CreateExtractor() => new();

    private static ArchiveExtractionRequest Request(
        string archive,
        string destination,
        int maximumEntries = 200_000,
        long maximumExpandedBytes = 1024 * 1024,
        int maximumExpansionRatio = 100) =>
        new(archive, destination, maximumEntries, maximumExpandedBytes, maximumExpansionRatio, AllowLinks: false);

    private static byte[] CreateZip(
        params (string Name, string Content)[] entries) =>
        CreateZip(entries, CompressionLevel.NoCompression);

    private static byte[] CreateZip(
        (string Name, string Content)[] entries,
        CompressionLevel compressionLevel)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, compressionLevel);
                using Stream output = entry.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                output.Write(bytes);
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateTar(params (TarEntryType Type, string Name, string Content)[] entries)
    {
        using MemoryStream stream = new();
        using (TarWriter writer = new(stream, leaveOpen: true))
        {
            foreach ((TarEntryType type, string name, string content) in entries)
            {
                PaxTarEntry entry = new(type, name);
                if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    entry.LinkName = content;
                }
                else if (type == TarEntryType.RegularFile)
                {
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                }

                writer.WriteEntry(entry);
            }
        }

        return stream.ToArray();
    }

    private static void AssertNoExtractedFiles(string destination)
    {
        Assert.True(!Directory.Exists(destination) ||
            !Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Any());
    }

    private sealed class ArchiveTestRoot : IDisposable
    {
        public ArchiveTestRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lacertae-archive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Destination = System.IO.Path.Combine(Path, "output");
        }

        public string Path { get; }
        public string Destination { get; }

        public string WriteArchive(string name, byte[] content)
        {
            string archive = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(archive, content);
            return archive;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
