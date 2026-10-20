using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetadataNotifier.Configuration;
using Jellyfin.Plugin.MetadataNotifier.Middleware;
using Jellyfin.Plugin.MetadataNotifier.Services;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.MetadataNotifier.Tests;

[TestClass]
public class ClientCapabilitiesTests
{
    // jellyfin-web on Tizen: no DV decoder, but DOVIWithHDR10 is allowed because the HDR10 base layer plays.
    private const string TizenWebProfile = """
        { "DeviceProfile": { "CodecProfiles": [
          { "Type": "Video", "Codec": "hevc", "Conditions": [
            { "Condition": "EqualsAny", "Property": "VideoRangeType", "Value": "SDR|HDR10|HDR10Plus|HLG|DOVIWithHDR10|DOVIWithHDR10Plus|DOVIWithEL|DOVIWithELHDR10Plus|DOVIInvalid", "IsRequired": false },
            { "Condition": "EqualsAny", "Property": "VideoProfile", "Value": "main|main 10", "IsRequired": false } ] },
          { "Type": "Video", "Codec": "h264", "Conditions": [
            { "Condition": "EqualsAny", "Property": "VideoRangeType", "Value": "SDR", "IsRequired": false } ] } ] } }
        """;

    // jellyfin-web where the browser really decodes DV (profiles 5 and 8).
    private const string DolbyVisionWebProfile = """
        { "DeviceProfile": { "CodecProfiles": [
          { "Type": "Video", "Codec": "hevc", "Conditions": [
            { "Condition": "EqualsAny", "Property": "VideoRangeType", "Value": "SDR|HDR10|HDR10Plus|HLG|DOVI|DOVIWithHDR10|DOVIWithHLG|DOVIWithSDR|DOVIWithHDR10Plus", "IsRequired": false } ] } ] } }
        """;

    private static string AndroidTvProfile(string unsupported) => $$"""
        { "DeviceProfile": { "CodecProfiles": [
          { "Type": "Video", "Codec": "hevc",
            "Conditions": [ { "Condition": "NotEquals", "Property": "VideoRangeType", "Value": "{{unsupported}}", "IsRequired": false } ],
            "ApplyConditions": [ { "Condition": "EqualsAny", "Property": "VideoRangeType", "Value": "{{unsupported}}", "IsRequired": false } ] } ] } }
        """;

