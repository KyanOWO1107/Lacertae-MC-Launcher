using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Lacertae.Application.Accounts;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;

namespace Lacertae.Platform.Windows.Accounts;

/// <summary>
/// Stores renewable credentials encrypted with the current Windows user's DPAPI key.
/// </summary>
public sealed class DpapiSecretVault : ISecretVault
{
    private const int MaximumSecretBytes = 4 * 1024 * 1024;
    private const int MaximumStoredFileBytes = MaximumSecretBytes + 1024 * 1024;
    private const int EntropyBytes = 32;
    private const byte FormatVersion = 1;
    private const int HeaderBytes = 4 + 1 + sizeof(int);
    private const string FileSuffix = ".bin";
    private const string TemporarySuffix = ".bin.tmp";
    private static ReadOnlySpan<byte> Magic => "LCSV"u8;

    private readonly string secretsDirectory;

    public DpapiSecretVault(string secretsDirectory)
    {
        if (string.IsNullOrWhiteSpace(secretsDirectory))
        {
            throw new ArgumentException("A secrets directory is required.", nameof(secretsDirectory));
        }

        this.secretsDirectory = Path.GetFullPath(secretsDirectory);
    }

    public async Task<Result<Unit>> WriteAsync(
        string secretRef,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<Unit>? referenceValidation = ValidateReference(secretRef);
        if (referenceValidation is not null)
        {
            return referenceValidation;
        }

        if (!OperatingSystem.IsWindows())
        {
            return Failure<Unit>("SECRET_PLATFORM_UNAVAILABLE");
        }

        if (secret.IsEmpty || secret.Length > MaximumSecretBytes)
        {
            return Failure<Unit>("SECRET_PAYLOAD_INVALID");
        }

        string finalPath = GetFinalPath(secretRef);
        string temporaryPath = GetTemporaryPath(secretRef);
        byte[] plaintext = secret.ToArray();
        byte[] entropy = [];
        byte[] ciphertext = [];

        try
        {
            Directory.CreateDirectory(secretsDirectory);
            entropy = RandomNumberGenerator.GetBytes(EntropyBytes);
            try
            {
                ciphertext = ProtectedData.Protect(
                    plaintext,
                    entropy,
                    DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return Failure<Unit>("SECRET_ENCRYPT_FAILED");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await WriteTemporaryFileAsync(
                temporaryPath,
                entropy,
                ciphertext,
                cancellationToken);

            try
            {
                ApplyRestrictedFileAcl(temporaryPath);
            }
            catch (Exception exception) when (IsAclFailure(exception))
            {
                return Failure<Unit>("SECRET_ACL_FAILED");
            }

            cancellationToken.ThrowIfCancellationRequested();
            // File.Move with overwrite is a same-volume native replace on Windows.
            File.Move(temporaryPath, finalPath, overwrite: true);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlatformNotSupportedException)
        {
            return Failure<Unit>("SECRET_PLATFORM_UNAVAILABLE");
        }
        catch (CryptographicException)
        {
            return Failure<Unit>("SECRET_ENCRYPT_FAILED");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<Unit>("SECRET_STORAGE_FAILED");
        }
        catch (IOException)
        {
            return Failure<Unit>("SECRET_STORAGE_FAILED");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(ciphertext);
            TryDelete(temporaryPath);
        }
    }

    public async Task<Result<byte[]>> ReadAsync(
        string secretRef,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<byte[]>? referenceValidation = ValidateReference<byte[]>(secretRef);
        if (referenceValidation is not null)
        {
            return referenceValidation;
        }

        if (!OperatingSystem.IsWindows())
        {
            return Failure<byte[]>("SECRET_PLATFORM_UNAVAILABLE");
        }

        string path = GetFinalPath(secretRef);
        if (!File.Exists(path))
        {
            return Failure<byte[]>("SECRET_NOT_FOUND");
        }

        byte[] fileBytes = [];
        byte[] entropy = [];
        byte[] ciphertext = [];
        byte[]? plaintext = null;
        try
        {
            FileInfo fileInfo = new(path);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaximumStoredFileBytes)
            {
                return Failure<byte[]>("SECRET_DECRYPT_FAILED");
            }

            fileBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (fileBytes.Length <= 0 || fileBytes.Length > MaximumStoredFileBytes)
            {
                return Failure<byte[]>("SECRET_DECRYPT_FAILED");
            }

            if (!TryParseStoredFile(fileBytes, out entropy, out ciphertext))
            {
                return Failure<byte[]>("SECRET_DECRYPT_FAILED");
            }

            try
            {
                plaintext = ProtectedData.Unprotect(
                    ciphertext,
                    entropy,
                    DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return Failure<byte[]>("SECRET_DECRYPT_FAILED");
            }

            if (plaintext.Length == 0 || plaintext.Length > MaximumSecretBytes)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                plaintext = null;
                return Failure<byte[]>("SECRET_DECRYPT_FAILED");
            }

            return Result<byte[]>.Success(plaintext);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlatformNotSupportedException)
        {
            return Failure<byte[]>("SECRET_PLATFORM_UNAVAILABLE");
        }
        catch (FileNotFoundException)
        {
            return Failure<byte[]>("SECRET_NOT_FOUND");
        }
        catch (DirectoryNotFoundException)
        {
            return Failure<byte[]>("SECRET_NOT_FOUND");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<byte[]>("SECRET_READ_FAILED");
        }
        catch (IOException)
        {
            return Failure<byte[]>("SECRET_READ_FAILED");
        }
        catch (CryptographicException)
        {
            return Failure<byte[]>("SECRET_DECRYPT_FAILED");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileBytes);
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public Task<Result<Unit>> DeleteAsync(
        string secretRef,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<Unit>? referenceValidation = ValidateReference(secretRef);
        if (referenceValidation is not null)
        {
            return Task.FromResult(referenceValidation);
        }

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(Failure<Unit>("SECRET_PLATFORM_UNAVAILABLE"));
        }

        try
        {
            File.Delete(GetFinalPath(secretRef));
            File.Delete(GetTemporaryPath(secretRef));
            return Task.FromResult(Result.Success());
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Failure<Unit>("SECRET_DELETE_FAILED"));
        }
        catch (IOException)
        {
            return Task.FromResult(Failure<Unit>("SECRET_DELETE_FAILED"));
        }
    }

