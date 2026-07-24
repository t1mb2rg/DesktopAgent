using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

public sealed record TargetDisplayLayoutExecutionStep(
    int StepIndex,
    string StableId,
    string DisplayName,
    PixelPoint FromPosition,
    PixelPoint ToPosition,
    TargetDisplayLayoutExecutionStepKind Kind,
    string ExpectedMonitorDeviceName,
    int? StagingSequence = null,
    IReadOnlyList<string>? RelatedCycleStableIds = null);