    private static ClientVideoCapabilities Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var capabilities = ClientVideoCapabilities.FromPlaybackInfoBody(document.RootElement);
        Assert.IsNotNull(capabilities);
        return capabilities;
    }

    private static SessionInfo CreateSession(string client, string deviceName)
    {
        return new SessionInfo(null!, null!) { Client = client, DeviceName = deviceName };
    }

    private static MediaStream DolbyVisionProfile8Stream() => new()
    {
        Type = MediaStreamType.Video,
        Codec = "hevc",
        DvProfile = 8,
        ColorTransfer = "smpte2084",
        ColorPrimaries = "bt2020"
    };

    // ----- Parsing and evaluation -----

    [TestMethod]
    public void WebProfile_FallbackOnlyClient_RejectsPlainDoviButAllowsDoviWithHdr10()
    {
        var capabilities = Parse(TizenWebProfile);

        Assert.AreEqual(RangeSupport.Unsupported, capabilities.Supports("hevc", VideoRangeType.DOVI));
        Assert.AreEqual(RangeSupport.Supported, capabilities.Supports("hevc", VideoRangeType.DOVIWithHDR10));
        Assert.AreEqual(RangeSupport.Supported, capabilities.Supports("hevc", VideoRangeType.HDR10));
        Assert.AreEqual(RangeSupport.Unsupported, capabilities.Supports("h264", VideoRangeType.HDR10));
        Assert.AreEqual(RangeSupport.Unknown, capabilities.Supports("av1", VideoRangeType.HDR10), "No AV1 rules were declared");
    }

    [TestMethod]
    [DataRow("h265")]
    [DataRow("HEVC")]
    [DataRow("hvc1")]
    public void CodecAliases_MatchTheHevcProfile(string codec)
    {
        Assert.AreEqual(RangeSupport.Supported, Parse(DolbyVisionWebProfile).Supports(codec, VideoRangeType.DOVI));
    }

    [TestMethod]
    public void AndroidTvProfile_WithoutDvDecoder_ListsDoviAsUnsupported()
    {
        var capabilities = Parse(AndroidTvProfile("DOVIInvalid|DOVI|DOVIWithEL|DOVIWithELHDR10Plus|HDR10Plus|DOVIWithHDR10Plus"));

        Assert.AreEqual(RangeSupport.Unsupported, capabilities.Supports("hevc", VideoRangeType.DOVI));
        Assert.AreEqual(RangeSupport.Unsupported, capabilities.Supports("hevc", VideoRangeType.HDR10Plus));
        Assert.AreEqual(RangeSupport.Supported, capabilities.Supports("hevc", VideoRangeType.DOVIWithHDR10));
        Assert.AreEqual(RangeSupport.Supported, capabilities.Supports("hevc", VideoRangeType.HDR10));
    }

    [TestMethod]
    public void AndroidTvProfile_WithDvDecoder_AllowsDovi()
    {
        var capabilities = Parse(AndroidTvProfile("DOVIInvalid"));

        Assert.AreEqual(RangeSupport.Supported, capabilities.Supports("hevc", VideoRangeType.DOVI));
        Assert.AreEqual(RangeSupport.Unsupported, capabilities.Supports("hevc", VideoRangeType.DOVIInvalid));
    }

    [TestMethod]
    public void ContainerRestrictedProfile_OnlyAppliesToMatchingContainers()
    {
        // webOS: DV only in mp4/ts, so a second hevc profile for every other container drops the DOVI types.
        var capabilities = Parse("""
            { "DeviceProfile": { "CodecProfiles": [
              { "Type": "Video", "Codec": "hevc", "Conditions": [
                { "Condition": "EqualsAny", "Property": "VideoRangeType", "Value": "SDR|HDR10|HLG|DOVI|DOVIWithHDR10" } ] },
              { "Type": "Video", "Codec": "hevc", "Container": "-mp4,ts", "Conditions": [
                { "Condition": "EqualsAny", "Property": "VideoRangeType", "Value": "SDR|HDR10|HLG" } ] } ] } }
            """);

        Assert.AreEqual(RangeSupport.Unsupported, capabilities.Supports("hevc", VideoRangeType.DOVI, "mkv"));
        Assert.AreEqual(RangeSupport.Supported, capabilities.Supports("hevc", VideoRangeType.DOVI, "mp4"));
        Assert.AreEqual(RangeSupport.Supported, capabilities.Supports("hevc", VideoRangeType.DOVI, "mov,mp4,m4a"));
        Assert.AreEqual(RangeSupport.Supported, capabilities.Supports("hevc", VideoRangeType.DOVI, null), "A container-specific profile can't apply when the container is unknown");
    }

    [TestMethod]
    public void NumericEnumsAndLowercaseNames_AreAccepted()
    {
        var json = $$"""
            { "deviceProfile": { "codecProfiles": [
              { "type": {{(int)CodecType.Video}}, "codec": "hevc", "conditions": [
                { "condition": {{(int)ProfileConditionType.EqualsAny}}, "property": {{(int)ProfileConditionValue.VideoRangeType}}, "value": "SDR|HDR10" } ] } ] } }
            """;

        var capabilities = Parse(json);

        Assert.AreEqual(RangeSupport.Supported, capabilities.Supports("hevc", VideoRangeType.HDR10));
        Assert.AreEqual(RangeSupport.Unsupported, capabilities.Supports("hevc", VideoRangeType.DOVI));
    }

    [TestMethod]
    [DataRow("""{ "UserId": "x" }""")]
    [DataRow("""{ "DeviceProfile": { "CodecProfiles": [] } }""")]
    [DataRow("""{ "DeviceProfile": { "CodecProfiles": [ { "Type": "Audio", "Codec": "aac", "Conditions": [] } ] } }""")]
    [DataRow("""{ "DeviceProfile": { "CodecProfiles": [ { "Type": "Video", "Codec": "hevc", "Conditions": [ { "Condition": "LessThanEqual", "Property": "Width", "Value": "3840" } ] } ] } }""")]
    public void BodiesWithoutRangeRules_GiveNoCapabilities(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.IsNull(ClientVideoCapabilities.FromPlaybackInfoBody(document.RootElement));
    }

    [TestMethod]
    public void TypedDeviceProfile_FromSessionCapabilities_IsUnderstood()
    {
        var profile = new DeviceProfile
        {
            CodecProfiles =
            [
                new CodecProfile
                {
                    Type = CodecType.Video,
                    Codec = "hevc",
                    Conditions = [new ProfileCondition(ProfileConditionType.EqualsAny, ProfileConditionValue.VideoRangeType, "SDR|HDR10|DOVIWithHDR10")]
                }
            ]
        };

        var capabilities = ClientVideoCapabilities.FromDeviceProfile(profile);

        Assert.IsNotNull(capabilities);
        Assert.AreEqual(RangeSupport.Unsupported, capabilities.Supports("hevc", VideoRangeType.DOVI));
        Assert.IsNull(ClientVideoCapabilities.FromDeviceProfile(new DeviceProfile()));
    }

    // ----- Toast decisions: the profile replaces the name guesses -----

    [TestMethod]
    public void SamsungTizen_WithFallbackOnlyProfile_ReportsHdr10()
    {
        var config = new PluginConfiguration();
        var result = MetadataNotifierService.GetHdrInfo(DolbyVisionProfile8Stream(), "/m/Film.mkv", "Film",
            CreateSession("Jellyfin for Tizen", "Samsung Smart TV"), config, Parse(TizenWebProfile), "mkv");

        Assert.AreEqual("HDR10", result.Value);
        Assert.AreEqual("dolby-vision-client-fallback-hdr10", result.Rule);
    }

    [TestMethod]
    public void AndroidPhone_WithDvDecoder_ReportsDolbyVisionInsteadOfGuessingNo()
    {
        // By name alone an Android phone was assumed to lack DV; its profile says otherwise.
        var config = new PluginConfiguration();
        var session = CreateSession("Jellyfin Android", "Pixel 9 Pro");

        var guessed = MetadataNotifierService.GetHdrInfo(DolbyVisionProfile8Stream(), "/m/Film.mkv", "Film", session, config);
        var fromProfile = MetadataNotifierService.GetHdrInfo(DolbyVisionProfile8Stream(), "/m/Film.mkv", "Film", session, config, Parse(DolbyVisionWebProfile), "mkv");

        Assert.AreEqual("HDR10", guessed.Value);
        Assert.AreEqual("Dolby Vision", fromProfile.Value);
    }

    [TestMethod]
    public void UnknownBrand_WithoutDvDecoder_ReportsHdr10()
    {
        // A client no name list covers: the profile alone decides.
        var result = MetadataNotifierService.GetHdrInfo(DolbyVisionProfile8Stream(), "/m/Film.mkv", "Film",
            CreateSession("Jellyfin for Hisense", "VIDAA TV"), new PluginConfiguration(), Parse(TizenWebProfile), "mkv");

        Assert.AreEqual("HDR10", result.Value);
    }

    [TestMethod]
    public void NoProfile_FallsBackToNameMatching()
    {
        var result = MetadataNotifierService.GetHdrInfo(DolbyVisionProfile8Stream(), "/m/Film.mkv", "Film",
            CreateSession("Jellyfin for Tizen", "Samsung Smart TV"), new PluginConfiguration(), capabilities: null);

        Assert.AreEqual("HDR10", result.Value);
    }

    [TestMethod]
    public void FireTvOverride_WinsOverDvCapableProfile()
    {
        // The Fire TV decodes DV, but the user says its TV can't show it.
        var config = new PluginConfiguration { SuppressDvOnFireTv = true };
        var result = MetadataNotifierService.GetHdrInfo(DolbyVisionProfile8Stream(), "/m/Film.mkv", "Film",
            CreateSession("Jellyfin for Android TV", "Fire TV Stick 4K Max"), config, Parse(AndroidTvProfile("DOVIInvalid")), "mkv");

        Assert.AreEqual("HDR10", result.Value);
    }

    [TestMethod]
    public void FireTvOverride_WorksEvenWhenGeneralDvFallbackIsOff()
    {
        var config = new PluginConfiguration { SuppressDvOnSamsung = false, SuppressDvOnFireTv = true };
        var result = MetadataNotifierService.GetHdrInfo(DolbyVisionProfile8Stream(), "/m/Film.mkv", "Film",
            CreateSession("Jellyfin for Android TV", "Fire TV Cube"), config);

        Assert.AreEqual("HDR10", result.Value);
    }

    [TestMethod]
    public void DvFallbackSettingOff_ReportsDolbyVisionEvenIfProfileRejectsIt()
    {
        var config = new PluginConfiguration { SuppressDvOnSamsung = false };
        var result = MetadataNotifierService.GetHdrInfo(DolbyVisionProfile8Stream(), "/m/Film.mkv", "Film",
            CreateSession("Jellyfin Web", "Chrome"), config, Parse(TizenWebProfile), "mkv");

        Assert.AreEqual("Dolby Vision", result.Value);
    }

    [TestMethod]
    public void Hdr10Plus_RejectedByProfile_ReportsHdr10()
    {
        var stream = new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Hdr10PlusPresentFlag = true, ColorTransfer = "smpte2084" };
        var result = MetadataNotifierService.GetHdrInfo(stream, "/m/Film.mkv", "Film",
            CreateSession("Jellyfin for Android TV", "Chromecast"), new PluginConfiguration(),
            Parse(AndroidTvProfile("DOVIInvalid|HDR10Plus|DOVIWithHDR10Plus")), "mkv");

        Assert.AreEqual("HDR10", result.Value);
    }

    [TestMethod]
    public void Hdr10Plus_ClaimedByWebProfileOnLg_IsStillCorrectedByName()
    {
        // jellyfin-web allows HDR10Plus wherever it allows HDR10, including webOS.
        var stream = new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Hdr10PlusPresentFlag = true, ColorTransfer = "smpte2084" };
        var result = MetadataNotifierService.GetHdrInfo(stream, "/m/Film.mkv", "Film",
            CreateSession("Jellyfin for WebOS", "LG OLED TV"), new PluginConfiguration(), Parse(DolbyVisionWebProfile), "mkv");

        Assert.AreEqual("HDR10", result.Value);
    }

    [TestMethod]
    public void VideoTranscode_UsesOutputCodecRules()
    {
        // Re-encoded to H.264, which this client only accepts as SDR, so the source's DV can't arrive.
        var session = CreateSession("Jellyfin Web", "Safari");
        session.TranscodingInfo = new TranscodingInfo { IsVideoDirect = false, IsAudioDirect = true, VideoCodec = "h264", Container = "ts" };
        var capabilities = Parse("""
            { "DeviceProfile": { "CodecProfiles": [
              { "Type": "Video", "Codec": "hevc", "Conditions": [ { "Condition": "EqualsAny", "Property": "VideoRangeType", "Value": "SDR|HDR10|DOVI|DOVIWithHDR10" } ] },
              { "Type": "Video", "Codec": "h264", "Conditions": [ { "Condition": "EqualsAny", "Property": "VideoRangeType", "Value": "SDR" } ] } ] } }
            """);

        var result = MetadataNotifierService.GetHdrInfo(DolbyVisionProfile8Stream(), "/m/Film.mkv", "Film", session, new PluginConfiguration(), capabilities, "mkv");

        Assert.AreNotEqual("Dolby Vision", result.Value);
    }

    // ----- Browsers: what reaches the screen -----

    // The file from the bug report: DV profile 8.1 with an HDR10 base layer and HDR10+ metadata,
    // sent unchanged (video direct, audio transcoded) to Chrome on Windows.
    private static MediaStream DolbyVisionWithHdr10PlusStream() => new()
    {
        Type = MediaStreamType.Video,
        Codec = "hevc",
        DvProfile = 8,
        DvBlSignalCompatibilityId = 1,
        Hdr10PlusPresentFlag = true,
        RpuPresentFlag = 1,
        BlPresentFlag = 1,
        ColorTransfer = "smpte2084",
        ColorPrimaries = "bt2020"
    };

    private static SessionInfo CreateChromeSession()
    {
        var session = CreateSession("Jellyfin Web", "Chrome");
        session.TranscodingInfo = new TranscodingInfo { IsVideoDirect = true, IsAudioDirect = false, VideoCodec = "hevc", AudioCodec = "aac" };
        return session;
    }

    [TestMethod]
    public void Chrome_DolbyVisionWithHdr10Plus_ReportsHdr10()
    {
        // Chrome's profile allows DOVIWithHDR10Plus and HDR10Plus but not DOVI. The DV fallback used
        // to pick HDR10+, which Chrome never applies; it plays the HDR10 base layer.
        var result = MetadataNotifierService.GetHdrInfo(DolbyVisionWithHdr10PlusStream(), "/m/S01E02.mkv", "Camera Shy",
            CreateChromeSession(), new PluginConfiguration(), Parse(TizenWebProfile), "mkv");

        Assert.AreEqual("HDR10", result.Value);
        Assert.AreEqual("hdr10plus-client-fallback-hdr10", result.Rule);
    }

    [TestMethod]
    public void Hdr10PlusFallback_DoesNotReportDolbyVisionOnClientWithoutDv()
    {
        // An LG set without a DV decoder: the HDR10+ fallback used to jump to Dolby Vision anyway.
        var result = MetadataNotifierService.GetHdrInfo(DolbyVisionWithHdr10PlusStream(), "/m/Film.mkv", "Film",
            CreateSession("Jellyfin for WebOS", "LG OLED TV"), new PluginConfiguration(), Parse(TizenWebProfile), "mkv");

        Assert.AreEqual("HDR10", result.Value);
        Assert.AreEqual("hdr10plus-client-fallback-hdr10", result.Rule);
    }

    [TestMethod]
    public void Safari_KeepsHdr10Plus()
    {
        var stream = new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Hdr10PlusPresentFlag = true, ColorTransfer = "smpte2084" };
        var result = MetadataNotifierService.GetHdrInfo(stream, "/m/Film.mkv", "Film",
            CreateSession("Jellyfin Web", "Safari"), new PluginConfiguration(), Parse(DolbyVisionWebProfile), "mkv");

        Assert.AreEqual("HDR10+", result.Value);
    }

    [TestMethod]
    [DataRow("Jellyfin Web", "Chrome", "Chrome")]
    [DataRow("Jellyfin Web", "Edge Chromium", "Edge Chromium")]
    [DataRow("Jellyfin Web", "Firefox Android", "Firefox")]
    [DataRow("Jellyfin Web", "Safari iPad", "Safari")]
    [DataRow("Jellyfin Web", "Samsung Smart TV", null)]
    [DataRow("Jellyfin Web", "Chromecast", null)]
    [DataRow("Jellyfin Media Player", "Chrome", null)]
    [DataRow("Samsung Smart TV", "Living Room", null)]
    public void GetWebBrowserName_OnlyMatchesJellyfinWebInABrowser(string client, string deviceName, string? expected)
    {
        Assert.AreEqual(expected, MetadataNotifierService.GetWebBrowserName(CreateSession(client, deviceName)));
    }

    [TestMethod]
    public void ClientToneMappedSdr_RequiresOptionSdrDisplayAndBrowser()
    {
        var enabled = new PluginConfiguration { DesktopSdrMode = true };
        var chrome = CreateChromeSession();

        Assert.IsTrue(MetadataNotifierService.ShouldReportClientToneMappedSdr(enabled, chrome, displayHdr: false));
        Assert.IsFalse(MetadataNotifierService.ShouldReportClientToneMappedSdr(enabled, chrome, displayHdr: true), "HDR display");
        Assert.IsFalse(MetadataNotifierService.ShouldReportClientToneMappedSdr(enabled, chrome, displayHdr: null), "Not reported");
        Assert.IsFalse(MetadataNotifierService.ShouldReportClientToneMappedSdr(new PluginConfiguration(), chrome, displayHdr: false), "Option off");
        Assert.IsFalse(
            MetadataNotifierService.ShouldReportClientToneMappedSdr(enabled, CreateSession("Jellyfin for Tizen", "Samsung Smart TV"), displayHdr: false),
            "TV apps render HDR outside the web view");
    }

    // ----- Capturing the profile from PlaybackInfo requests -----

    private static DefaultHttpContext CreatePlaybackInfoRequest(string body, string path = "/Items/0123456789abcdef0123456789abcdef/PlaybackInfo")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.Headers.Authorization = "MediaBrowser Client=\"Jellyfin Web\", Device=\"Firefox\", DeviceId=\"TW96aWxsYQ%3D%3D\", Version=\"10.11.0\", Token=\"t\"";
        return context;
    }

    [TestMethod]
    public async Task Middleware_StoresProfileByDeviceId_AndLeavesBodyReadable()
    {
        var store = new ClientCapabilityStore();
        string? bodySeenByJellyfin = null;
        var middleware = new PlaybackInfoCaptureMiddleware(
            async ctx => bodySeenByJellyfin = await new StreamReader(ctx.Request.Body).ReadToEndAsync(),
            NullLogger<PlaybackInfoCaptureMiddleware>.Instance);
        var context = CreatePlaybackInfoRequest(TizenWebProfile, "/jellyfin/Items/01234567-89ab-cdef-0123-456789abcdef/PlaybackInfo");

        await middleware.InvokeAsync(context, store);

        Assert.AreEqual(TizenWebProfile, bodySeenByJellyfin, "Jellyfin must still receive the full request body");
        Assert.IsNotNull(store.Get("TW96aWxsYQ=="), "URL-decoded device ID");
        Assert.IsNotNull(store.Get("TW96aWxsYQ%3D%3D"), "Device ID as sent");
        Assert.AreEqual(RangeSupport.Unsupported, store.Get("TW96aWxsYQ==")!.Supports("hevc", VideoRangeType.DOVI));
    }

    [TestMethod]
    public async Task Middleware_IgnoresOtherRequestsAndBrokenJson()
    {
        var store = new ClientCapabilityStore();
        var calls = 0;
        var middleware = new PlaybackInfoCaptureMiddleware(_ => { calls++; return Task.CompletedTask; }, NullLogger<PlaybackInfoCaptureMiddleware>.Instance);

        await middleware.InvokeAsync(CreatePlaybackInfoRequest(TizenWebProfile, "/Items/0123456789abcdef0123456789abcdef/Images/Primary"), store);
        await middleware.InvokeAsync(CreatePlaybackInfoRequest("{ not json"), store);

        Assert.AreEqual(2, calls, "The request always continues");
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public async Task Middleware_RequestWithoutProfile_KeepsPreviousOne()
    {
        var store = new ClientCapabilityStore();
        var middleware = new PlaybackInfoCaptureMiddleware(_ => Task.CompletedTask, NullLogger<PlaybackInfoCaptureMiddleware>.Instance);

        await middleware.InvokeAsync(CreatePlaybackInfoRequest(DolbyVisionWebProfile), store);
        await middleware.InvokeAsync(CreatePlaybackInfoRequest("""{ "UserId": "x" }"""), store);

        Assert.AreEqual(RangeSupport.Supported, store.Get("TW96aWxsYQ==")!.Supports("hevc", VideoRangeType.DOVI));
    }

    [TestMethod]
    [DataRow("sdr", false)]
    [DataRow("hdr", true)]
    public async Task Middleware_StoresDisplayRangeFromCookie(string cookie, bool expected)
    {
        var store = new ClientCapabilityStore();
        var middleware = new PlaybackInfoCaptureMiddleware(_ => Task.CompletedTask, NullLogger<PlaybackInfoCaptureMiddleware>.Instance);
        var context = CreatePlaybackInfoRequest("""{ "UserId": "x" }""");
        context.Request.Headers.Cookie = $"other=1; {DisplayScript.CookieName}={cookie}";

        await middleware.InvokeAsync(context, store);

        Assert.AreEqual(expected, store.GetDisplayHdr("TW96aWxsYQ=="));
    }

    [TestMethod]
    public async Task Middleware_WithUnknownDisplayCookie_KeepsDisplayUnknown()
    {
        var store = new ClientCapabilityStore();
        var middleware = new PlaybackInfoCaptureMiddleware(_ => Task.CompletedTask, NullLogger<PlaybackInfoCaptureMiddleware>.Instance);
        var context = CreatePlaybackInfoRequest(TizenWebProfile);
        context.Request.Headers.Cookie = $"{DisplayScript.CookieName}=bogus";

        await middleware.InvokeAsync(context, store);

        Assert.IsNull(store.GetDisplayHdr("TW96aWxsYQ=="));
    }

    [TestMethod]
    [DataRow("MediaBrowser Client=\"Web\", DeviceId=\"abc\", Token=\"t\"", "abc")]
    [DataRow("MediaBrowser DeviceId=abc, Token=t", "abc")]
    [DataRow("Client=\"Web\", DeviceId=\"abc\"", "abc")]
    [DataRow("MediaBrowser Token=\"t\"", null)]
    public void GetAuthorizationParameter_ReadsDeviceId(string header, string? expected)
    {
        Assert.AreEqual(expected, PlaybackInfoCaptureMiddleware.GetAuthorizationParameter(header, "DeviceId"));
    }
}
