// Linked into both plugin test projects, so these run against each plugin's compiled-in copy of
// Shared/MediaDetection.
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Shared.MediaDetection;
using MediaBrowser.Model.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.Shared.MediaDetection.Tests;

[TestClass]
public class HdrFormatDetectionTests
{
    [TestMethod]
    [DataRow("/movies/Movie (2020)/Movie (2020).mkv", "Movie (2020).mkv", "Movie (2020)")]
    [DataRow(@"D:\Movies\Movie.2020.DV\movie.mkv", "movie.mkv", "Movie.2020.DV")]
    [DataRow(@"\\nas\media/Mixed\Separators/file.mkv", "file.mkv", "Separators")]
    [DataRow("/movies/Movie.2020.DV.HDR10/", "Movie.2020.DV.HDR10", "movies")]
    [DataRow(@"C:\Movie.mkv", "Movie.mkv", "")]
    [DataRow("Movie.mkv", "Movie.mkv", "")]
    [DataRow("/home/user/Recording.dv", "Recording", "user")]
    [DataRow("", "", "")]
    [DataRow(null, "", "")]
    public void GetPathNames_SplitsFileNameAndParentFolder(string? path, string expectedFileName, string expectedParent)
    {
        var (fileName, parentFolder) = HdrFormatDetection.GetPathNames(path);

        Assert.AreEqual(expectedFileName, fileName);
        Assert.AreEqual(expectedParent, parentFolder);
    }

    [TestMethod]
    [DataRow("Movie.2023.2160p.UHD.BluRay.x265.DV.HDR.mkv")]
    [DataRow("/movies/Movie [DOVI].mkv")]
    [DataRow("/movies/Movie Dolby Vision.mkv")]
    [DataRow("/movies/Movie.Dolby-Vision.mkv")]
    [DataRow("/downloads/Movie.2020.2160p.DV.HDR10-GRP/grp-movie.mkv")] // parent folder fallback
    [DataRow(@"C:\Downloads\Movie (2020) [DV]\movie.mkv")]
    public void PathIndicatesDolbyVision_TagInFileNameOrParentFolder_ReturnsTrue(string path)
    {
        Assert.IsTrue(HdrFormatDetection.PathIndicatesDolbyVision(path), path);
    }

    [TestMethod]
    [DataRow("/movies/My.Great.Adventure.1998.DVD.mkv")]
    [DataRow("/movies/Recording.DVB-T.ts")]
    [DataRow("/movies/Classic.DVDRip.x264.mkv")]
    [DataRow("/home/user/Camcorder/Holiday.dv")] // raw DV camcorder file, not Dolby Vision
    [DataRow("/media/DV/Movies/Movie (2020)/Movie (2020).mkv")] // grandparent folders are ignored
    [DataRow(@"D:\Dolby Vision Rips\Collection\Movie (2020)\Movie (2020).mkv")]
    [DataRow("")]
    [DataRow(null)]
    public void PathIndicatesDolbyVision_NoTagInFileNameOrParentFolder_ReturnsFalse(string? path)
    {
        Assert.IsFalse(HdrFormatDetection.PathIndicatesDolbyVision(path), path);
    }

    [TestMethod]
    [DataRow("/movies/Movie.2022.HDR10+.mkv")]
    [DataRow("/movies/Movie.2022.HDR10Plus.mkv")]
    [DataRow("/movies/Movie.2022.HDR10-Plus.mkv")]
    [DataRow("/movies/Movie.2022.HDR10_Plus.mkv")]
    [DataRow("/movies/Movie.2022.HDR10.Plus.mkv")]
    [DataRow(@"C:\Movies\HDR10+\Sample.Movie.mkv")] // parent folder fallback
    public void PathIndicatesHdr10Plus_TagInFileNameOrParentFolder_ReturnsTrue(string path)
    {
        Assert.IsTrue(HdrFormatDetection.PathIndicatesHdr10Plus(path), path);
    }

    [TestMethod]
    [DataRow("/movies/Movie.2022.HDR10.mkv")]
    [DataRow("/media/HDR10+ Collection/Films/Movie (2022)/Movie (2022).mkv")] // grandparent folders are ignored
    [DataRow("")]
    [DataRow(null)]
    public void PathIndicatesHdr10Plus_NoTagInFileNameOrParentFolder_ReturnsFalse(string? path)
    {
        Assert.IsFalse(HdrFormatDetection.PathIndicatesHdr10Plus(path), path);
    }

