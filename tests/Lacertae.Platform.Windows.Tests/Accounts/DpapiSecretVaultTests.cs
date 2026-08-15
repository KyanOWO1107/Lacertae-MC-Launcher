using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Lacertae.Platform.Windows.Accounts;

namespace Lacertae.Platform.Windows.Tests.Accounts;

public sealed class DpapiSecretVaultTests
{
    [Fact]
    public async Task WriteThenReadRoundTripsSecretBytesForCurrentUser()
    {
        using TestDirectory directory = new();
        DpapiSecretVault vault = new(directory.Path);
        byte[] secret = [1, 2, 3, 4, 5];

        var written = await vault.WriteAsync(
            "0123456789abcdef0123456789abcdef",
            secret,
            TestContext.Current.CancellationToken);
        var read = await vault.ReadAsync(
            "0123456789abcdef0123456789abcdef",
            TestContext.Current.CancellationToken);

        Assert.True(written.IsSuccess, written.Problem?.Code);
        Assert.True(read.IsSuccess, read.Problem?.Code);
        Assert.Equal(secret, read.Value);
    }

    [Fact]
    public async Task StoredFileDoesNotContainPlaintextOrPlaintextHashName()
    {
        using TestDirectory directory = new();
        DpapiSecretVault vault = new(directory.Path);
        byte[] secret = "refresh-token-value"u8.ToArray();
        string plaintextHash = Convert.ToHexString(SHA256.HashData(secret)).ToLowerInvariant();

        var result = await vault.WriteAsync(
            "0123456789abcdef0123456789abcdef",
            secret,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        string[] files = Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(files, file => file.Contains(plaintextHash, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, file =>
            File.ReadAllBytes(file).AsSpan().IndexOf(secret) >= 0);
    }

    [Fact]
    public async Task WriteLeavesOnlyTheFinalFileAfterAtomicReplace()
    {
        using TestDirectory directory = new();
        DpapiSecretVault vault = new(directory.Path);
        const string secretRef = "0123456789abcdef0123456789abcdef";

        var result = await vault.WriteAsync(secretRef, new byte[] { 9, 8, 7 }, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.True(File.Exists(Path.Combine(directory.Path, secretRef + ".bin")));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReadReturnsSecretNotFoundForMissingReference()
    {
        using TestDirectory directory = new();
        DpapiSecretVault vault = new(directory.Path);

        var result = await vault.ReadAsync(
            "0123456789abcdef0123456789abcdef",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("SECRET_NOT_FOUND", result.Problem?.Code);
    }

    [Fact]
    public async Task ReadReturnsDecryptFailedForTamperedCiphertext()
    {
        using TestDirectory directory = new();
        DpapiSecretVault vault = new(directory.Path);
        const string secretRef = "0123456789abcdef0123456789abcdef";
        Assert.True((await vault.WriteAsync(secretRef, new byte[] { 9, 8, 7 }, TestContext.Current.CancellationToken)).IsSuccess);
        string path = Path.Combine(directory.Path, secretRef + ".bin");
        byte[] bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        var result = await vault.ReadAsync(secretRef, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("SECRET_DECRYPT_FAILED", result.Problem?.Code);
    }

    [Fact]
    public async Task DeleteIsIdempotent()
    {
        using TestDirectory directory = new();
        DpapiSecretVault vault = new(directory.Path);
        const string secretRef = "0123456789abcdef0123456789abcdef";
        Assert.True((await vault.WriteAsync(secretRef, new byte[] { 1 }, TestContext.Current.CancellationToken)).IsSuccess);

        var first = await vault.DeleteAsync(secretRef, TestContext.Current.CancellationToken);
        var second = await vault.DeleteAsync(secretRef, TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess, first.Problem?.Code);
        Assert.True(second.IsSuccess, second.Problem?.Code);
        Assert.False(File.Exists(Path.Combine(directory.Path, secretRef + ".bin")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("ABC")]
    [InlineData("0123456789abcdef0123456789abcdef.bin")]
    public async Task SecretReferenceMustBe32LowerHexCharacters(string secretRef)
    {
        using TestDirectory directory = new();
        DpapiSecretVault vault = new(directory.Path);

        var result = await vault.WriteAsync(secretRef, new byte[] { 1 }, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("SECRET_REFERENCE_INVALID", result.Problem?.Code);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task StoredFileUsesRestrictedCurrentUserAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TestDirectory directory = new();
        DpapiSecretVault vault = new(directory.Path);
        const string secretRef = "0123456789abcdef0123456789abcdef";
        Assert.True((await vault.WriteAsync(secretRef, new byte[] { 1 }, TestContext.Current.CancellationToken)).IsSuccess);

        FileSecurity security = new FileInfo(Path.Combine(directory.Path, secretRef + ".bin")).GetAccessControl();
        Assert.False(security.AreAccessRulesProtected is false);
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User!;
        FileSystemAccessRule? currentRule = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .FirstOrDefault(rule => rule.IdentityReference == currentUser);
        Assert.NotNull(currentRule);
        Assert.True(currentRule!.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.DoesNotContain(
            security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Value is "S-1-1-0" or "S-1-5-32-545");
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "lacertae-secrets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
