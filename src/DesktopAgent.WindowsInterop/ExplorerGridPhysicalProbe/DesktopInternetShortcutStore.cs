using System.Text;

namespace DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

/// <summary>
/// 仅管理当前 Probe 自己创建的唯一命名 .url 文件。不会枚举、覆盖或删除其他桌面项目。
/// </summary>
public sealed class DesktopInternetShortcutStore : IExplorerGridProbeShortcutStore
{
    private const string OwnedFilePrefix = "DAProbe-";
    private readonly string _desktopDirectory;
    private readonly object _ownedPathsGate = new();
    private readonly HashSet<string> _ownedPaths = new(StringComparer.OrdinalIgnoreCase);

    public DesktopInternetShortcutStore()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
    {
    }

    public DesktopInternetShortcutStore(string desktopDirectory)
    {
        if (string.IsNullOrWhiteSpace(desktopDirectory))
            throw new ArgumentException("桌面目录不能为空白。", nameof(desktopDirectory));

        _desktopDirectory = Path.GetFullPath(desktopDirectory);
    }

    public async Task<IReadOnlyList<ExplorerGridProbeShortcut>> CreateAsync(
        int count,
        CancellationToken cancellationToken)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!Directory.Exists(_desktopDirectory))
            throw new DirectoryNotFoundException($"桌面目录不存在：{_desktopDirectory}");

        // Keep the visible label deliberately short. IFolderView reports the upper-left
        // of the whole item, and a long label can be reflowed/clamped at a display edge,
        // which would contaminate a grid-position probe with text-layout geometry.
        var runId = Guid.NewGuid().ToString("N")[..8];
        var created = new List<ExplorerGridProbeShortcut>(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(
                    _desktopDirectory,
                    $"{OwnedFilePrefix}{runId}-{index + 1:D2}.url");
                await using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);
                await using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(
                    "[InternetShortcut]\r\n" +
                    "URL=https://example.invalid/desktopagent-grid-probe\r\n" +
                    "IconFile=%SystemRoot%\\System32\\shell32.dll\r\n" +
                    "IconIndex=167\r\n");
                await writer.FlushAsync(cancellationToken);
                lock (_ownedPathsGate)
                    _ownedPaths.Add(Path.GetFullPath(path));
                created.Add(new ExplorerGridProbeShortcut(path));
            }

            return Array.AsReadOnly(created.ToArray());
        }
        catch
        {
            foreach (var shortcut in created)
            {
                try
                {
                    File.Delete(shortcut.FilePath);
                    lock (_ownedPathsGate)
                        _ownedPaths.Remove(Path.GetFullPath(shortcut.FilePath));
                }
                catch
                {
                    // 原始创建异常优先；Runner 的最终核验仍会报告残留。
                }
            }

            throw;
        }
    }

    public Task DeleteAsync(
        IReadOnlyList<ExplorerGridProbeShortcut> shortcuts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        foreach (var shortcut in shortcuts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(shortcut);

            var resolvedPath = Path.GetFullPath(shortcut.FilePath);
            var isOwnedByThisProcess = false;
            lock (_ownedPathsGate)
                isOwnedByThisProcess = _ownedPaths.Contains(resolvedPath);
            if (!isOwnedByThisProcess ||
                !string.Equals(
                    Path.GetDirectoryName(resolvedPath),
                    _desktopDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedPath).StartsWith(
                    OwnedFilePrefix,
                    StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(resolvedPath), ".url", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"拒绝删除不属于本次 Probe 命名空间的路径：{resolvedPath}");
            }

            File.Delete(resolvedPath);
            lock (_ownedPathsGate)
                _ownedPaths.Remove(resolvedPath);
        }

        return Task.CompletedTask;
    }
}
