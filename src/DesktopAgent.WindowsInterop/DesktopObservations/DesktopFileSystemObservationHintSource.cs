using DesktopAgent.Application.DesktopObservations;

namespace DesktopAgent.WindowsInterop.DesktopObservations;

public sealed record DesktopFileSystemHintSourceStartResult
{
    public bool IsRunning { get; }
    public int ActiveWatcherCount { get; }
    public IReadOnlyList<string> WatchedDirectories { get; }
    public IReadOnlyList<string> Issues { get; }

    public DesktopFileSystemHintSourceStartResult(
        bool isRunning,
        int activeWatcherCount,
        IReadOnlyList<string> watchedDirectories,
        IReadOnlyList<string> issues)
    {
        if (activeWatcherCount < 0)
            throw new ArgumentOutOfRangeException(nameof(activeWatcherCount));
        ArgumentNullException.ThrowIfNull(watchedDirectories);
        ArgumentNullException.ThrowIfNull(issues);

        if (isRunning != (activeWatcherCount > 0))
            throw new ArgumentException(
                "IsRunning 必须与 ActiveWatcherCount 是否大于 0 一致。",
                nameof(isRunning));

        IsRunning = isRunning;
        ActiveWatcherCount = activeWatcherCount;
        WatchedDirectories = Array.AsReadOnly(watchedDirectories.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
    }
}

/// <summary>
/// 监听用户 Desktop 与 Public Desktop 顶层文件系统条目变化，并只发布“值得重新观察”的 hint。
/// 文件路径、文件名和 FileSystemWatcher payload 都不会进入 DesktopObservationHint。
/// </summary>
public sealed class DesktopFileSystemObservationHintSource : IDesktopObservationHintSource
{
    public const string ProviderName = "DesktopFileSystemWatcher";

    private readonly object _gate = new();
    private readonly Action<DesktopObservationHintKind, string> _publishHint;
    private readonly IReadOnlyList<string> _directories;
    private readonly IDesktopDirectoryWatcherFactory _watcherFactory;
    private readonly List<IDesktopDirectoryWatcher> _watchers = [];

    private int _isRunning;
    private bool _disposed;
    private DesktopFileSystemHintSourceStartResult? _lastStartResult;

    public string Source => ProviderName;
    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;
    public IReadOnlyList<string> ConfiguredDirectories => _directories;

    public DesktopFileSystemObservationHintSource(
        Action<DesktopObservationHintKind, string> publishHint)
        : this(
            publishHint,
            GetDefaultDesktopDirectories(),
            new FileSystemDesktopDirectoryWatcherFactory())
    {
    }

    internal DesktopFileSystemObservationHintSource(
        Action<DesktopObservationHintKind, string> publishHint,
        IEnumerable<string> directories,
        IDesktopDirectoryWatcherFactory watcherFactory)
    {
        _publishHint = publishHint ?? throw new ArgumentNullException(nameof(publishHint));
        ArgumentNullException.ThrowIfNull(directories);
        _watcherFactory = watcherFactory ?? throw new ArgumentNullException(nameof(watcherFactory));
        _directories = NormalizeDirectories(directories);
    }

    public DesktopFileSystemHintSourceStartResult Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsRunning && _lastStartResult is not null)
                return _lastStartResult;

            var issues = new List<string>();
            var watchedDirectories = new List<string>();

            foreach (var directory in _directories)
            {
                IDesktopDirectoryWatcher? watcher = null;
                try
                {
                    watcher = _watcherFactory.Create(directory);
                    watcher.Changed += OnChanged;
                    watcher.Error += OnError;
                    watcher.Start();
                    _watchers.Add(watcher);
                    watchedDirectories.Add(directory);
                }
                catch (Exception ex)
                {
                    if (watcher is not null)
                    {
                        watcher.Changed -= OnChanged;
                        watcher.Error -= OnError;
                        try
                        {
                            watcher.Dispose();
                        }
                        catch
                        {
                        }
                    }

                    issues.Add(
                        $"无法监听桌面目录 “{directory}”：{ex.GetType().Name}: {ex.Message}");
                }
            }

