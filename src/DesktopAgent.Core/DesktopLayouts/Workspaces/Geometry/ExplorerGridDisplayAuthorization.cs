namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

/// <summary>
/// 一条站定授权的状态。
///
/// 撤销同样要留痕：授权是用户对"这台机器可以自己动手"的长期决定，
/// "它被收回过"这件事必须可读，而不是靠记录消失来表达。
/// </summary>
public enum ExplorerGridAuthorizationState
{
    Granted = 0,
    Revoked = 1,
}

/// <summary>
/// 用户对一块显示器的站定授权：在当前显示拓扑下，允许无人值守执行。
///
/// 它与 <see cref="ExplorerGridMeasurement"/> 是两件不同的事，因此不放在同一张表里：
/// 网格事实回答"图标会落在哪"，那是证据；授权回答"人是否已经同意它自己动手"，
/// 那是决定。把两者混在一起，会让"实测过"看起来像"已经同意过"。
///
/// 授权是环境绑定的：它必须绑定签发它时的 topology fingerprint。
/// 换屏、改分辨率、移动任务栏之后，旧授权与新环境无关；
/// 与新拓扑不匹配的授权一律不再被选出——与网格事实同一套作废语义。
/// </summary>
public sealed record ExplorerGridDisplayAuthorization
{
    public ExplorerGridAuthorizationState State { get; }
    public string DeviceName { get; }
    public string TopologyFingerprint { get; }
    public DateTimeOffset AuthorizedAtUtc { get; }

    public ExplorerGridDisplayAuthorization(
        ExplorerGridAuthorizationState state,
        string deviceName,
        string topologyFingerprint,
        DateTimeOffset authorizedAtUtc)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), "Authorization State 无效。");
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("DeviceName 不能为空。", nameof(deviceName));
        if (string.IsNullOrWhiteSpace(topologyFingerprint))
            throw new ArgumentException("TopologyFingerprint 不能为空。", nameof(topologyFingerprint));
        if (authorizedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("AuthorizedAtUtc 必须是 UTC。", nameof(authorizedAtUtc));

        State = state;
        DeviceName = deviceName.Trim();
        TopologyFingerprint = topologyFingerprint.Trim();
        AuthorizedAtUtc = authorizedAtUtc;
    }

    /// <summary>
    /// 这条授权能否为"当前拓扑下的无人值守执行"背书。
    ///
    /// 已撤销的、绑定别的拓扑的、或指纹为空的调用一律 false。
    /// 没有授权时无人值守必须停下，而不是退回某个更宽松的默认值。
    /// </summary>
    public bool AuthorizesUnattendedExecution(string currentTopologyFingerprint)
        => State == ExplorerGridAuthorizationState.Granted &&
           !string.IsNullOrWhiteSpace(currentTopologyFingerprint) &&
           string.Equals(
               TopologyFingerprint,
               currentTopologyFingerprint.Trim(),
               StringComparison.Ordinal);
}
