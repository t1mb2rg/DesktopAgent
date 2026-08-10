using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class DesktopZoneLayoutPatternGeometryTests
{
    [Fact]
    public void AllSupportedPatterns_ProduceBoundedNonOverlappingGeometry()
    {
        foreach (var pattern in Enum.GetValues<DesktopZoneLayoutPattern>())
        {
            for (var count = 1; count <= DesktopZoneLayoutPatternGeometry.MaxZoneCount; count++)
            {
                var bounds = DesktopZoneLayoutPatternGeometry.CreateBounds(pattern, count);

                Assert.Equal(count, bounds.Count);
                for (var i = 0; i < bounds.Count; i++)
                {
                    for (var j = i + 1; j < bounds.Count; j++)
                        Assert.False(bounds[i].Overlaps(bounds[j]), $"{pattern}, count={count}, {i} overlaps {j}");
                }
            }
        }

        var primaryRight = DesktopZoneLayoutPatternGeometry.CreateBounds(
            DesktopZoneLayoutPattern.PrimaryRight,
            3);
        Assert.Equal(5_000, primaryRight[0].Left);
        Assert.Equal(10_000, primaryRight[0].Right);
    }
}
