using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lacertae.Application.Storage;

namespace Lacertae.Updater;

public enum UpdateJournalOperationKind
{
    Backup,
    DeleteObsolete,
    InstallNew,
}

public sealed record UpdateJournalEntry(
    UpdateJournalOperationKind Kind,
    string RelativePath,
    string? SourcePath,
    string? DestinationPath,
    string? OldSha256,
    string? NewSha256,
    bool Applied);

public sealed record UpdateApplyJournalDocument(
    int SchemaVersion,
    string State,
    IReadOnlyList<UpdateJournalEntry> Entries);

/// <summary>
/// Durable journal for one update attempt. A record is flushed before and
/// after each filesystem mutation so a killed updater can be diagnosed and a
/// later invocation can safely choose rollback rather than guessing.
/// </summary>
public sealed class UpdateApplyJournal
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string path;
    private readonly List<UpdateJournalEntry> entries = [];
    private string state = "prepared";

    public UpdateApplyJournal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Journal path must be absolute.", nameof(path));
        }

        this.path = System.IO.Path.GetFullPath(path);
    }

    public string Path => path;

    public IReadOnlyList<UpdateJournalEntry> Entries => entries;

    public string State => state;

    public void SetState(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32 ||
            !value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException("Journal state is invalid.", nameof(value));
        }

        state = value;
        Persist();
    }

    public int AddPending(UpdateJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Applied)
        {
            throw new ArgumentException("A pending journal entry must not be applied.", nameof(entry));
        }

        entries.Add(entry);
        Persist();
        return entries.Count - 1;
    }

    public void MarkApplied(int index)
    {
        if ((uint)index >= (uint)entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        entries[index] = entries[index] with { Applied = true };
        Persist();
    }

    public void Persist()
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("Journal has no parent directory.");
        }

        SecureFileSystem.EnsureDirectory(directory);
        UpdateApplyJournalDocument document = new(CurrentSchemaVersion, state, entries.ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        SecureFileSystem.WriteAtomically(path, bytes, directory);
    }

    public static UpdateApplyJournal Load(string path)
    {
        UpdateApplyJournal journal = new(path);
        if (!File.Exists(journal.path))
        {
            return journal;
        }

        string parent = System.IO.Path.GetDirectoryName(journal.path)!;
        if (!SecureFileSystem.IsSafeFile(journal.path, parent))
        {
            throw new InvalidDataException("Update journal path is not a regular file.");
        }

        using Stream stream = SecureFileSystem.OpenRead(journal.path, parent);
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        UpdateApplyJournalDocument? document = JsonSerializer.Deserialize<UpdateApplyJournalDocument>(bytes, JsonOptions);
        if (document is null || document.SchemaVersion != CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(document.State))
        {
            throw new InvalidDataException("Update journal is invalid.");
        }

        journal.state = document.State;
        journal.entries.AddRange(document.Entries);
        return journal;
    }

    public static string Sha256(string path)
    {
        using Stream stream = SecureFileSystem.OpenRead(path, System.IO.Path.GetDirectoryName(path)!);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
