using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.WindowsInterop.DesktopObservations;

namespace DesktopAgent.Tests.DesktopObservations;

public sealed class DesktopFileSystemObservationHintSourceTests
{
    [Fact]
    public void NormalizeDirectories_IgnoresBlank_DeduplicatesCaseInsensitively_AndSorts()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DesktopAgent-M3-02D"));
        var first = Path.Combine(root, "z-desktop");
        var duplicate = first.ToUpperInvariant() + Path.DirectorySeparatorChar;
        var second = Path.Combine(root, "a-desktop") + Path.DirectorySeparatorChar;

        var result = DesktopFileSystemObservationHintSource.NormalizeDirectories(
            new[] { "  ", first, duplicate, second, string.Empty });

        Assert.Equal(2, result.Count);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            result[0],
            ignoreCase: true);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            result[1],
            ignoreCase: true);
    }

    [Fact]
    public void Start_TwoDirectories_StartsTwoWatchers_AndIsIdempotentWhileRunning()
    {
        var factory = new FakeWatcherFactory();
        var source = Source(factory, out _, "c:\\desktop-a", "c:\\desktop-b");

        var first = source.Start();
        var second = source.Start();

        Assert.True(first.IsRunning);
        Assert.Equal(2, first.ActiveWatcherCount);
        Assert.Empty(first.Issues);
        Assert.Equal(2, factory.Created.Count);
        Assert.Same(first, second);
        Assert.All(factory.Created, watcher => Assert.Equal(1, watcher.StartCount));

        source.Dispose();
    }

    [Fact]
    public void Changed_PublishesOnlyFileSystemKindAndProviderIdentity()
    {
        var factory = new FakeWatcherFactory();
        var source = Source(factory, out var published, "c:\\desktop");
        source.Start();

        factory.Created.Single().EmitChanged();

        var hint = Assert.Single(published);
        Assert.Equal(DesktopObservationHintKind.FileSystem, hint.Kind);
        Assert.Equal(DesktopFileSystemObservationHintSource.ProviderName, hint.Source);

        source.Dispose();
    }

    [Fact]
    public void Error_PublishesUnknownWithoutLeakingExceptionPayload()
    {
        var factory = new FakeWatcherFactory();
        var source = Source(factory, out var published, "c:\\desktop");
        source.Start();

        factory.Created.Single().EmitError(new IOException("secret path c:\\desktop\\private.txt"));

        var hint = Assert.Single(published);
        Assert.Equal(DesktopObservationHintKind.Unknown, hint.Kind);
        Assert.Equal(DesktopFileSystemObservationHintSource.ProviderName, hint.Source);
        Assert.DoesNotContain("private", hint.Source, StringComparison.OrdinalIgnoreCase);

        source.Dispose();
    }

    [Fact]
    public void PublishCallbackThrowing_DoesNotEscapeWatcherCallback()
    {
        var factory = new FakeWatcherFactory();
        using var source = new DesktopFileSystemObservationHintSource(
            (_, _) => throw new InvalidOperationException("sink failed"),
            new[] { "c:\\desktop" },
            factory);
        source.Start();
        var watcher = factory.Created.Single();

        var changed = Record.Exception(watcher.EmitChanged);
        var error = Record.Exception(() => watcher.EmitError(new IOException("watcher failed")));

        Assert.Null(changed);
        Assert.Null(error);
    }

    [Fact]
    public void Start_PartialFailure_KeepsSuccessfulWatcherRunning()
    {
        var factory = new FakeWatcherFactory(path =>
            path.EndsWith("desktop-b", StringComparison.OrdinalIgnoreCase)
                ? new IOException("access denied")
                : null);
        var source = Source(factory, out _, "c:\\desktop-a", "c:\\desktop-b");

        var result = source.Start();

        Assert.True(result.IsRunning);
        Assert.Equal(1, result.ActiveWatcherCount);
        Assert.Single(result.WatchedDirectories);
        var issue = Assert.Single(result.Issues);
        Assert.Contains("desktop-b", issue, StringComparison.OrdinalIgnoreCase);
        Assert.True(source.IsRunning);

        source.Dispose();
    }

    [Fact]
    public void Start_AllFailures_ReturnsNotRunningWithIssues()
    {
        var factory = new FakeWatcherFactory(_ => new IOException("cannot watch"));
        var source = Source(factory, out _, "c:\\desktop-a", "c:\\desktop-b");

        var result = source.Start();

        Assert.False(result.IsRunning);
        Assert.Equal(0, result.ActiveWatcherCount);
        Assert.Empty(result.WatchedDirectories);
        Assert.Equal(3, result.Issues.Count); // two directory failures + no watcher started summary
        Assert.False(source.IsRunning);

        source.Dispose();
    }

    [Fact]
    public void Start_WatcherStartFailure_DisposesThatWatcherAndContinues()
    {
        var factory = new FakeWatcherFactory();
        factory.StartFailures["c:\\desktop-a"] = new IOException("start failed");
        var source = Source(factory, out _, "c:\\desktop-a", "c:\\desktop-b");

        var result = source.Start();

        Assert.True(result.IsRunning);
        Assert.Equal(1, result.ActiveWatcherCount);
        var failed = factory.Created.Single(w =>
            string.Equals(w.Directory, "c:\\desktop-a", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, failed.DisposeCount);
        Assert.Single(result.Issues);

        source.Dispose();
    }

    [Fact]
    public void Stop_UnsubscribesStopsDisposes_AndLateEventsDoNotPublish()
    {
        var factory = new FakeWatcherFactory();
        var source = Source(factory, out var published, "c:\\desktop");
        source.Start();
        var watcher = factory.Created.Single();

        watcher.EmitChanged();
        Assert.Single(published);

        source.Stop();
        watcher.EmitChanged();
        watcher.EmitError(new IOException("late"));

        Assert.Single(published);
        Assert.False(source.IsRunning);
        Assert.Equal(1, watcher.StopCount);
        Assert.Equal(1, watcher.DisposeCount);
    }

    [Fact]
    public void Stop_ThenStart_CreatesFreshWatchers()
    {
        var factory = new FakeWatcherFactory();
        var source = Source(factory, out _, "c:\\desktop");

        source.Start();
        source.Stop();
        var second = source.Start();

        Assert.True(second.IsRunning);
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(1, factory.Created[0].DisposeCount);
        Assert.Equal(1, factory.Created[1].StartCount);

        source.Dispose();
    }

    [Fact]
    public void Dispose_ThenStart_ThrowsObjectDisposedException()
    {
        var factory = new FakeWatcherFactory();
        var source = Source(factory, out _, "c:\\desktop");
        source.Start();

        source.Dispose();

        Assert.Throws<ObjectDisposedException>(source.Start);
    }

    private static DesktopFileSystemObservationHintSource Source(
        FakeWatcherFactory factory,
        out List<PublishedHint> published,
        params string[] directories)
    {
        var captured = new List<PublishedHint>();
        published = captured;
        return new DesktopFileSystemObservationHintSource(
            (kind, source) => captured.Add(new PublishedHint(kind, source)),
            directories,
            factory);
    }

    private sealed record PublishedHint(
        DesktopObservationHintKind Kind,
        string Source);

    private sealed class FakeWatcherFactory(
        Func<string, Exception?>? createFailure = null)
        : IDesktopDirectoryWatcherFactory
    {
        public List<FakeWatcher> Created { get; } = [];
        public Dictionary<string, Exception> StartFailures { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public IDesktopDirectoryWatcher Create(string directory)
        {
            var failure = createFailure?.Invoke(directory);
            if (failure is not null)
                throw failure;

            StartFailures.TryGetValue(directory, out var startFailure);
            var watcher = new FakeWatcher(directory, startFailure);
            Created.Add(watcher);
            return watcher;
        }
    }

    private sealed class FakeWatcher(
        string directory,
        Exception? startFailure)
        : IDesktopDirectoryWatcher
    {
        public event Action? Changed;
        public event Action<Exception?>? Error;

        public string Directory { get; } = directory;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void Start()
        {
            StartCount++;
            if (startFailure is not null)
                throw startFailure;
        }

        public void Stop() => StopCount++;

        public void Dispose() => DisposeCount++;

        public void EmitChanged() => Changed?.Invoke();

        public void EmitError(Exception? exception) => Error?.Invoke(exception);
    }
}
