using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Geometry;

public sealed class ExplorerGridMeasurementTests
{
    private const string Fingerprint = "topology-a";

    [Fact]
    public void MeasuredFact_CanAuthorizeExecution_WhenBoundToCurrentTopology()
    {
        var fact = Measured(
            ExplorerGridEvidenceSource.Measured,
            sampleCount: 6,
            consensusCount: 6,
            spacingConstant: true,
            phaseConstant: true);

        Assert.True(fact.CanAuthorizeExecution(Fingerprint));
    }

    [Fact]
    public void MeasuredFact_CannotAuthorizeExecution_WhenTopologyChanged()
    {
        var fact = Measured(
            ExplorerGridEvidenceSource.Measured,
            sampleCount: 6,
            consensusCount: 6,
            spacingConstant: true,
            phaseConstant: true);

        // 缩放、任务栏或显示器排列变化后，陈旧实测比推断更危险。
        Assert.False(fact.CanAuthorizeExecution("topology-b"));
        Assert.False(fact.MatchesTopology("topology-b"));
    }

    [Fact]
    public void InferredFact_CanNeverAuthorizeExecution_EvenWithStrongConsensus()
    {
        var fact = Measured(
            ExplorerGridEvidenceSource.Inferred,
            sampleCount: 20,
            consensusCount: 20,
            spacingConstant: true,
            phaseConstant: true);

        Assert.Equal(ExplorerGridEvidenceSource.Inferred, fact.Source);
        Assert.False(fact.CanAuthorizeExecution(Fingerprint));
    }

    [Fact]
    public void MeasuredClaim_WithTooFewSamples_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => Measured(
            ExplorerGridEvidenceSource.Measured,
            sampleCount: 2,
            consensusCount: 2,
            spacingConstant: true,
            phaseConstant: true));

        Assert.Contains("至少需要", exception.Message);
    }

    [Fact]
    public void MeasuredClaim_WithDisagreeingSample_IsRejected()
    {
        // 任何不一致的样本都意味着这不是实测，而是投票。
        Assert.Throws<ArgumentException>(() => Measured(
            ExplorerGridEvidenceSource.Measured,
            sampleCount: 6,
            consensusCount: 5,
            spacingConstant: true,
            phaseConstant: true));
    }

    [Fact]
    public void MeasuredClaim_WithNonConstantSpacingOrPhase_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Measured(
            ExplorerGridEvidenceSource.Measured,
            sampleCount: 6,
            consensusCount: 6,
            spacingConstant: true,
            phaseConstant: false));
    }

    [Fact]
    public void InferredFact_WithWeakConsensus_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Measured(
            ExplorerGridEvidenceSource.Inferred,
            sampleCount: 10,
            consensusCount: 5,
            spacingConstant: false,
            phaseConstant: false));
    }

    [Fact]
    public void Phase_OutsideSpacingResidueRange_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Measured(
            ExplorerGridEvidenceSource.Measured,
            sampleCount: 6,
            consensusCount: 6,
            spacingConstant: true,
            phaseConstant: true,
            phase: new PixelPoint(93, 0)));
    }

    private static ExplorerGridMeasurement Measured(
        ExplorerGridEvidenceSource source,
        int sampleCount,
        int consensusCount,
        bool spacingConstant,
        bool phaseConstant,
        PixelPoint? phase = null)
        => new(
            source,
            "DISPLAY1",
            new PixelPoint(93, 122),
            phase ?? new PixelPoint(16, 2),
            sampleCount,
            consensusCount,
            spacingConstant,
            phaseConstant,
            Fingerprint,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));
}
