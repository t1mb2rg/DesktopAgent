namespace DesktopAgent.Application.Intelligence;

/// <summary>首次理解阶段的一次临时 item_ref → Zone proposal 归属。</summary>
public sealed class DesktopUnderstandingItemAssignment
{
    public string ItemRef { get; }
    public string ZoneKey { get; }

    public DesktopUnderstandingItemAssignment(string itemRef, string zoneKey)
    {
        if (!IsValidItemRef(itemRef))
            throw new ArgumentException("ItemRef 必须使用 i1、i2 ... 形式的临时引用。", nameof(itemRef));
        if (!DesktopUnderstandingZoneProposal.IsValidKey(zoneKey))
            throw new ArgumentException("ZoneKey 格式无效。", nameof(zoneKey));

        ItemRef = itemRef;
        ZoneKey = zoneKey;
    }

    internal static bool IsValidItemRef(string? value)
        => value is { Length: >= 2 and <= 8 } &&
           value[0] == 'i' &&
           value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 &&
           value[1] != '0';
}
