using DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Automation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Geometry;

public sealed class DesktopExplorerGridGeometryEvidenceTests
{
    private static readonly PixelPoint Spacing = new(80, 80);
    private static readonly PixelPoint Phase = new(30, 50);

    [Fact]
    public void From_MapsEachDefinedSourceToItsOwnEvidenceLevel()
    {
        Assert.Equal(
            DesktopGeometryEvidence.Measured,
            DesktopGeometryEvidenceMapping.From(ExplorerGridEvidenceSource.Measured));
        Assert.Equal(
            DesktopGeometryEvidence.Inferred,
            DesktopGeometryEvidenceMapping.From(ExplorerGridEvidenceSource.Inferred));
    }

    [Fact]
    public void NumericCast_WouldTurnInferredIntoMeasured_WhichIsWhyTheExplicitSwitchExists()
    {
        // 两套枚举的数值顺序不同：Source 是 Measured=0 / Inferred=1，
        // Evidence 是 Unknown=0 / Measured=1 / Inferred=2。
        // 显式 switch 存在的唯一理由就是挡住这条 fail-open 的数值转换：
        // 直接 cast 会把"推断"升级成"实测"，反向则把"实测"降级成 Unknown。
        var castOfInferred = (DesktopGeometryEvidence)(int)ExplorerGridEvidenceSource.Inferred;
        Assert.Equal(DesktopGeometryEvidence.Measured, castOfInferred);
        Assert.NotEqual(
            DesktopGeometryEvidenceMapping.From(ExplorerGridEvidenceSource.Inferred),
            castOfInferred);

        var castOfMeasured = (DesktopGeometryEvidence)(int)ExplorerGridEvidenceSource.Measured;
        Assert.Equal(DesktopGeometryEvidence.Unknown, castOfMeasured);
        Assert.NotEqual(
            DesktopGeometryEvidenceMapping.From(ExplorerGridEvidenceSource.Measured),
            castOfMeasured);
    }

    [Fact]
    public void From_NeverReturnsMeasuredForANonMeasuredSource()
    {
        foreach (var source in Enum.GetValues<ExplorerGridEvidenceSource>())
        {
            var evidence = DesktopGeometryEvidenceMapping.From(source);
            var expected = source == ExplorerGridEvidenceSource.Measured
                ? DesktopGeometryEvidence.Measured
                : DesktopGeometryEvidence.Inferred;

            Assert.Equal(expected, evidence);
            if (source != ExplorerGridEvidenceSource.Measured)
            {
                Assert.NotEqual(DesktopGeometryEvidence.Measured, evidence);
            }
        }
    }

    [Fact]
    public void From_UndefinedSourceValue_FailsClosedToUnknown()
    {
        Assert.Equal(
            DesktopGeometryEvidence.Unknown,
            DesktopGeometryEvidenceMapping.From((ExplorerGridEvidenceSource)42));
    }

    [Fact]
    public void FromProvenance_NullOrEmpty_FailsClosedToUnknown()
    {
        Assert.Equal(DesktopGeometryEvidence.Unknown, DesktopExplorerGridGeometryEvidence.FromProvenance(null));
        Assert.Equal(
            DesktopGeometryEvidence.Unknown,
            DesktopExplorerGridGeometryEvidence.FromProvenance([]));
    }

    [Fact]
    public void FromProvenance_AllMeasured_ReturnsMeasured()
    {
        var provenance = new[]
        {
            Entry("DISPLAY1", ExplorerGridEvidenceSource.Measured),
            Entry("DISPLAY2", ExplorerGridEvidenceSource.Measured),
        };

        Assert.Equal(DesktopGeometryEvidence.Measured, DesktopExplorerGridGeometryEvidence.FromProvenance(provenance));
    }

    [Fact]
    public void FromProvenance_AnyInferredAmongMeasured_ReturnsInferred()
    {
        // 只要还有一块显示器是多数投票，整次执行就不能按实测授权。
        var provenance = new[]
        {
            Entry("DISPLAY1", ExplorerGridEvidenceSource.Measured),
            Entry("DISPLAY2", ExplorerGridEvidenceSource.Inferred),
            Entry("DISPLAY3", ExplorerGridEvidenceSource.Measured),
        };

        Assert.Equal(DesktopGeometryEvidence.Inferred, DesktopExplorerGridGeometryEvidence.FromProvenance(provenance));
    }

    [Fact]
    public void FromProvenance_UndefinedSource_FailsClosedToUnknown()
    {
        var provenance = new[] { Entry("DISPLAY1", (ExplorerGridEvidenceSource)42) };

        Assert.Equal(DesktopGeometryEvidence.Unknown, DesktopExplorerGridGeometryEvidence.FromProvenance(provenance));
    }

    [Fact]
    public void FromProvenance_MeasuredPlusUnknown_ReturnsUnknown_BecauseUnknownIsMoreConservativeThanInferred()
    {
        var provenance = new[]
        {
            Entry("DISPLAY1", ExplorerGridEvidenceSource.Measured),
            Entry("DISPLAY2", (ExplorerGridEvidenceSource)42),
        };

        Assert.Equal(DesktopGeometryEvidence.Unknown, DesktopExplorerGridGeometryEvidence.FromProvenance(provenance));
    }

    [Fact]
    public void FromProvenance_InferredPlusUndefined_ReturnsUnknown_UnknownIsTheMoreConservativeLevel()
    {
        // 口径确认：当同一份 provenance 里既有"推断"又有未定义来源时，
        // 结果必须是 Unknown，而不是先命中 Inferred 就返回。
        var provenance = new[]
        {
            Entry("DISPLAY1", ExplorerGridEvidenceSource.Inferred),
            Entry("DISPLAY2", (ExplorerGridEvidenceSource)42),
        };

        Assert.Equal(DesktopGeometryEvidence.Unknown, DesktopExplorerGridGeometryEvidence.FromProvenance(provenance));
    }

    // DesktopExplorerGridProvenance 是纯数据 record，构造时不做校验。
    private static DesktopExplorerGridProvenance Entry(
        string deviceName,
        ExplorerGridEvidenceSource source)
        => new(deviceName, source, Spacing, Phase, 4, 4);
}