    private static async Task WriteTemporaryFileAsync(
        string path,
        byte[] entropy,
        byte[] ciphertext,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.SequentialScan);

        byte[] header = new byte[HeaderBytes];
        Magic.CopyTo(header);
        header[4] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), entropy.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(entropy, cancellationToken);
        await stream.WriteAsync(ciphertext, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        CryptographicOperations.ZeroMemory(header);
    }

    private static bool TryParseStoredFile(
        byte[] fileBytes,
        out byte[] entropy,
        out byte[] ciphertext)
    {
        entropy = [];
        ciphertext = [];
        if (fileBytes.Length < HeaderBytes + 1)
        {
            return false;
        }

        ReadOnlySpan<byte> header = fileBytes.AsSpan(0, HeaderBytes);
        if (!header[..Magic.Length].SequenceEqual(Magic) || header[4] != FormatVersion)
        {
            return false;
        }

        int entropyLength = BinaryPrimitives.ReadInt32LittleEndian(header[5..]);
        if (entropyLength != EntropyBytes || fileBytes.Length <= HeaderBytes + entropyLength)
        {
            return false;
        }

        entropy = fileBytes.AsSpan(HeaderBytes, entropyLength).ToArray();
        ciphertext = fileBytes.AsSpan(HeaderBytes + entropyLength).ToArray();
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyRestrictedFileAcl(string path)
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private string GetFinalPath(string secretRef) => Path.Combine(secretsDirectory, secretRef + FileSuffix);

    private string GetTemporaryPath(string secretRef) => Path.Combine(secretsDirectory, secretRef + TemporarySuffix);

    private static Result<Unit>? ValidateReference(string? secretRef) =>
        IsValidReference(secretRef)
            ? null
            : Failure<Unit>("SECRET_REFERENCE_INVALID");

    private static Result<T>? ValidateReference<T>(string? secretRef) =>
        IsValidReference(secretRef)
            ? null
            : Failure<T>("SECRET_REFERENCE_INVALID");

    private static bool IsValidReference(string? secretRef)
    {
        if (secretRef is null || secretRef.Length != 32)
        {
            return false;
        }

        foreach (char character in secretRef)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static Result<T> Failure<T>(string code) => Result<T>.Failure(new Problem(
        code,
        ProblemStage.Authentication,
        "problem.auth.secret_vault_failed",
        code is "SECRET_STORAGE_FAILED" or "SECRET_READ_FAILED" or "SECRET_DELETE_FAILED",
        Guid.NewGuid().ToString("N"),
        ["action.auth.retry"]));

    private static bool IsAclFailure(Exception exception) => exception is
        UnauthorizedAccessException or
        IOException or
        IdentityNotMappedException or
        System.Security.SecurityException or
        PlatformNotSupportedException or
        InvalidOperationException or
        ArgumentException;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The primary result must describe the write failure; cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // The primary result must describe the write failure; cleanup is best effort.
        }
    }
}
