using System.Collections.ObjectModel;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;

public enum DesktopWorkspaceDisplayLoad { Unspecified, Low, Medium, High }

public sealed record DesktopWorkspaceReservedIntent(string Key, string DisplayRef,
    DesktopZonePlacementHint PlacementHint, DesktopZoneSizeHint SizeHint, string Purpose);

/// <summary>累计的语义设计偏好；不包含实际图标坐标、请求临时项目编号或执行权。</summary>
public sealed record DesktopWorkspaceConversationIntent
{
    public string Summary { get; }
    public IReadOnlyDictionary<string, string> CategoryDisplayTargets { get; }
    public IReadOnlyDictionary<string, string> ItemDisplayTargets { get; }
    public IReadOnlyDictionary<string, DesktopWorkspaceDisplayLoad> DisplayLoads { get; }
    public IReadOnlyList<DesktopWorkspaceReservedIntent> ReservedRegions { get; }

    public static DesktopWorkspaceConversationIntent Empty { get; } = new();

    public DesktopWorkspaceConversationIntent(string? summary = null,
        IReadOnlyDictionary<string, string>? categoryDisplayTargets = null,
        IReadOnlyDictionary<string, string>? itemDisplayTargets = null,
        IReadOnlyDictionary<string, DesktopWorkspaceDisplayLoad>? displayLoads = null,
        IEnumerable<DesktopWorkspaceReservedIntent>? reservedRegions = null)
    {
        Summary = summary?.Trim() ?? string.Empty;
        CategoryDisplayTargets = Copy(categoryDisplayTargets);
        ItemDisplayTargets = Copy(itemDisplayTargets);
        var loads = (displayLoads ?? new Dictionary<string, DesktopWorkspaceDisplayLoad>())
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (loads.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !Enum.IsDefined(pair.Value)))
            throw new ArgumentException("显示器负载偏好无效。");
        DisplayLoads = new ReadOnlyDictionary<string, DesktopWorkspaceDisplayLoad>(loads);
        var reserved = reservedRegions?.ToArray() ?? [];
        if (reserved.Any(region => region is null || string.IsNullOrWhiteSpace(region.Key) ||
            string.IsNullOrWhiteSpace(region.DisplayRef) || string.IsNullOrWhiteSpace(region.Purpose) ||
            !Enum.IsDefined(region.PlacementHint) || !Enum.IsDefined(region.SizeHint)) ||
            reserved.Select(region => region.Key).Distinct(StringComparer.Ordinal).Count() != reserved.Length)
            throw new ArgumentException("留白偏好无效或重复。");
        ReservedRegions = Array.AsReadOnly(reserved);
    }

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? source)
    {
        var copy = (source ?? new Dictionary<string, string>()).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (copy.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            throw new ArgumentException("设计偏好不能包含空键或值。");
        return new ReadOnlyDictionary<string, string>(copy);
    }
}
