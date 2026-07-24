using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

public sealed record TargetDisplayLayoutStagingAssignment(
    string StableId,
    PixelPoint StagingPosition,
    int EnterStepIndex,
    int ExitStepIndex);
