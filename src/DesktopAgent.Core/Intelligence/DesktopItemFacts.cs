using DesktopAgent.Core.DesktopIcons;

namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// M4 智能层理解一个桌面项目时可使用的最小、UI 无关事实集合。
/// 不包含物理坐标、显示器瞬时身份、PIDL、COM 对象或 Writer 命令。
/// </summary>
public sealed class DesktopItemFacts
{
    /// <summary>DesktopAgent 当前用于标识该项目的 StableId；保持原值，不 Trim。</summary>
    public string StableId { get; }

    /// <summary>Explorer 中显示给用户的名称；保持原值，不 Trim。</summary>
    public string DisplayName { get; }

    /// <summary>桌面项目类型。</summary>
    public ItemKind ItemKind { get; }

    /// <summary>
    /// 可安全确定时的文件扩展名，例如 ".pdf"；不可确定或不适用时为 null。
    /// 保持原值，不做大小写归一化。
    /// </summary>
    public string? FileExtension { get; }

    /// <summary>该项目是否为 Shell 系统项目。</summary>
    public bool IsSystemItem { get; }

    /// <summary>该项目是否为文件系统项目。</summary>
    public bool IsFileSystemItem { get; }

    public DesktopItemFacts(
        string stableId,
        string displayName,
        ItemKind itemKind,
        string? fileExtension,
        bool isSystemItem,
        bool isFileSystemItem)
    {
        if (stableId is null)
            throw new ArgumentNullException(nameof(stableId));
        if (stableId.Length == 0)
            throw new ArgumentException("StableId 不能为空字符串。", nameof(stableId));
        if (displayName is null)
            throw new ArgumentNullException(nameof(displayName));
        if (displayName.Length == 0)
            throw new ArgumentException("DisplayName 不能为空字符串。", nameof(displayName));
        if (!Enum.IsDefined(itemKind))
            throw new ArgumentException($"无效的 ItemKind：{(int)itemKind}。", nameof(itemKind));
        if (fileExtension is not null)
        {
            if (fileExtension.Length == 0)
                throw new ArgumentException("FileExtension 应使用 null 表示不可用，不能使用空字符串。", nameof(fileExtension));
            if (!fileExtension.StartsWith(".", StringComparison.Ordinal))
                throw new ArgumentException("FileExtension 必须以 '.' 开头。", nameof(fileExtension));
        }

        StableId = stableId;
        DisplayName = displayName;
        ItemKind = itemKind;
        FileExtension = fileExtension;
        IsSystemItem = isSystemItem;
        IsFileSystemItem = isFileSystemItem;
    }
}
