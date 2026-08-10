using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Application.Intelligence;

namespace DesktopAgent.WindowsInterop.Intelligence;

/// <summary>
/// Windows CurrentUser DPAPI Secret Store。磁盘只保存受 DPAPI 保护的 blob。
/// </summary>
public sealed class DpapiIntelligenceSecretStore : IIntelligenceSecretStore
{
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("DesktopAgent.Intelligence.Secret.v1");

    private readonly string _rootDirectory;

    public DpapiIntelligenceSecretStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Secret 根目录不能为空。", nameof(rootDirectory));

        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public async ValueTask SaveSecretAsync(
        string secretReference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(secretReference);
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Secret 不能为空。", nameof(secret));

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_rootDirectory);

        var plaintext = Encoding.UTF8.GetBytes(secret);
        byte[]? protectedBytes = null;
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            protectedBytes = ProtectedData.Protect(
                plaintext,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);

            await File.WriteAllBytesAsync(tempPath, protectedBytes, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public async ValueTask<string?> ResolveSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(secretReference);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
            return null;

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(
                protectedBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public ValueTask<bool> DeleteSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(secretReference);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(path))
            return ValueTask.FromResult(false);

        File.Delete(path);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> ContainsSecretAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(secretReference);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(File.Exists(path));
    }

    private string GetSecretPath(string secretReference)
    {
        var normalized = NormalizeSecretReference(secretReference);
        return Path.Combine(_rootDirectory, $"{normalized}.dpapi");
    }

    private static string NormalizeSecretReference(string secretReference)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
            throw new ArgumentException("SecretReference 不能为空。", nameof(secretReference));

        var normalized = secretReference.Trim();
        if (normalized.Length > 128 ||
            normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "SecretReference 只能包含 ASCII 字母、数字、点、短横线或下划线，且最长 128 字符。",
                nameof(secretReference));
        }

        if (normalized is "." or "..")
            throw new ArgumentException("SecretReference 不能是路径片段。", nameof(secretReference));

        return normalized;
    }
}
