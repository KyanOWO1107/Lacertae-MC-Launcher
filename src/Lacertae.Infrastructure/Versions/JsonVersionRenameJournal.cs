using System.Text.Json;
using Lacertae.Application.Versions;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Versions;

namespace Lacertae.Infrastructure.Versions;

public sealed class JsonVersionRenameJournal(string path) : IVersionRenameJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string path = Path.GetFullPath(
        string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Journal path cannot be blank.", nameof(path))
            : path);

    public async Task<Result<Unit>> WriteAsync(
        VersionRenameJournalEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string temporaryPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, entry, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return Result.Failure(Problem("VERSION_RENAME_JOURNAL_FAILED"));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<Result<VersionRenameJournalEntry?>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Result<VersionRenameJournalEntry?>.Success(null);
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            VersionRenameJournalEntry? entry = await JsonSerializer.DeserializeAsync<VersionRenameJournalEntry>(
                stream,
                SerializerOptions,
                cancellationToken);
            return Result<VersionRenameJournalEntry?>.Success(entry);
        }
        catch (JsonException)
        {
            return Result<VersionRenameJournalEntry?>.Failure(Problem("VERSION_RENAME_JOURNAL_CORRUPT"));
        }
        catch (IOException)
        {
            return Result<VersionRenameJournalEntry?>.Failure(Problem("VERSION_RENAME_JOURNAL_FAILED"));
        }
    }

    public Task<Result<Unit>> DeleteAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.FromResult(Result.Success());
        }
        catch (IOException)
        {
            return Task.FromResult(Result.Failure(Problem("VERSION_RENAME_JOURNAL_FAILED")));
        }
    }

    private Problem Problem(string code) => new(
        code,
        ProblemStage.Storage,
        "problem.version.rename_journal_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.version.review_rename"],
        new Dictionary<string, string> { ["file"] = Path.GetFileName(path) });
}
