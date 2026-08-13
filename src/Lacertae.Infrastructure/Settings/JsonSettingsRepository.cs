using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lacertae.Application.Settings;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Settings;

namespace Lacertae.Infrastructure.Settings;

public sealed class JsonSettingsRepository(string path) : ISettingsRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string path = Path.GetFullPath(
        string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Settings path cannot be blank.", nameof(path)) : path);

    public async Task<Result<LauncherSettings>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Result<LauncherSettings>.Success(LauncherSettings.Default);
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            SettingsDocumentV1? document = await JsonSerializer.DeserializeAsync<SettingsDocumentV1>(
                stream,
                SerializerOptions,
                cancellationToken);
            if (document is null)
            {
                return Result<LauncherSettings>.Failure(Problem("SETTINGS_CORRUPT"));
            }

            if (document.SchemaVersion > LauncherSettings.Default.SchemaVersion)
            {
                return Result<LauncherSettings>.Failure(Problem("SETTINGS_VERSION_UNSUPPORTED"));
            }

            if (document.SchemaVersion != LauncherSettings.Default.SchemaVersion)
            {
                return Result<LauncherSettings>.Failure(Problem("SETTINGS_CORRUPT"));
            }

            return Result<LauncherSettings>.Success(document.ToDomain());
        }
        catch (JsonException)
        {
            return Result<LauncherSettings>.Failure(Problem("SETTINGS_CORRUPT"));
        }
        catch (IOException)
        {
            return Result<LauncherSettings>.Failure(Problem("SETTINGS_CORRUPT"));
        }
    }

    public async Task<Result<Unit>> SaveAsync(LauncherSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != LauncherSettings.Default.SchemaVersion)
        {
            return Result.Failure(Problem("SETTINGS_VERSION_UNSUPPORTED"));
        }

        string temporaryPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, SettingsDocumentV1.FromDomain(settings), SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                string backupPath = path + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".bak";
                File.Copy(path, backupPath, overwrite: false);
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
            return Result.Failure(Problem("SETTINGS_SAVE_FAILED"));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private Problem Problem(string code) => new(
        code,
        ProblemStage.Configuration,
        "problem.settings.invalid",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.settings.restore_backup"],
        new Dictionary<string, string> { ["file"] = Path.GetFileName(path) });
}
