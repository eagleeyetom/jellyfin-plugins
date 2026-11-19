using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Serialization;
using Jellyfin.Plugin.PositionedSubtitles.Api;
using Jellyfin.Plugin.PositionedSubtitles.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.PositionedSubtitles.Tests;

[TestClass]
public sealed class LiveStylingTests
{
    private static readonly Guid User = Guid.Parse("11111111222233334444555555555555");

    private const string PlaybackInfo = """
        {"MediaSources":[{"Id":"1fc41f59fbbda350aa4176acd828bfef","MediaStreams":[
         {"Type":"Video","Codec":"hevc","Index":0},
         {"Type":"Subtitle","Codec":"subrip","Language":"pol","Index":24,"IsExternal":false,"DeliveryMethod":"External","DeliveryUrl":"/Videos/a/b/Subtitles/24/0/Stream.js?ApiKey=x"},
         {"Type":"Subtitle","Codec":"subrip","Language":"eng","Index":2,"IsExternal":false,"DeliveryMethod":"External","DeliveryUrl":"/Videos/a/b/Subtitles/2/0/Stream.js"},
         {"Type":"Subtitle","Codec":"subrip","Language":"pol","Index":25,"IsExternal":false,"DeliveryMethod":"Encode"},
         {"Type":"Subtitle","Codec":"ass","Language":"pol","Index":26,"IsExternal":false,"DeliveryMethod":"External","DeliveryUrl":"/Videos/a/b/Subtitles/26/0/Stream.ass"}
        ]}],"PlaySessionId":"z"}
        """;

    private static (int Changed, JsonArray Streams) RunRewrite(PluginConfiguration config)
    {
        var root = JsonNode.Parse(PlaybackInfo);
        var localization = DispatchProxy.Create<ILocalizationManager, NullProxy>();
        var paths = DispatchProxy.Create<IPathManager, NullProxy>();
        int changed = SubtitleStyleMiddleware.Rewrite(root, config, User, paths, localization);
        return (changed, root!["MediaSources"]![0]!["MediaStreams"]!.AsArray());
    }

    [TestMethod]
    public void Rewrite_AnnouncesSrtAsAssAndTagsUser()
    {
        var (changed, streams) = RunRewrite(new PluginConfiguration { OnlyPositioned = false });

        Assert.AreEqual(2, changed);
        Assert.AreEqual("ass", (string?)streams[1]!["Codec"]);
        Assert.AreEqual("/Videos/a/b/Subtitles/24/0/Stream.ass?ApiKey=x&PsUser=11111111222233334444555555555555", (string?)streams[1]!["DeliveryUrl"]);
        Assert.AreEqual("subrip", (string?)streams[2]!["Codec"], "other languages stay untouched");
        Assert.AreEqual("subrip", (string?)streams[3]!["Codec"], "burned-in tracks stay untouched");
        Assert.AreEqual("/Videos/a/b/Subtitles/26/0/Stream.ass?PsUser=11111111222233334444555555555555", (string?)streams[4]!["DeliveryUrl"]);
    }

    [TestMethod]
    public void Rewrite_OnlyPositioned_WithoutCache_OnlyTagsAss()
    {
        var (changed, streams) = RunRewrite(new PluginConfiguration { OnlyPositioned = true });
        Assert.AreEqual(1, changed);
        Assert.AreEqual("subrip", (string?)streams[1]!["Codec"]);
    }

    [TestMethod]
    public void FindUserStyle_MatchesEnabledUsersOnly()
    {
        var config = new PluginConfiguration();
        config.UserStyles.Add(new UserStyle { UserId = "11111111222233334444555555555555", UserName = "A" });
        config.UserStyles.Add(new UserStyle { UserId = "aaaaaaaa222233334444555555555555", UserName = "B", Enabled = false });

        Assert.AreEqual("A", config.FindUserStyle(Guid.Parse("11111111-2222-3333-4444-555555555555"))?.UserName);
        Assert.IsNull(config.FindUserStyle(Guid.Parse("aaaaaaaa222233334444555555555555")));
        Assert.IsNull(config.FindUserStyle(Guid.NewGuid()));
        Assert.IsNull(config.FindUserStyle(Guid.Empty));
    }

    [TestMethod]
    public void Configuration_RoundTripsThroughXml_AndLoadsV1Files()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration();
        config.UserStyles.Add(new UserStyle { UserId = "11111111222233334444555555555555", UserName = "A", PrimaryColor = "#00FF00" });
        config.UserStyles.Add(new UserStyle { UserId = "aaaaaaaa222233334444555555555555", UserName = "B", Enabled = false });

        var writer = new StringWriter();
        serializer.Serialize(writer, config);
        var back = (PluginConfiguration)serializer.Deserialize(new StringReader(writer.ToString()))!;
        Assert.AreEqual(2, back.UserStyles.Count);
        Assert.AreEqual("#00FF00", back.UserStyles[0].PrimaryColor);
        Assert.IsFalse(back.UserStyles[1].Enabled);

        const string v1 = "<?xml version=\"1.0\"?><PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><Languages>pol</Languages><FontName>Arial</FontName><PrimaryColor>#FFFF00</PrimaryColor><DryRun>false</DryRun></PluginConfiguration>";
        var legacy = (PluginConfiguration)serializer.Deserialize(new StringReader(v1))!;
        Assert.AreEqual("pol", legacy.Languages);
        Assert.AreEqual(0, legacy.UserStyles.Count);
        Assert.AreEqual("#FFFF00", legacy.Style.PrimaryColor);
    }
}

/// <summary>Interface stub whose members all return null/default.</summary>
public class NullProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
}
