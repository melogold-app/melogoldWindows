using Melogold.Core.Domain;
using Xunit;

namespace Melogold.Tests;

/// <summary>Устройства Apple (tasks/0006 §3): значки по <c>platform</c> и нормализация кода входа.</summary>
public class DeviceSymbolsTests
{
    [Theory]
    [InlineData("android", DeviceKind.Phone, "")]
    [InlineData("ios", DeviceKind.Phone, "")]
    [InlineData("ipados", DeviceKind.Tablet, "")]
    [InlineData("macos", DeviceKind.Computer, "")]
    [InlineData("windows", DeviceKind.Computer, "")]
    [InlineData("linux", DeviceKind.Computer, "")]
    [InlineData("watchos", DeviceKind.Watch, "")]
    [InlineData("visionos", DeviceKind.Headset, "")]
    [InlineData("WatchOS", DeviceKind.Watch, "")]
    [InlineData("tvos", DeviceKind.Other, "")]
    [InlineData("other", DeviceKind.Other, "")]
    [InlineData(null, DeviceKind.Other, "")]
    public void EveryPlatformHasAnIcon(string? platform, DeviceKind kind, string glyph)
    {
        Assert.Equal(kind, DeviceSymbols.Kind(platform));
        Assert.Equal(glyph, DeviceSymbols.Glyph(platform));
    }

    [Theory]
    [InlineData("k7qx m2pd", "K7QX-M2PD")]
    [InlineData("K7QX-M2PD", "K7QX-M2PD")]
    [InlineData("k7qxm2pd", "K7QX-M2PD")]
    [InlineData(" k7qx_m2pd ", "K7QX-M2PD")]
    // O → 0, I и L → 1
    [InlineData("OIL5-ABCD", "0115-ABCD")]
    [InlineData("oil5abcd", "0115-ABCD")]
    [InlineData("K7QX-M2P", null)]
    [InlineData("K7QX-M2PDX", null)]
    // U нет в алфавите Крокфорда
    [InlineData("K7QX-M2PU", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void UserCodeIsNormalized(string? input, string? expected) => Assert.Equal(expected, UserCode.Normalize(input));
}
