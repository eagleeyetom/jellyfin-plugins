// Linked into both plugin test projects, so these run against each plugin's compiled-in copy of
// Shared/MediaDetection.
using Jellyfin.Plugin.Shared.MediaDetection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.Shared.MediaDetection.Tests;

[TestClass]
public class ClientDeviceDetectionTests
{
    [TestMethod]
    [DataRow("Fire TV Stick 4K")]
    [DataRow("Amazon FireTV")]
    [DataRow("Amazon AFTKA")]
    [DataRow("AFTMM")]
    [DataRow("Mozilla/5.0 (Linux; Android 11; AFTKMST Build/RS8244.2081N; wv) AppleWebKit/537.36")]
    [DataRow("Kodi (fire-tv.lan)")]
    [DataRow("fire_tv_livingroom")]
    [DataRow("Fire Stick 4K Max")]
    [DataRow("FireStick")]
    [DataRow("FireOS 7")]
    [DataRow("Living Room Fire")]
    [DataRow("eagleeyetom's Fire TV")]
    public void ContainsFireTvIndicator_FireTvIdentifiers_ReturnsTrue(string value)
    {
        Assert.IsTrue(ClientDeviceDetection.ContainsFireTvIndicator(value), value);
    }

    [TestMethod]
    [DataRow("Firefox")]
    [DataRow("Firefox 131")]
    [DataRow("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/110.0")]
    [DataRow("Mozilla/5.0 (Android 14; Mobile; rv:131.0) Gecko/131.0 Firefox/131.0")]
    [DataRow("Aftermath PC")]
    [DataRow("aftmm")] // model codes are upper-case
    [DataRow("Amazonas Laptop")]
    [DataRow("Mozilla/5.0 (Linux; Android 9; KFTRWI) AppleWebKit/537.36 (KHTML, like Gecko) Silk/120.4.1 like Chrome/120.0 Safari/537.36")] // Fire tablet
    [DataRow("Nvidia Shield")]
    [DataRow("Jellyfin for Android TV")]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void ContainsFireTvIndicator_OtherIdentifiers_ReturnsFalse(string? value)
    {
        Assert.IsFalse(ClientDeviceDetection.ContainsFireTvIndicator(value), value);
    }

    [TestMethod]
    [DataRow("Samsung Smart TV")]
    [DataRow("Jellyfin for Tizen")]
    [DataRow("Mozilla/5.0 (SMART-TV; LINUX; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko) 85.0.4183.93/6.0 TV Safari/537.36")]
    [DataRow("Mozilla/5.0 (SMART-TV; Linux; Tizen 5.0) AppleWebKit/538.1 (KHTML, like Gecko) SamsungBrowser/2.2 TV Safari/538.1")]
    [DataRow("Samsung QN90A")]
    public void ContainsSamsungTvIndicator_SamsungTvIdentifiers_ReturnsTrue(string value)
    {
        Assert.IsTrue(ClientDeviceDetection.ContainsSamsungTvIndicator(value), value);
    }

    [TestMethod]
    [DataRow("Samsung Galaxy S23")]
    [DataRow("Samsung SM-S918B")]
    [DataRow("Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/24.0 Chrome/117.0 Mobile Safari/537.36")]
    [DataRow("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/24.0 Chrome/117.0 Safari/537.36")] // DeX
    [DataRow("Jellyfin Web")]
    [DataRow("Chrome")]
    [DataRow("")]
    [DataRow(null)]
    public void ContainsSamsungTvIndicator_OtherIdentifiers_ReturnsFalse(string? value)
    {
        Assert.IsFalse(ClientDeviceDetection.ContainsSamsungTvIndicator(value), value);
    }
}
