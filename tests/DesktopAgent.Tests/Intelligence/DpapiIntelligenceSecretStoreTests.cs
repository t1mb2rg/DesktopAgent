using System.Text;
using DesktopAgent.WindowsInterop.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DpapiIntelligenceSecretStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"DesktopAgent.Tests.DpapiSecrets.{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task SaveResolveDelete_RoundTripsWithoutPlaintextOnDisk()
    {
        var store = new DpapiIntelligenceSecretStore(_tempDir);
        const string reference = "openai-compatible-api-key";
        const string secret = "sk-test-secret-never-plaintext";

        await store.SaveSecretAsync(reference, secret);

        Assert.True(await store.ContainsSecretAsync(reference));
        Assert.Equal(secret, await store.ResolveSecretAsync(reference));

        var file = Assert.Single(Directory.GetFiles(_tempDir, "*.dpapi"));
        var bytes = await File.ReadAllBytesAsync(file);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        Assert.True(await store.DeleteSecretAsync(reference));
        Assert.False(await store.ContainsSecretAsync(reference));
        Assert.Null(await store.ResolveSecretAsync(reference));
    }

    [Fact]
    public async Task SecretReference_CannotEscapeStoreDirectory()
    {
        var store = new DpapiIntelligenceSecretStore(_tempDir);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.SaveSecretAsync("../outside", "secret"));
    }
}
