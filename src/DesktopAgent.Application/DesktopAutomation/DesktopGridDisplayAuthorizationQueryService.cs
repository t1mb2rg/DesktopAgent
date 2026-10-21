using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.Application.DesktopAutomation;

/// <summary>读取"当前显示设置下记住了哪些显示器"的结局。</summary>
public enum DesktopGridDisplayAuthorizationQueryStatus
{
    /// <summary>读到了，Authorizations 就是当前仍然成立的那一组（可能为空）。</summary>
    Available = 0,

    /// <summary>读不到当前桌面：无法确定这套显示设置。</summary>
    ObservationUnavailable = 1,

    /// <summary>观察到了桌面，但算不出显示拓扑指纹：授权没有可依附的环境。</summary>
    TopologyUnavailable = 2,

    /// <summary>读库失败。此时不回答"没有授权"——不知道不等于没有。</summary>
    StoreFailed = 3,
}

/// <summary>
/// 当前显示设置下的站定授权快照。
///
/// 只包含绑定**当前** topology fingerprint 且仍然 Granted 的授权。
/// 已撤销的、绑定别的拓扑的都不在这里：它们已经是历史，不是当前授权。
/// </summary>
public sealed record DesktopGridDisplayAuthorizationSnapshot(
    DesktopGridDisplayAuthorizationQueryStatus Status,
    string TopologyFingerprint,
    IReadOnlyList<ExplorerGridDisplayAuthorization> Authorizations)
{
    public bool IsAvailable => Status == DesktopGridDisplayAuthorizationQueryStatus.Available;
}

/// <summary>
/// 把"现在这台机器上，人已经同意过哪些显示器可以自己动手"读出来的入口。
///
/// 它只读，不做任何决定；存在的理由是授权必须可被看见——一个用户看不见的
/// 长期授权，和没有授权在体验上是一样的，唯一的区别是前者更危险。
/// </summary>
public interface IDesktopGridDisplayAuthorizationQueryService
{
    Task<DesktopGridDisplayAuthorizationSnapshot> ReadAsync(
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DesktopGridDisplayAuthorizationQueryService : IDesktopGridDisplayAuthorizationQueryService
{
    private readonly IDesktopObservationSource _observationSource;
    private readonly IExplorerGridDisplayAuthorizationStore _store;

    public DesktopGridDisplayAuthorizationQueryService(
        IDesktopObservationSource observationSource,
        IExplorerGridDisplayAuthorizationStore store)
    {
        _observationSource = observationSource ?? throw new ArgumentNullException(nameof(observationSource));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<DesktopGridDisplayAuthorizationSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        DesktopObservationCaptureResult capture;
        try
        {
            capture = await _observationSource.CaptureAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failure(DesktopGridDisplayAuthorizationQueryStatus.ObservationUnavailable);
        }

        if (!capture.IsCaptured || capture.Observation is null)
        {
            return Failure(DesktopGridDisplayAuthorizationQueryStatus.ObservationUnavailable);
        }

        // 与 Adapter、写事实方、读证据方共用同一口径：指纹必须算得出来。
        var fingerprint = DesktopExplorerWorkspaceGeometryAdapter.ComputeTopologyIdentity(capture.Observation);
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return Failure(DesktopGridDisplayAuthorizationQueryStatus.TopologyUnavailable);
        }

        IReadOnlyList<ExplorerGridDisplayAuthorization> active;
        try
        {
            await _store.InitializeAsync(cancellationToken);
            active = await _store.ListActiveAsync(fingerprint, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 读不出来就说读不出来：不能把"不知道"讲成"没有授权"，
            // 否则界面会显示成"没人同意过"，而真实情况可能是"库坏了"。
            return new DesktopGridDisplayAuthorizationSnapshot(
                DesktopGridDisplayAuthorizationQueryStatus.StoreFailed,
                fingerprint,
                Array.Empty<ExplorerGridDisplayAuthorization>());
        }

        // Store 返回的是"每块屏最新一条决定"，其中可能已经是 Revoked。
        // 只有仍然 Granted 的才构成当前授权。
        var granted = active
            .Where(static authorization =>
                authorization.State == ExplorerGridAuthorizationState.Granted)
            .ToArray();

        return new DesktopGridDisplayAuthorizationSnapshot(
            DesktopGridDisplayAuthorizationQueryStatus.Available,
            fingerprint,
            granted);
    }

    private static DesktopGridDisplayAuthorizationSnapshot Failure(
        DesktopGridDisplayAuthorizationQueryStatus status)
        => new(status, string.Empty, Array.Empty<ExplorerGridDisplayAuthorization>());
}
