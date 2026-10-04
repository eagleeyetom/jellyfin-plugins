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

    [TestMethod]
    public void GetHdrInfo_Profile5DolbyVision_OnSamsung_DoesNotFallbackToHdr10()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "hevc",
            DvProfile = 5,
            Profile = "Profile 5",
            ColorTransfer = "smpte2084",
            ColorSpace = "bt2020nc"
        };

        var session = CreateSession("Jellyfin for Tizen", "Samsung Smart TV");
        var config = new PluginConfiguration
        {
            ShowHdr10Plus = true,
            ShowHdr10 = true,
            ShowDolbyVision = true,
            SuppressDvOnSamsung = true,
            ShowSdr = false
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, "/media/movies/Sample.Movie.mkv", "Sample Movie", session, config);

        Assert.AreEqual(string.Empty, result.Value);
        Assert.AreEqual("dolby-vision-client-fallback-hidden", result.Rule);
        Assert.IsFalse(MetadataNotifierService.HasHdr10BaseLayer(videoStream));
    }

    [TestMethod]
    public void GetHdrInfo_Smpte2084ColorTransfer_DetectedAsHdr10()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            ColorTransfer = "smpte2084",
            ColorSpace = "bt2020nc"
        };

        var session = CreateSession("Jellyfin Web", "Firefox");
        var config = new PluginConfiguration
        {
            ShowHdr10 = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, "/media/movies/Sample.Movie.mkv", "Sample Movie", session, config);

        Assert.AreEqual("HDR10", result.Value);
        Assert.AreEqual("hdr10", result.Rule);
    }

    [TestMethod]
    public void GetHdrInfo_AribStdB67ColorTransfer_DetectedAsHlg()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            ColorTransfer = "arib-std-b67"
        };

        var session = CreateSession("Jellyfin Web", "Firefox");
        var config = new PluginConfiguration
        {
            ShowHlg = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, "/media/movies/Sample.Movie.mkv", "Sample Movie", session, config);

        Assert.AreEqual("HLG", result.Value);
        Assert.AreEqual("hlg", result.Rule);
    }

    [TestMethod]
    public void FireTv_IdentifiedCorrectly()
    {
        Assert.IsTrue(MetadataNotifierService.IsFireTvClient(CreateSession("Jellyfin for Android TV", "Fire TV Stick 4K")));
        Assert.IsTrue(MetadataNotifierService.IsFireTvClient(CreateSession("Jellyfin for Android TV", "AFTMM")));
        Assert.IsTrue(MetadataNotifierService.IsFireTvClient(CreateSession("Fire TV Client", "Living Room")));
        Assert.IsTrue(MetadataNotifierService.IsFireTvClient(CreateSession("Android TV", "Amazon FireTV")));
        Assert.IsFalse(MetadataNotifierService.IsFireTvClient(CreateSession("Jellyfin for Android TV", "Nvidia Shield")));
    }

    [TestMethod]
    public void FireTv_IsNotNonHdr10PlusClient()
    {
        var fireTvSession = CreateSession("Jellyfin for Android TV", "Fire TV Stick 4K");
        var aftSession = CreateSession("Jellyfin for Android TV", "AFTMM");

        Assert.IsFalse(MetadataNotifierService.IsNonHdr10PlusClient(fireTvSession));
        Assert.IsFalse(MetadataNotifierService.IsNonHdr10PlusClient(aftSession));
    }

    [TestMethod]
    public void FireTv_Hdr10PlusMedia_ReportsHdr10Plus_EvenWhenSuppressHdr10PlusOnLgEnabled()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "hevc",
            Profile = "Main 10",
            Hdr10PlusPresentFlag = true,
            ColorTransfer = "smpte2084",
            ColorPrimaries = "bt2020",
            ColorSpace = "bt2020nc"
        };

        var session = CreateSession("Jellyfin for Android TV", "Fire TV Stick 4K Max");
        var config = new PluginConfiguration
        {
            ShowHdr10Plus = true,
            ShowHdr10 = true,
            SuppressHdr10PlusOnLg = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, "/media/movies/Movie.mkv", "Movie", session, config);

        Assert.AreEqual("HDR10+", result.Value);
        Assert.AreEqual("hdr10plus", result.Rule);
    }

    [TestMethod]
    public void FireTv_DualDvAndHdr10Plus_WhenSuppressDvOnFireTvDisabled_ReportsDolbyVision()
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

        var session = CreateSession("Jellyfin for Android TV", "Fire TV Stick 4K Max");
        var config = new PluginConfiguration
        {
            ShowDolbyVision = true,
            ShowHdr10Plus = true,
            ShowHdr10 = true,
            SuppressDvOnSamsung = true,
            SuppressDvOnFireTv = false
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, "/media/movies/Movie.mkv", "Movie", session, config);

        Assert.AreEqual("Dolby Vision", result.Value);
        Assert.AreEqual("dolby-vision", result.Rule);
    }

    [TestMethod]
    public void FireTv_DualDvAndHdr10Plus_WhenSuppressDvOnFireTvEnabled_FallsBackToHdr10Plus()
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

        var session = CreateSession("Jellyfin for Android TV", "Fire TV Stick 4K Max");
        var config = new PluginConfiguration
        {
            ShowDolbyVision = true,
            ShowHdr10Plus = true,
            ShowHdr10 = true,
            SuppressDvOnSamsung = true,
            SuppressDvOnFireTv = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, "/media/movies/Movie.mkv", "Movie", session, config);

        Assert.AreEqual("HDR10+", result.Value);
        Assert.AreEqual("dolby-vision-client-fallback-hdr10plus", result.Rule);
    }

    [TestMethod]
    public void FireTv_DvProfile8Only_WhenSuppressDvOnFireTvEnabled_FallsBackToHdr10()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "hevc",
            Profile = "Main 10",
            DvProfile = 8,
            Hdr10PlusPresentFlag = false,
            ColorTransfer = "smpte2084",
            ColorPrimaries = "bt2020",
            ColorSpace = "bt2020nc"
        };

        var session = CreateSession("Jellyfin for Android TV", "Fire TV Stick 4K");
        var config = new PluginConfiguration
        {
            ShowDolbyVision = true,
            ShowHdr10Plus = true,
            ShowHdr10 = true,
            SuppressDvOnSamsung = true,
            SuppressDvOnFireTv = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, "/media/movies/Movie.mkv", "Movie", session, config);

        Assert.AreEqual("HDR10", result.Value);
        Assert.AreEqual("dolby-vision-client-fallback-hdr10", result.Rule);
    }

    [TestMethod]
    public void NullMediaStream_GuardsDoNotThrow()
    {
        Assert.IsFalse(MetadataNotifierService.IsDolbyVision(null!, string.Empty, string.Empty));
        Assert.IsFalse(MetadataNotifierService.IsHdr10Plus(null!, string.Empty, string.Empty));
        Assert.IsFalse(MetadataNotifierService.HasHdr10BaseLayer(null!));
    }

    [TestMethod]
    public void NonHdr10PlusClient_LgWordBoundaryCheck()
    {
        // Whole word "LG" matches
        Assert.IsTrue(MetadataNotifierService.IsNonHdr10PlusClient(CreateSession("Jellyfin Web", "LG TV")));
        Assert.IsTrue(MetadataNotifierService.IsNonHdr10PlusClient(CreateSession("LG webOS", "Living Room TV")));

        // Substrings containing "lg" must not match
        Assert.IsFalse(MetadataNotifierService.IsNonHdr10PlusClient(CreateSession("Jellyfin Web", "Olga's Phone")));
        Assert.IsFalse(MetadataNotifierService.IsNonHdr10PlusClient(CreateSession("Belgium Browser", "Desktop")));
    }

    [TestMethod]
    public void DetailedDolbyVision_DoesNotFalseTriggerOnUnrelatedNumbers()
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "hevc",
            Profile = "4K HEVC 8.1 Mbps bitrate",
            DvProfile = null
        };

        var session = CreateSession("Jellyfin Web", "PC");
        var config = new PluginConfiguration
        {
            ShowDolbyVision = true,
            UseDetailedVideoNames = true
        };

        var result = MetadataNotifierService.GetHdrInfo(videoStream, "/media/movies/Movie.DV.mkv", "Movie", session, config);

        // Should NOT false-trigger on "8.1" to return "DV Profile 8.1"
        Assert.AreNotEqual("DV Profile 8.1", result.Value);
        Assert.AreEqual("Dolby Vision (4K HEVC 8.1 Mbps bitrate)", result.Value);
    }
}

