using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.Application.DesktopAutomation;

/// <summary>一次站定授权签发的结局。每一种失败都对应一句可以照实告诉用户的话。</summary>
public enum DesktopGridDisplayAuthorizationGrantStatus
{
    Granted = 0,

    /// <summary>调用方没有给出任何显示器名，因此没有任何决定需要被记录。</summary>
    NothingRequested = 1,

    /// <summary>读不到当前桌面：无法确定这套显示设置，因此不写授权。</summary>
    ObservationUnavailable = 2,

    /// <summary>观察到了桌面，但算不出显示拓扑指纹：授权没有可依附的环境。</summary>
    TopologyUnavailable = 3,

    /// <summary>写库失败。GrantedDeviceNames 里是被写坏之前已经落盘的那部分。</summary>
    StoreFailed = 4,
}

/// <summary>
/// 签发结局。指纹即使失败也一并带回：调用方需要说明“当时算不算得出这套显示设置”。
/// </summary>
public sealed record DesktopGridDisplayAuthorizationGrantOutcome(
    DesktopGridDisplayAuthorizationGrantStatus Status,
    string TopologyFingerprint,
    IReadOnlyList<string> GrantedDeviceNames)
{
    public bool Succeeded => Status == DesktopGridDisplayAuthorizationGrantStatus.Granted;
}

/// <summary>
/// 把“用户已经同意这台机器可以自己动手”落成授权记录的入口。
///
/// 它不做任何判断，只负责把用户的决定按时按刻写下来；调用方必须是用户当面确认的路径。
/// 授权绑定**写入当时**的显示拓扑指纹，因此换屏、改分辨率、移动任务栏之后
/// 旧授权自然失效，用户会在新拓扑下被再问一次，而不是继承一个已经不成立的同意。
/// </summary>
public interface IDesktopGridDisplayAuthorizationGrantService
{
    Task<DesktopGridDisplayAuthorizationGrantOutcome> GrantAsync(
        IReadOnlyCollection<string> deviceNames,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DesktopGridDisplayAuthorizationGrantService : IDesktopGridDisplayAuthorizationGrantService
{
    private readonly IDesktopObservationSource _observationSource;
    private readonly IExplorerGridDisplayAuthorizationStore _store;
    private readonly TimeProvider _timeProvider;

    public DesktopGridDisplayAuthorizationGrantService(
        IDesktopObservationSource observationSource,
        IExplorerGridDisplayAuthorizationStore store,
        TimeProvider? timeProvider = null)
    {
        _observationSource = observationSource ?? throw new ArgumentNullException(nameof(observationSource));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DesktopGridDisplayAuthorizationGrantOutcome> GrantAsync(
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
            return new DesktopGridDisplayAuthorizationGrantOutcome(
                DesktopGridDisplayAuthorizationGrantStatus.NothingRequested,
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
            return new DesktopGridDisplayAuthorizationGrantOutcome(
                DesktopGridDisplayAuthorizationGrantStatus.ObservationUnavailable,
                string.Empty,
                Array.Empty<string>());
        }

        if (!capture.IsCaptured || capture.Observation is null)
        {
            return new DesktopGridDisplayAuthorizationGrantOutcome(
                DesktopGridDisplayAuthorizationGrantStatus.ObservationUnavailable,
                string.Empty,
                Array.Empty<string>());
        }

        // 与 Adapter、写事实方、读证据方共用同一口径：指纹算不出来就不写任何授权。
        var fingerprint = DesktopExplorerWorkspaceGeometryAdapter.ComputeTopologyIdentity(capture.Observation);
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return new DesktopGridDisplayAuthorizationGrantOutcome(
                DesktopGridDisplayAuthorizationGrantStatus.TopologyUnavailable,
                string.Empty,
                Array.Empty<string>());
        }

        var grantedAtUtc = _timeProvider.GetUtcNow();
        var granted = new List<string>(requested.Length);
        try
        {
            await _store.InitializeAsync(cancellationToken);
            foreach (var deviceName in requested)
            {
                await _store.AppendAsync(
                    new ExplorerGridDisplayAuthorization(
                        ExplorerGridAuthorizationState.Granted,
                        deviceName,
                        fingerprint,
                        grantedAtUtc),
                    cancellationToken);
                granted.Add(deviceName);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 部分写入不构成授权：调用方必须得到“没成功”，而不是“大概记住了”。
            return new DesktopGridDisplayAuthorizationGrantOutcome(
                DesktopGridDisplayAuthorizationGrantStatus.StoreFailed,
                fingerprint,
                granted.ToArray());
        }

        return new DesktopGridDisplayAuthorizationGrantOutcome(
            DesktopGridDisplayAuthorizationGrantStatus.Granted,
            fingerprint,
            granted.ToArray());
    }
}
