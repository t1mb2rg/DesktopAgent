using System.Text.Json;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Productization;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
namespace DesktopAgent.WinUI;

// Presentation only: never supplies execution or calibration evidence.
internal sealed record AppliedWorkspaceFrame(string DeviceName, string Name,
    int DisplayLeft, int DisplayTop, int DisplayRight, int DisplayBottom,
    int Left, int Top, int Right, int Bottom);
internal sealed record AppliedWorkspacePresentation(string Title, int Revision, AppliedWorkspaceFrame[] Frames)
{
    private static string FilePath => Path.Combine(Path.GetDirectoryName(DesktopAgentDataPaths.DefaultDatabasePath)!, "applied-workspace-presentation.json");
    internal static async Task SaveAsync(DesktopWorkspaceFrozenPreview preview)
    {
        var frames = preview.Displays.SelectMany(display => display.Zones.Select(zone =>
        {
            var rect = ExplorerGridPresentation.FrameBounds(zone.NativeRect, display.GridGeometry);
            var bounds = display.PixelBounds;
            return new AppliedWorkspaceFrame(display.DeviceName, zone.Name, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
                rect.Left, rect.Top, rect.Right, rect.Bottom);
        })).ToArray();
        var value = new AppliedWorkspacePresentation(preview.Blueprint.DesignSummary, preview.Blueprint.Revision, frames);
        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value)); File.Move(temp, FilePath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static async Task<AppliedWorkspacePresentation?> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 2 * 1024 * 1024) return null;
            var value = JsonSerializer.Deserialize<AppliedWorkspacePresentation>(await File.ReadAllTextAsync(FilePath));
            return value is { Revision: > 0, Frames.Length: > 0 and <= 1000 } &&
                value.Frames.All(f => f is not null && f.Right > f.Left && f.Bottom > f.Top &&
                    f.Left >= f.DisplayLeft && f.Top >= f.DisplayTop && f.Right <= f.DisplayRight && f.Bottom <= f.DisplayBottom) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
    internal bool Matches(DesktopRadarSnapshot snapshot) => Frames.All(frame => snapshot.Displays.Any(display =>
        display.DisplayKey == frame.DeviceName && display.Bounds.Left == frame.DisplayLeft && display.Bounds.Top == frame.DisplayTop &&
        display.Bounds.Right == frame.DisplayRight && display.Bounds.Bottom == frame.DisplayBottom));

}
