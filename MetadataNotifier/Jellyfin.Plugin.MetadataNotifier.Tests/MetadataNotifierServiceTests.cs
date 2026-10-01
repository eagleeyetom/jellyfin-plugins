using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetadataNotifier.Configuration;
using Jellyfin.Plugin.MetadataNotifier.Services;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MetadataNotifier.Tests;

[TestClass]
public class MetadataNotifierServiceTests
{
    private static SessionInfo CreateSession(string client, string deviceName)
    {
        return new SessionInfo(null!, null!)
        {
            Client = client,
            DeviceName = deviceName
        };
    }

    [TestMethod]
    public void DualDvAndHdr10Plus_OnSamsungTv_ReturnsHdr10Plus()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "hevc",
            Profile = "Main 10",
            DvProfile = 8,
            Hdr10PlusPresentFlag = true,
            ColorTransfer = "smpte2084",
            ColorPrimaries = "bt2020",
            ColorSpace = "bt2020nc"
        };

        var path = "/media/movies/Sample.Movie.2023.2160p.DoVi.HDR10+.DD+.5.1.Atmos.H.265.mkv";
        var name = "Sample Movie";

        var session = CreateSession("Jellyfin for Tizen", "Samsung Smart TV");

        var config = new PluginConfiguration
        {
            ShowHdr10Plus = true,
            ShowHdr10 = true,
            ShowDolbyVision = true,
            SuppressDvOnSamsung = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, path, name, session, config);

        Assert.AreEqual("HDR10+", result.Value);
        Assert.AreEqual("dolby-vision-client-fallback-hdr10plus", result.Rule);
    }

    [TestMethod]
    public void FilenameHdr10Plus_WithoutProbeFlag_ReturnsHdr10Plus()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "hevc",
            Profile = "Main 10",
            DvProfile = 8,
            Hdr10PlusPresentFlag = null,
            ColorTransfer = "smpte2084",
            ColorPrimaries = "bt2020",
            ColorSpace = "bt2020nc"
        };

        var path = "/media/movies/Sample.Movie.2023.2160p.DoVi.HDR10+.DD+.5.1.Atmos.H.265.mkv";
        var name = "Sample Movie";

        var session = CreateSession("Jellyfin for Tizen", "Samsung Smart TV");

        var config = new PluginConfiguration
        {
            ShowHdr10Plus = true,
            ShowHdr10 = true,
            ShowDolbyVision = true,
            SuppressDvOnSamsung = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, path, name, session, config);

        Assert.AreEqual("HDR10+", result.Value);
        Assert.AreEqual("dolby-vision-client-fallback-hdr10plus", result.Rule);
    }

    [TestMethod]
    public void ProbeFlagHdr10Plus_WithoutFilenameTag_ReturnsHdr10Plus()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "hevc",
            Profile = "Main 10",
            DvProfile = 8,
            Hdr10PlusPresentFlag = true,
            ColorTransfer = "smpte2084",
            ColorPrimaries = "bt2020",
            ColorSpace = "bt2020nc"
        };

        var path = "/media/movies/SampleMovie.mkv";
        var name = "Sample Movie";

        var session = CreateSession("Jellyfin for Tizen", "Samsung Smart TV");

        var config = new PluginConfiguration
        {
            ShowHdr10Plus = true,
            ShowHdr10 = true,
            ShowDolbyVision = true,
            SuppressDvOnSamsung = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, path, name, session, config);

        Assert.AreEqual("HDR10+", result.Value);
        Assert.AreEqual("dolby-vision-client-fallback-hdr10plus", result.Rule);
    }

    [TestMethod]
    [DataRow("Sample.Movie.2023.DoVi.HDR10+.DD+.5.1.mkv", true)]
    [DataRow("Sample.Movie.2023.HDR10+.mkv", true)]
    [DataRow("Sample.Movie.2023.HDR10Plus.mkv", true)]
    [DataRow("Sample.Movie.2023.HDR10.Plus.mkv", true)]
    [DataRow("Sample-Movie-2023-HDR10-Plus.mkv", true)]
    [DataRow("Sample_Movie_2023_HDR10+_x265.mkv", true)]
    [DataRow("[HDR10+] Sample Movie (2023).mkv", true)]
    [DataRow("(HDR10Plus) Sample Movie (2023).mkv", true)]
    [DataRow("HDR10+ Sample Movie.mkv", true)]
    [DataRow("Sample Movie HDR10+", true)]
    [DataRow("Sample Movie 2023 HDR10PLUS mkv", true)]
    [DataRow("/media/movies/HDR10+/Sample.Movie.mkv", true)]
    [DataRow(@"C:\Movies\HDR10+\Sample.Movie.mkv", true)]
    [DataRow("/media/movies/HDR10Plus/Sample.Movie.mkv", true)]
    [DataRow("/media/movies/HDR10/Sample.Movie.mkv", false)]
    [DataRow("Sample.Movie.2023.HDR10.DD+.5.1.mkv", false)]
    [DataRow("Sample.Movie.HDR10.mkv", false)]
    [DataRow("Sample Movie HDR10.265", false)]
    public void Hdr10PlusPathRegex_ValidatesPatternCorrectly(string filename, bool shouldMatch)
    {
        var isMatch = MetadataNotifierService.Hdr10PlusPathRegex.IsMatch(filename);
        Assert.AreEqual(shouldMatch, isMatch, $"Failed match check for '{filename}'");
    }

    [TestMethod]
    public void DualDvAndHdr10Plus_OnLgTv_FallsBackToDolbyVision()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "hevc",
            Profile = "Main 10",
            DvProfile = 8,
            Hdr10PlusPresentFlag = true,
            ColorTransfer = "smpte2084",
            ColorPrimaries = "bt2020",
            ColorSpace = "bt2020nc"
        };

        var session = CreateSession("Jellyfin for WebOS", "LG OLED TV");

        var config = new PluginConfiguration
        {
            ShowHdr10Plus = true,
            ShowHdr10 = true,
            ShowDolbyVision = true,
            SuppressHdr10PlusOnLg = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, "/media/movies/SampleMovie.mkv", "Sample Movie", session, config);

        Assert.AreEqual("Dolby Vision", result.Value);
        Assert.AreEqual("hdr10plus-client-fallback-dolby-vision", result.Rule);
    }

    [TestMethod]
    public void RegularHdr10_ReportsHdr10()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "hevc",
            Profile = "Main 10",
            Hdr10PlusPresentFlag = false,
            ColorTransfer = "smpte2084",
            ColorPrimaries = "bt2020",
            ColorSpace = "bt2020nc"
        };

        var session = CreateSession("Jellyfin for Tizen", "Samsung Smart TV");

        var config = new PluginConfiguration
        {
            ShowHdr10Plus = true,
            ShowHdr10 = true,
            ShowDolbyVision = true,
            SuppressDvOnSamsung = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, "/media/movies/SampleMovie.HDR10.mkv", "Sample Movie", session, config);

        Assert.AreEqual("HDR10", result.Value);
        Assert.AreEqual("hdr10", result.Rule);
    }
}