            if (_watchers.Count == 0)
            {
                issues.Add(
                    _directories.Count == 0
                        ? "未解析到用户 Desktop 或 Public Desktop 目录。"
                        : "没有任何 Desktop 文件系统 watcher 成功启动。");
            }

            Interlocked.Exchange(ref _isRunning, _watchers.Count > 0 ? 1 : 0);
            _lastStartResult = new DesktopFileSystemHintSourceStartResult(
                IsRunning,
                _watchers.Count,
                watchedDirectories,
                issues);
            return _lastStartResult;
        }
    }

    DesktopObservationHintSourceStartResult IDesktopObservationHintSource.Start()
    {
        var result = Start();
        return new DesktopObservationHintSourceStartResult(
            ProviderName,
            result.IsRunning,
            result.ActiveWatcherCount,
            result.WatchedDirectories,
            result.Issues);
    }

    public void Stop()
    {
        List<IDesktopDirectoryWatcher> watchers;
        lock (_gate)
        {
            Interlocked.Exchange(ref _isRunning, 0);
            watchers = _watchers.ToList();
            _watchers.Clear();
            _lastStartResult = null;
        }

        foreach (var watcher in watchers)
        {
            watcher.Changed -= OnChanged;
            watcher.Error -= OnError;
            try
            {
                watcher.Stop();
            }
            catch
            {
            }

            try
            {
                watcher.Dispose();
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        Stop();
    }

    internal static IReadOnlyList<string> GetDefaultDesktopDirectories() =>
        NormalizeDirectories(
            new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            });

    internal static IReadOnlyList<string> NormalizeDirectories(
        IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var normalized = directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Array.AsReadOnly(normalized);
    }

    private void OnChanged()
    {
        if (!IsRunning)
            return;

        PublishHintSafely(DesktopObservationHintKind.FileSystem);
    }

    private void OnError(Exception? exception)
    {
        if (!IsRunning)
            return;

        // Buffer overflow / watcher infrastructure errors still only mean“值得重新观察”。
        // 具体 exception 不进入 hint payload。
        _ = exception;
        PublishHintSafely(DesktopObservationHintKind.Unknown);
    }

    private void PublishHintSafely(DesktopObservationHintKind kind)
    {
        try
        {
            _publishHint(kind, ProviderName);
        }
        catch
        {
            // FileSystemWatcher callback thread 不能被上层 observer/pump 异常杀死。
        }
    }
}

internal interface IDesktopDirectoryWatcherFactory
{
    IDesktopDirectoryWatcher Create(string directory);
}

internal interface IDesktopDirectoryWatcher : IDisposable
{
    event Action? Changed;
    event Action<Exception?>? Error;

    void Start();
    void Stop();
}

internal sealed class FileSystemDesktopDirectoryWatcherFactory : IDesktopDirectoryWatcherFactory
{
    public IDesktopDirectoryWatcher Create(string directory) =>
        new FileSystemDesktopDirectoryWatcher(directory);
}

internal sealed class FileSystemDesktopDirectoryWatcher : IDesktopDirectoryWatcher
{
    private readonly FileSystemWatcher _watcher;
    private bool _disposed;

    public event Action? Changed;
    public event Action<Exception?>? Error;

    public FileSystemDesktopDirectoryWatcher(string directory)
    {
        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };

        _watcher.Created += OnEntryChanged;
        _watcher.Deleted += OnEntryChanged;
        _watcher.Renamed += OnEntryRenamed;
        _watcher.Error += OnWatcherError;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (_disposed)
            return;
        _watcher.EnableRaisingEvents = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnEntryChanged;
        _watcher.Deleted -= OnEntryChanged;
        _watcher.Renamed -= OnEntryRenamed;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
    }

    private void OnEntryChanged(object sender, FileSystemEventArgs e)
    {
        _ = sender;
        _ = e;
        Changed?.Invoke();
    }

    private void OnEntryRenamed(object sender, RenamedEventArgs e)
    {
        _ = sender;
        _ = e;
        Changed?.Invoke();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _ = sender;
        Error?.Invoke(e.GetException());
    }
}
