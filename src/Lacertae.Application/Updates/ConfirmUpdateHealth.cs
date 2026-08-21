using System.Text.Json;
using Lacertae.Application.Storage;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Application.Updates;

public sealed record ConfirmUpdateHealthRequest(
    string UpdatesPath,
    string HealthNonce,
    int ProcessId,
    bool StartupCompleted,
    bool MainWindowRendered,
    string CorrelationId);

/// <summary>
/// Writes the nonce-bound health marker only after startup and the first main
/// window render have completed. The updater accepts no other success signal.
/// </summary>
public sealed class ConfirmUpdateHealth
{
    private const int MaximumNonceLength = 128;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly TimeProvider timeProvider;

    public ConfirmUpdateHealth(TimeProvider? timeProvider = null) =>
        this.timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<Result<Unit>> ExecuteAsync(
        ConfirmUpdateHealthRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.StartupCompleted || !request.MainWindowRendered)
        {
            return Failure("UPDATE_HEALTH_NOT_READY", request.CorrelationId);
        }

        if (request.ProcessId <= 0 ||
            string.IsNullOrWhiteSpace(request.UpdatesPath) ||
            !Path.IsPathFullyQualified(request.UpdatesPath) ||
            !IsNonce(request.HealthNonce) ||
            string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return Failure("UPDATE_HEALTH_INVALID", request.CorrelationId);
        }

        string updatesPath;
        try
        {
            updatesPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.UpdatesPath));
            string healthDirectory = GetHealthDirectory(updatesPath);
            SecureFileSystem.EnsureDirectory(updatesPath);
            SecureFileSystem.EnsureDirectory(healthDirectory, updatesPath);
            string healthPath = GetHealthFilePath(updatesPath, request.HealthNonce);
            UpdateHealthDocument document = new(1, request.HealthNonce, request.ProcessId, timeProvider.GetUtcNow());
            await SecureFileSystem.WriteAtomicallyAsync(
                healthPath,
                JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions),
                cancellationToken,
                updatesPath);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return Failure("UPDATE_HEALTH_WRITE_FAILED", request.CorrelationId);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("UPDATE_HEALTH_WRITE_FAILED", request.CorrelationId);
        }
        catch (ArgumentException)
        {
            return Failure("UPDATE_HEALTH_INVALID", request.CorrelationId);
        }
        catch (NotSupportedException)
        {
            return Failure("UPDATE_HEALTH_INVALID", request.CorrelationId);
        }
    }

    public static string GetHealthFilePath(string updatesPath, string healthNonce)
    {
        if (string.IsNullOrWhiteSpace(updatesPath) || !Path.IsPathFullyQualified(updatesPath) || !IsNonce(healthNonce))
        {
            throw new ArgumentException("Health path inputs are invalid.");
        }

        return Path.Combine(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(updatesPath)),
            "health",
            healthNonce + ".json");
    }

    private static string GetHealthDirectory(string updatesPath) => Path.Combine(updatesPath, "health");

    private static bool IsNonce(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 16 and <= MaximumNonceLength &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static Result<Unit> Failure(string code, string correlationId) => Result<Unit>.Failure(new Problem(
        code,
        ProblemStage.Update,
        "problem.update.health_failed",
        false,
        string.IsNullOrWhiteSpace(correlationId) ? "update-health" : correlationId,
        ["action.update.retry"]));

    private sealed record UpdateHealthDocument(int SchemaVersion, string Nonce, int ProcessId, DateTimeOffset ConfirmedUtc);
}
