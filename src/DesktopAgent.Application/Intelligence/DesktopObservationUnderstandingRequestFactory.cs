using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 从一次 fresh Observation 建立语义理解请求。真实路径只在本地用于提取扩展名，
/// Provider 仍只接触请求内临时引用和最小语义事实。
/// </summary>
public static class DesktopObservationUnderstandingRequestFactory
{
    public static DesktopFirstRunUnderstandingRequest Create(
        DesktopObservation observation,
        IEnumerable<IntelligenceZoneOption>? existingZones = null,
        string? currentIntentSummary = null,
        string? latestUserGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return new DesktopFirstRunUnderstandingRequest(
            observation.Icons.Select(ToFacts),
            existingZones,
            currentIntentSummary,
            latestUserGuidance);
    }

    private static DesktopItemFacts ToFacts(DesktopIconDescriptor icon)
        => new(
            icon.StableId,
            icon.DisplayName,
            icon.ItemKind,
            TryGetFileExtension(icon),
            icon.IsSystemItem,
            icon.IsFileSystemItem);

    private static string? TryGetFileExtension(DesktopIconDescriptor icon)
    {
        if (!icon.IsFileSystemItem ||
            icon.ItemKind is not (ItemKind.File or ItemKind.Shortcut) ||
            string.IsNullOrWhiteSpace(icon.ParsingName))
        {
            return null;
        }

        try
        {
            var extension = Path.GetExtension(icon.ParsingName);
            return string.IsNullOrEmpty(extension) ? null : extension;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
