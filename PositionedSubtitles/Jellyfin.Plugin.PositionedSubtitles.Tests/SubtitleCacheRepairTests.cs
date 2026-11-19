using Jellyfin.Plugin.PositionedSubtitles.Services;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.PositionedSubtitles.Tests;

[TestClass]
public sealed class SubtitleCacheRepairTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup() => _root = Directory.CreateTempSubdirectory("posubs-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    /// <summary>Streams as Jellyfin numbers them: external subtitles first, then video, audio and embedded subtitles.</summary>
    private static List<MediaStream> Streams(int external, int embeddedSubs)
    {
        var list = new List<MediaStream>();
        int i = 0;
        for (int e = 0; e < external; e++)
        {
            list.Add(new MediaStream { Index = i++, Type = MediaStreamType.Subtitle, IsExternal = true, Codec = "ass" });
        }

        list.Add(new MediaStream { Index = i++, Type = MediaStreamType.Video });
        list.Add(new MediaStream { Index = i++, Type = MediaStreamType.Audio });
        for (int s = 0; s < embeddedSubs; s++)
        {
            list.Add(new MediaStream { Index = i++, Type = MediaStreamType.Subtitle, Codec = "subrip" });
        }

        return list;
    }

    private string MakeCache(string name, IEnumerable<int> indices, DateTime? mtime = null)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (int i in indices)
        {
            string f = Path.Combine(dir, $"{i}.srt");
            File.WriteAllText(f, $"ff{i}");
            File.WriteAllText(f + ".meta", "m");
            if (mtime is DateTime t)
            {
                File.SetLastWriteTimeUtc(f, t);
            }
        }

        return dir;
    }

    private static string[] Names(string dir) => Directory.GetFiles(dir)
        .Select(Path.GetFileName)
        .Where(n => !n!.EndsWith(".meta", StringComparison.Ordinal) && !n.StartsWith('.'))
        .OrderBy(n => int.Parse(n!.Split('.')[0]))
        .ToArray()!;

    private static SubtitleCacheRepair Repair(bool dryRun = false) => new(NullLogger.Instance, dryRun);

    [TestMethod]
    public void ExternalFileAddedAfterExtraction_ShiftsCacheUp()
    {
        // Extracted with no external files (ffmpeg subtitle indices 2..33), now one external file exists.
        string dir = MakeCache("bb", Enumerable.Range(2, 32));
        var repair = Repair();

        Assert.AreEqual(CacheState.Shifted, repair.Repair(dir, Streams(1, 32), "bb"));
        CollectionAssert.AreEqual(Enumerable.Range(3, 32).Select(i => $"{i}.srt").ToArray(), Names(dir));
        Assert.AreEqual("ff2", File.ReadAllText(Path.Combine(dir, "3.srt")));
        Assert.AreEqual("ff33", File.ReadAllText(Path.Combine(dir, "34.srt")));
        Assert.IsTrue(File.Exists(Path.Combine(dir, "34.srt.meta")));
        Assert.IsFalse(File.Exists(Path.Combine(dir, "2.srt.meta")));
        Assert.AreEqual(CacheState.Consistent, repair.Repair(dir, Streams(1, 32), "bb"));
    }

    [TestMethod]
    public void ConsistentCache_IsLeftAlone()
    {
        string none = MakeCache("ok", Enumerable.Range(2, 5));
        Assert.AreEqual(CacheState.Consistent, Repair().Repair(none, Streams(0, 5), "ok"));
        Assert.AreEqual(5, Names(none).Length);
        Assert.IsTrue(File.Exists(Path.Combine(none, SubtitleCacheRepair.StateFileName)));

        string two = MakeCache("ext2", Enumerable.Range(4, 3));
        Assert.AreEqual(CacheState.Consistent, Repair().Repair(two, Streams(2, 3), "ext2"));
        Assert.AreEqual("ff4", File.ReadAllText(Path.Combine(two, "4.srt")));
    }

    [TestMethod]
    public void RecordedOffset_KeepsFilesWrittenLaterAtTheirIndex()
    {
        string dir = MakeCache("state", Enumerable.Range(2, 4), DateTime.UtcNow.AddHours(-2));
        var repair = Repair();
        repair.WriteState(dir, 0);
        File.SetLastWriteTimeUtc(Path.Combine(dir, SubtitleCacheRepair.StateFileName), DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(dir, "6.srt"), "new6"); // written by Jellyfin after the external file appeared

        Assert.AreEqual(CacheState.Shifted, repair.Repair(dir, Streams(1, 4), "state"));
        CollectionAssert.AreEqual(new[] { "3.srt", "4.srt", "5.srt", "6.srt" }, Names(dir));
        Assert.AreEqual("new6", File.ReadAllText(Path.Combine(dir, "6.srt")));
        Assert.AreEqual("ff2", File.ReadAllText(Path.Combine(dir, "3.srt")));
    }

    [TestMethod]
    public void ShiftAll_BeforeRefresh_ThenConsistent()
    {
        string dir = MakeCache("plug", Enumerable.Range(2, 3));
        var repair = Repair();
        repair.Repair(dir, Streams(0, 3), "plug");
        repair.ShiftAll(dir, 1, 1);

        CollectionAssert.AreEqual(new[] { "3.srt", "4.srt", "5.srt" }, Names(dir));
        Assert.AreEqual("ff2", File.ReadAllText(Path.Combine(dir, "3.srt")));
        Assert.AreEqual(CacheState.Consistent, repair.Repair(dir, Streams(1, 3), "plug"));
    }

    [TestMethod]
    public void ExternalFileRemoved_ShiftsCacheDown()
    {
        string dir = MakeCache("removed", Enumerable.Range(3, 3));
        Assert.AreEqual(CacheState.Shifted, Repair().Repair(dir, Streams(0, 3), "removed"));
        CollectionAssert.AreEqual(new[] { "2.srt", "3.srt", "4.srt" }, Names(dir));
        Assert.AreEqual("ff3", File.ReadAllText(Path.Combine(dir, "2.srt")));
    }

    [TestMethod]
    public void UnknownLayout_DeletesCache()
    {
        string dir = MakeCache("garbage", new[] { 2, 9, 40 });
        Assert.AreEqual(CacheState.Deleted, Repair().Repair(dir, Streams(0, 3), "garbage"));
        Assert.IsFalse(Directory.Exists(dir));
    }

    [TestMethod]
    public void DryRun_ChangesNothing()
    {
        string dir = MakeCache("dry", Enumerable.Range(2, 3));
        Repair(dryRun: true).Repair(dir, Streams(1, 3), "dry");
        CollectionAssert.AreEqual(new[] { "2.srt", "3.srt", "4.srt" }, Names(dir));
        Assert.IsFalse(File.Exists(Path.Combine(dir, SubtitleCacheRepair.StateFileName)));
    }
}
