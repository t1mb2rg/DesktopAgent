using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;

namespace DesktopAgent.WinUI;

public sealed partial class AiAssistantView
{
    private readonly AiDesktopWorkspacePlanningService _workspacePlanningService = new();

    /// <summary>
    /// ADR-0005 新 Workspace Blueprint 路径的只读 Frozen Preview 产品事件。
    /// 它不携带 Candidate / Execution / Writer authority。
    /// </summary>
    internal event Action<DesktopWorkspaceFrozenPreview>?
        WorkspaceFrozenPreviewRequested;
}
