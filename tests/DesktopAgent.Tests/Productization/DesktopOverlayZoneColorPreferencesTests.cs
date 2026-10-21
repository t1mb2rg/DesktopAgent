using DesktopAgent.Core.Productization;

namespace DesktopAgent.Tests.Productization;

public sealed class DesktopOverlayZoneColorPreferencesTests
{
    [Fact]
    public void MissingOverride_InheritsGlobalColor()
    {
        var zoneId = Guid.NewGuid();

        var color = DesktopOverlayZoneColorPreferences.Empty.Resolve(zoneId, 0x0078D4u);

        Assert.Equal(0x0078D4u, color);
    }

    [Fact]
    public void Override_CanBeAddedResolvedAndRemoved()
    {
        var zoneId = Guid.NewGuid();

        var customized = DesktopOverlayZoneColorPreferences.Empty
            .WithOverride(zoneId, 0xFF8A3Du);

        Assert.True(customized.TryGetOverride(zoneId, out var overrideColor));
        Assert.Equal(0xFF8A3Du, overrideColor);
        Assert.Equal(0xFF8A3Du, customized.Resolve(zoneId, 0x0078D4u));

        var inherited = customized.WithoutOverride(zoneId);
        Assert.False(inherited.TryGetOverride(zoneId, out _));
        Assert.Equal(0x0078D4u, inherited.Resolve(zoneId, 0x0078D4u));
    }

    [Fact]
    public void InvalidZoneOrColor_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            DesktopOverlayZoneColorPreferences.Empty.WithOverride(Guid.Empty, 0x0078D4u));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopOverlayZoneColorPreferences.Empty.WithOverride(Guid.NewGuid(), 0x01000000u));
    }

    [Fact]
    public void DuplicateZoneIds_AreRejected()
    {
        var zoneId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            new DesktopOverlayZoneColorPreferences(
            [
                new KeyValuePair<Guid, uint>(zoneId, 0x0078D4u),
                new KeyValuePair<Guid, uint>(zoneId, 0x8A4FFFu),
            ]));
    }
}
