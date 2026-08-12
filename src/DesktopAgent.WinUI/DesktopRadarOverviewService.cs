using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.Productization;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.Persistence.Productization;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.WinUI;

internal sealed record DesktopRadarOverviewState(
    DesktopRadarSnapshot? Snapshot,
    DesktopLayoutTemplate? ActiveLayout,
    DesktopProductSettings Settings,
    string FreshnessText,
    string? StatusMessage);

/// <summary>
/// Overview Radar 的轻量缓存 + 按需刷新协调器。
/// 缓存读取不触碰 Shell；Fresh DesktopObservation 只在首帧之后按需执行。
/// </summary>
internal sealed class DesktopRadarOverviewService : IDisposable
{
    private static readonly TimeSpan RefreshAge = TimeSpan.FromMinutes(2);

    private readonly JsonDesktopRadarSnapshotStore _cacheStore =
        new(DesktopAgentDataPaths.DefaultRadarCachePath);
    private readonly JsonDesktopProductSettingsStore _settingsStore =
        new(DesktopAgentDataPaths.DefaultSettingsPath);
    private readonly SqliteDesktopLayoutTemplateStore _templateStore =
        new(DesktopAgentDataPaths.DefaultDatabasePath);
    private readonly StaThreadExecutor _staExecutor = new();
    private readonly WindowsDesktopObservationSource _observationSource;
    private readonly DesktopRadarSnapshotBuilder _snapshotBuilder = new();
    private bool _disposed;

    public DesktopRadarOverviewService()
    {
        _observationSource = new WindowsDesktopObservationSource(
            new NativeDisplayTopologyReader(),
            new DesktopShellIconReader(),
            new DesktopShellIconPositioningCapabilitiesReader(),
            _staExecutor);
    }

    public async Task<DesktopRadarOverviewState> LoadCachedAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = await LoadLayoutContextAsync(cancellationToken);

        DesktopRadarSnapshot? snapshot = null;
        string? status = null;
        try
        {
            snapshot = await _cacheStore.LoadAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            // Presentation cache 可直接抛弃；下一次 fresh refresh 会覆盖它。
            status = "桌面地图缓存需要刷新";
        }

        return new DesktopRadarOverviewState(
            snapshot,
            context.Layout,
            context.Settings,
            FormatFreshness(snapshot?.ObservedAtUtc),
            status);
    }

    public bool ShouldRefresh(DesktopRadarSnapshot? snapshot)
    {
        if (snapshot is null)
            return true;

        return DateTimeOffset.UtcNow - snapshot.ObservedAtUtc >= RefreshAge;
    }

    public async Task<DesktopRadarOverviewState> RefreshAsync(
        DesktopRadarSnapshot? previousSnapshot,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var capture = await _observationSource.CaptureAsync(cancellationToken);
        var context = await LoadLayoutContextAsync(cancellationToken);

        if (!capture.IsCaptured || capture.Observation is null)
        {
            var message = capture.Issues.Count == 0
                ? "当前桌面暂时不可读取"
                : capture.Issues[0].Message;
            return new DesktopRadarOverviewState(
                previousSnapshot,
                context.Layout,
                context.Settings,
                FormatFreshness(previousSnapshot?.ObservedAtUtc),
                message);
        }

        var snapshot = _snapshotBuilder.Build(
            capture.Observation,
            previousSnapshot,
            context.Layout?.TemplateId,
            context.Layout?.Version);
        await _cacheStore.SaveAsync(snapshot, cancellationToken);

        return new DesktopRadarOverviewState(
            snapshot,
            context.Layout,
            context.Settings,
            FormatFreshness(snapshot.ObservedAtUtc),
            statusMessage: null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _staExecutor.Dispose();
    }

    private async Task<LayoutContext> LoadLayoutContextAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsStore.GetAsync(cancellationToken);
        DesktopLayoutTemplate? layout = null;
        if (settings.ActiveLayoutTemplateId is Guid templateId)
        {
            await _templateStore.InitializeAsync(cancellationToken);
            layout = await _templateStore.GetAsync(templateId, cancellationToken);
        }

        return new LayoutContext(settings, layout);
    }

    private static string FormatFreshness(DateTimeOffset? observedAtUtc)
    {
        if (!observedAtUtc.HasValue)
            return "等待首次刷新";

        var age = DateTimeOffset.UtcNow - observedAtUtc.Value;
        if (age < TimeSpan.Zero || age < TimeSpan.FromSeconds(45))
            return "刚刚更新";
        if (age < TimeSpan.FromHours(1))
            return $"{Math.Max(1, (int)Math.Floor(age.TotalMinutes))} 分钟前";

        return "需要刷新";
    }

    private sealed record LayoutContext(
        DesktopProductSettings Settings,
        DesktopLayoutTemplate? Layout);
}
