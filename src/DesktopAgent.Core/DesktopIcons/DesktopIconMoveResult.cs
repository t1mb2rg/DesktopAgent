using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 单个桌面图标移动操作的结果。
/// 纯数据 record，不包含 COM 对象、PIDL 或 HWND。
/// </summary>
public sealed record DesktopIconMoveResult
{
    public string StableId { get; }
    public PixelPoint PreviousPosition { get; }
    public PixelPoint RequestedPosition { get; }
    public PixelPoint ActualPosition { get; }
    public ConnectionMethod ConnectionMethod { get; }
    public Exception? PrimaryPathException { get; }

    /// <summary>实际位置是否与请求位置完全一致。</summary>
    public bool IsExact => ActualPosition == RequestedPosition;

    public DesktopIconMoveResult(
        string stableId,
        PixelPoint previousPosition,
        PixelPoint requestedPosition,
        PixelPoint actualPosition,
        ConnectionMethod connectionMethod,
        Exception? primaryPathException)
    {
        if (stableId is null)
            throw new ArgumentNullException(nameof(stableId));
        if (string.IsNullOrWhiteSpace(stableId))
            throw new ArgumentException("StableId 不能为空白。", nameof(stableId));

        StableId = stableId;
        PreviousPosition = previousPosition;
        RequestedPosition = requestedPosition;
        ActualPosition = actualPosition;
        ConnectionMethod = connectionMethod;
        PrimaryPathException = primaryPathException;
    }
}
