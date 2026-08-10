using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>首次桌面理解请求。长期 StableId 只保留在本地，Provider 侧使用临时 item_ref。</summary>
public sealed class DesktopUnderstandingRequest
{
    public const int MaxItemCount = 256;
    public const int MaxUserIntentLength = 4_000;

    private readonly IReadOnlyList<DesktopItemFacts> _items;

    public IReadOnlyList<DesktopItemFacts> Items => _items;
    public string? UserIntent { get; }

    public DesktopUnderstandingRequest(
        IEnumerable<DesktopItemFacts> items,
        string? userIntent = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var copied = items.ToArray();
        if (copied.Length is < 1 or > MaxItemCount)
            throw new ArgumentOutOfRangeException(nameof(items),
                $"Items 数量必须在 1..{MaxItemCount}。更多项目应由上层显式分批，而不是静默截断。");
        if (copied.Any(static item => item is null))
            throw new ArgumentException("Items 不能包含 null。", nameof(items));

        if (userIntent is not null)
        {
            if (string.IsNullOrWhiteSpace(userIntent))
                throw new ArgumentException("UserIntent 使用 null 表示未提供，不能只包含空白。", nameof(userIntent));
            if (userIntent.Length > MaxUserIntentLength)
                throw new ArgumentOutOfRangeException(nameof(userIntent),
                    $"UserIntent 不能超过 {MaxUserIntentLength} 字符。");
        }

        _items = Array.AsReadOnly(copied);
        UserIntent = userIntent;
    }
}
