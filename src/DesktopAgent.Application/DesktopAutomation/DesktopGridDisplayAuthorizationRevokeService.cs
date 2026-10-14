using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.Application.DesktopAutomation;

/// <summary>一次站定授权收回的结局。每一种失败都对应一句可以照实告诉用户的话。</summary>
public enum DesktopGridDisplayAuthorizationRevokeStatus
{
    Revoked = 0,

    /// <summary>调用方没有给出任何显示器名，因此没有任何决定需要被记录。</summary>
    NothingRequested = 1,

    /// <summary>读不到当前桌面：无法确定这套显示设置，因此不写撤销。</summary>
    ObservationUnavailable = 2,

    /// <summary>观察到了桌面，但算不出显示拓扑指纹：撤销没有可依附的环境。</summary>
    TopologyUnavailable = 3,

    /// <summary>写库失败。RevokedDeviceNames 里是失败之前已经落盘的那部分。</summary>
    StoreFailed = 4,
}

/// <summary>
/// 收回结局。指纹即使失败也一并带回：调用方需要说明"当时算不算得出这套显示设置"。
/// </summary>
public sealed record DesktopGridDisplayAuthorizationRevokeOutcome(
    DesktopGridDisplayAuthorizationRevokeStatus Status,
    string TopologyFingerprint,
    IReadOnlyList<string> RevokedDeviceNames)
{
    public bool Succeeded => Status == DesktopGridDisplayAuthorizationRevokeStatus.Revoked;
}

/// <summary>
/// 把"用户不再同意这台机器自己动手"落成授权记录的入口。
///
/// 撤销是**追加**一条 Revoked，而不是删除历史：授权是用户对这台机器的长期决定，
/// "它被收回过"这件事必须可读。收回只改变以后还会不会被自动执行，
/// 不会移动任何桌面项目，也不会取消一次已经在进行中的执行。
///
/// 撤销绑定收回当时的 topology fingerprint：它只会收回那套显示设置下的同意。
/// 换屏之后旧授权本来就已经失效，不需要（也无法）为一段已经不成立的环境写撤销。
/// </summary>
public interface IDesktopGridDisplayAuthorizationRevokeService
{
    Task<DesktopGridDisplayAuthorizationRevokeOutcome> RevokeAsync(
        IReadOnlyCollection<string> deviceNames,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DesktopGridDisplayAuthorizationRevokeService : IDesktopGridDisplayAuthorizationRevokeService
{
    private readonly IDesktopObservationSource _observationSource;
    private readonly IExplorerGridDisplayAuthorizationStore _store;
    private readonly TimeProvider _timeProvider;

    public DesktopGridDisplayAuthorizationRevokeService(
        IDesktopObservationSource observationSource,
        IExplorerGridDisplayAuthorizationStore store,
        TimeProvider? timeProvider = null)
    {
        _observationSource = observationSource ?? throw new ArgumentNullException(nameof(observationSource));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DesktopGridDisplayAuthorizationRevokeOutcome> RevokeAsync(
        IReadOnlyCollection<string> deviceNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceNames);

        var requested = deviceNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requested.Length == 0)
        {
            return new DesktopGridDisplayAuthorizationRevokeOutcome(
                DesktopGridDisplayAuthorizationRevokeStatus.NothingRequested,
                string.Empty,
                Array.Empty<string>());
        }

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
            return new DesktopGridDisplayAuthorizationRevokeOutcome(
                DesktopGridDisplayAuthorizationRevokeStatus.ObservationUnavailable,
                string.Empty,
                Array.Empty<string>());
        }

        if (!capture.IsCaptured || capture.Observation is null)
        {
            return new DesktopGridDisplayAuthorizationRevokeOutcome(
                DesktopGridDisplayAuthorizationRevokeStatus.ObservationUnavailable,
                string.Empty,
                Array.Empty<string>());
        }

        var fingerprint = DesktopExplorerWorkspaceGeometryAdapter.ComputeTopologyIdentity(capture.Observation);
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return new DesktopGridDisplayAuthorizationRevokeOutcome(
                DesktopGridDisplayAuthorizationRevokeStatus.TopologyUnavailable,
                string.Empty,
                Array.Empty<string>());
        }

        var revokedAtUtc = _timeProvider.GetUtcNow();
        var revoked = new List<string>(requested.Length);
        try
        {
            await _store.InitializeAsync(cancellationToken);
            foreach (var deviceName in requested)
            {
                await _store.AppendAsync(
                    new ExplorerGridDisplayAuthorization(
                        ExplorerGridAuthorizationState.Revoked,
                        deviceName,
                        fingerprint,
                        revokedAtUtc),
                    cancellationToken);
                revoked.Add(deviceName);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 与签发同一个口径：部分写入不构成收回，调用方必须得到明确的失败。
            return new DesktopGridDisplayAuthorizationRevokeOutcome(
                DesktopGridDisplayAuthorizationRevokeStatus.StoreFailed,
                fingerprint,
                revoked.ToArray());
        }

        return new DesktopGridDisplayAuthorizationRevokeOutcome(
            DesktopGridDisplayAuthorizationRevokeStatus.Revoked,
            fingerprint,
            revoked.ToArray());
    }
}