    [TestMethod]
    public void GetFileNameForDetection_IgnoresHdrFolderNames()
    {
        var fileName = HdrFormatDetection.GetFileNameForDetection("/media/4K HDR/Movie (2020)/Movie (2020).mkv");

        Assert.AreEqual("Movie (2020).mkv", fileName);
        Assert.IsFalse(fileName.Contains("HDR", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void IsDolbyVision_SdrStreamInDolbyVisionLibraryFolder_ReturnsFalse()
    {
        var stream = new MediaStream { Type = MediaStreamType.Video };

        Assert.IsFalse(HdrFormatDetection.IsDolbyVision(stream, "/media/Dolby Vision/Movies/Movie (2020)/Movie (2020).mkv", "Movie"));
    }

    [TestMethod]
    public void IsHdr10Plus_SdrStreamInHdr10PlusLibraryFolder_ReturnsFalse()
    {
        var stream = new MediaStream { Type = MediaStreamType.Video };

        Assert.IsFalse(HdrFormatDetection.IsHdr10Plus(stream, "/media/HDR10+/Movies/Movie (2020)/Movie (2020).mkv", "Movie"));
    }

    [TestMethod]
    public void IsDolbyVision_ProbeResultsWinOverPath()
    {
        Assert.IsTrue(HdrFormatDetection.IsDolbyVision(new MediaStream { DvProfile = 8 }, "/movies/Movie.mkv", "Movie"));
        Assert.IsTrue(HdrFormatDetection.IsDolbyVision(VideoRangeType.DOVIWithHDR10, null, null, null, null, string.Empty, string.Empty));
        Assert.IsFalse(HdrFormatDetection.IsDolbyVision(new MediaStream { DvProfile = 0 }, "/movies/Movie.mkv", "Movie"));
    }

    [TestMethod]
    [DataRow("Main 10 (Dolby Vision)", "", "")]
    [DataRow("", "", "4K DoVi/HDR10")]
    [DataRow("", "DOVIWithHDR10", "")]
    public void StreamMetadataIndicatesDolbyVision_ReturnsTrue(string profile, string title, string displayTitle)
    {
        Assert.IsTrue(HdrFormatDetection.StreamMetadataIndicatesDolbyVision(profile, title, displayTitle));
    }

    [TestMethod]
    public void StreamMetadataIndicatesDolbyVision_DvdTitle_ReturnsFalse()
    {
        Assert.IsFalse(HdrFormatDetection.StreamMetadataIndicatesDolbyVision("Main", "DVD Video", "480p MPEG-2"));
    }

    [TestMethod]
    public void IsHdr10Plus_ProbeFlagAndStreamMetadata()
    {
        Assert.IsTrue(HdrFormatDetection.IsHdr10Plus(new MediaStream { Hdr10PlusPresentFlag = true }, string.Empty, string.Empty));
        Assert.IsTrue(HdrFormatDetection.IsHdr10Plus(VideoRangeType.DOVIWithELHDR10Plus, null, null, null, null, null, string.Empty, string.Empty));
        Assert.IsTrue(HdrFormatDetection.IsHdr10Plus(new MediaStream { Comment = "SMPTE ST 2094-40" }, string.Empty, string.Empty));
        Assert.IsFalse(HdrFormatDetection.IsHdr10Plus(VideoRangeType.HDR10, false, "Main 10", null, "4K HDR10", null, "/movies/Movie.HDR10.mkv", "Movie"));
        Assert.IsFalse(HdrFormatDetection.IsHdr10Plus(new MediaStream { ColorTransfer = "smpte2084" }, "/movies/Movie.HDR10.mkv", "Movie"));
    }

    [TestMethod]
    public void NullStream_ReturnsFalse()
    {
        Assert.IsFalse(HdrFormatDetection.IsDolbyVision(null, "/movies/Movie.DV.mkv", "Movie"));
        Assert.IsFalse(HdrFormatDetection.IsHdr10Plus(null, "/movies/Movie.HDR10+.mkv", "Movie"));
    }
}
