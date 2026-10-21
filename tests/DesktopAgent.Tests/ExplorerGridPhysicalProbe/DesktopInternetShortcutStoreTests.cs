using DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

namespace DesktopAgent.Tests.ExplorerGridPhysicalProbe;

public sealed class DesktopInternetShortcutStoreTests
{
    [Fact]
    public async Task CreateAsync_UsesShortLabels_AndDeletesOnlyReturnedOwnedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"desktopagent-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new DesktopInternetShortcutStore(directory);
            var shortcuts = await store.CreateAsync(3, CancellationToken.None);

            Assert.All(shortcuts, shortcut =>
            {
                var fileName = Path.GetFileName(shortcut.FilePath);
                Assert.StartsWith("DAProbe-", fileName, StringComparison.Ordinal);
                Assert.True(fileName.Length <= 24, $"Probe label unexpectedly long: {fileName}");
                Assert.True(File.Exists(shortcut.FilePath));
            });

            await store.DeleteAsync(shortcuts, CancellationToken.None);

            Assert.All(shortcuts, shortcut => Assert.False(File.Exists(shortcut.FilePath)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_RejectsMatchingNameNotCreatedByThisStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"desktopagent-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var unrelatedPath = Path.Combine(directory, "DAProbe-not-owned-01.url");
        await File.WriteAllTextAsync(unrelatedPath, "unrelated");
        try
        {
            var store = new DesktopInternetShortcutStore(directory);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.DeleteAsync(
                    [new ExplorerGridProbeShortcut(unrelatedPath)],
                    CancellationToken.None));
            Assert.True(File.Exists(unrelatedPath));
        }
        finally
        {
            File.Delete(unrelatedPath);
            Directory.Delete(directory, recursive: true);
        }
    }
}
