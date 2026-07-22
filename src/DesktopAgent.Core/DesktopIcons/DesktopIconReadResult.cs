namespace DesktopAgent.Core.DesktopIcons;

/// <summary>
/// 桌面图标读取的完整结果，包含图标列表、诊断信息与连接方式。
/// 纯数据 record，不包含 COM 对象、PIDL 或 WPF 类型。
/// </summary>
public sealed record DesktopIconReadResult(
    /// <summary>成功读取的图标描述列表。</summary>
    IReadOnlyList<DesktopIconDescriptor> Icons,

    /// <summary>单项读取失败的图标数量。</summary>
    int FailedItemCount,

    /// <summary>实际使用的 Shell 连接方式。</summary>
    ConnectionMethod ConnectionMethod,

    /// <summary>读取耗时。</summary>
    TimeSpan ReadTime,

    /// <summary>
    /// 当主路径失败并进入降级路径时，保留主路径异常用于诊断；
    /// 主路径成功时为 null。
    /// </summary>
    Exception? PrimaryPathException);
