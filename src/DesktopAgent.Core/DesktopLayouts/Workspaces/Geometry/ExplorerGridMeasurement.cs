using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

/// <summary>
/// 网格几何事实的证据来源。
///
/// Measured 只能来自真实机 Physical Gate：位置是主动写入后读回、或由稳定双采样确认的
/// 实际落点。Inferred 是降级路径，只描述"多数样本看起来像这样"，
/// 按已确认的验收标准不得单独授权执行。
/// </summary>
public enum ExplorerGridEvidenceSource
{
    Measured = 0,
    Inferred = 1,
}

/// <summary>
/// 一块显示器在当前 Explorer 环境中的网格事实：spacing、phase，以及它是怎么得到的。
///
/// 这个类型存在的唯一理由是让"实测"和"推断"在代码里可区分。
/// 在此之前，Geometry Adapter 产出的 phase 无法说明来源，策略层也就无法拒绝
/// 由多数投票得出的几何。
///
/// 事实是环境绑定的：它必须绑定产生它时的 topology fingerprint。
/// 拓扑、缩放或任务栏变化后，一条陈旧的实测记录比推断更危险，
/// 因为它是"自信地错"。
/// </summary>
public sealed record ExplorerGridMeasurement
{
    /// <summary>
    /// Physical Gate 要求的每轴最小主动样本数。与 Diagnostics 探针的
    /// PreferredMinimumActiveSamplesPerAxis 保持一致。
    /// </summary>
    public const int MinimumMeasuredActiveSamplesPerAxis = 3;

    /// <summary>
    /// 推断路径的最低共识比例。仅在无法实测时生效，且不构成执行授权。
    /// </summary>
    public const double MinimumInferredConsensusRatio = 0.60d;

    public ExplorerGridEvidenceSource Source { get; }
    public string DeviceName { get; }
    public PixelPoint Spacing { get; }
    public PixelPoint Phase { get; }

    /// <summary>
    /// 参与得出该事实的样本数。Measured 时必须是主动落点样本。
    /// </summary>
    public int SampleCount { get; }

    /// <summary>
    /// 与 Phase 一致的样本数。Measured 时必须等于 SampleCount。
    /// </summary>
    public int ConsensusCount { get; }

    public bool SpacingConstant { get; }
    public bool PhaseConstant { get; }

    /// <summary>
    /// 产生该事实时的 topology fingerprint。事实只在它匹配时有效。
    /// </summary>
    public string TopologyFingerprint { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public ExplorerGridMeasurement(
        ExplorerGridEvidenceSource source,
        string deviceName,
        PixelPoint spacing,
        PixelPoint phase,
        int sampleCount,
        int consensusCount,
        bool spacingConstant,
        bool phaseConstant,
        string topologyFingerprint,
        DateTimeOffset observedAtUtc)
    {
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source), "Evidence Source 无效。");
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("DeviceName 不能为空。", nameof(deviceName));
        if (string.IsNullOrWhiteSpace(topologyFingerprint))
            throw new ArgumentException("TopologyFingerprint 不能为空。", nameof(topologyFingerprint));
        if (spacing.X <= 0 || spacing.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing 必须为正数。");
        if (phase.X < 0 || phase.X >= spacing.X || phase.Y < 0 || phase.Y >= spacing.Y)
            throw new ArgumentOutOfRangeException(
                nameof(phase),
                "Phase 必须位于各自 spacing 的余数范围内。");
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ObservedAtUtc 必须是 UTC。", nameof(observedAtUtc));
        if (sampleCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "样本数必须大于 0。");
        if (consensusCount < 0 || consensusCount > sampleCount)
            throw new ArgumentOutOfRangeException(
                nameof(consensusCount),
                "ConsensusCount 必须位于 0 与 SampleCount 之间。");

        if (source == ExplorerGridEvidenceSource.Measured)
        {
            // 实测事实必须自带证据。达不到 Physical Gate 的"实测"不能被称为实测。
            if (sampleCount < MinimumMeasuredActiveSamplesPerAxis)
                throw new ArgumentException(
                    $"Measured 事实至少需要 {MinimumMeasuredActiveSamplesPerAxis} 个主动样本。",
                    nameof(sampleCount));
            if (consensusCount != sampleCount)
                throw new ArgumentException(
                    "Measured 事实要求全部样本与 Phase 一致；任何不一致都不是实测。",
                    nameof(consensusCount));
            if (!spacingConstant || !phaseConstant)
                throw new ArgumentException(
                    "Measured 事实要求 spacing 与 phase 恒定。",
                    nameof(spacingConstant));
        }
        else
        {
            // 推断是降级路径：允许弱证据，但必须被明确标注为推断。
            if (sampleCount < 2)
                throw new ArgumentException(
                    "Inferred 事实至少需要 2 个样本。",
                    nameof(sampleCount));
            if (consensusCount < Math.Max(2, (int)Math.Ceiling(sampleCount * MinimumInferredConsensusRatio)))
                throw new ArgumentException(
                    $"Inferred 事实至少需要 {MinimumInferredConsensusRatio:P0} 共识。",
                    nameof(consensusCount));
        }

        Source = source;
        DeviceName = deviceName.Trim();
        Spacing = spacing;
        Phase = phase;
        SampleCount = sampleCount;
        ConsensusCount = consensusCount;
        SpacingConstant = spacingConstant;
        PhaseConstant = phaseConstant;
        TopologyFingerprint = topologyFingerprint.Trim();
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>
    /// 该事实是否仍然属于当前拓扑。不匹配即作废，只能显式降级或重新实测。
    /// </summary>
    public bool MatchesTopology(string currentTopologyFingerprint)
        => !string.IsNullOrWhiteSpace(currentTopologyFingerprint) &&
           string.Equals(TopologyFingerprint, currentTopologyFingerprint, StringComparison.Ordinal);

    /// <summary>
    /// 执行授权判定：只有绑定当前拓扑的实测事实可以授权。
    /// 多数投票永远不能授权执行，这是已确认验收标准在代码中的落点。
    /// </summary>
    public bool CanAuthorizeExecution(string currentTopologyFingerprint)
        => Source == ExplorerGridEvidenceSource.Measured && MatchesTopology(currentTopologyFingerprint);
}
