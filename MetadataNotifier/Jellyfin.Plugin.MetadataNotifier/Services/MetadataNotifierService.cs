using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetadataNotifier.Configuration;
using Jellyfin.Plugin.Shared.MediaDetection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetadataNotifier.Services;

/// <summary>
/// Background service that listens for playback events and sends media info toast notifications.
/// </summary>
public partial class MetadataNotifierService : IHostedService
{
    internal readonly record struct HdrDetectionResult(string Value, string Rule);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ClientCapabilityStore _capabilityStore;
    private readonly ILogger<MetadataNotifierService> _logger;
    private readonly ConcurrentDictionary<string, int> _lastActiveAudioTrack = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingStartupToasts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _serviceCancellation;

    [GeneratedRegex(@"\bLG\b", RegexOptions.IgnoreCase)]
    private static partial Regex LgWordRegex();

    [GeneratedRegex(@"(?:Profile|dvhe|dvh1)[\s\.]*8\.1\b", RegexOptions.IgnoreCase)]
    private static partial Regex Profile81Regex();

    [GeneratedRegex(@"(?:Profile|dvhe|dvh1)[\s\.]*8\.4\b", RegexOptions.IgnoreCase)]
    private static partial Regex Profile84Regex();

    [GeneratedRegex(@"(?:Profile|dvhe|dvh1)[\s\.]*7\.6\b", RegexOptions.IgnoreCase)]
    private static partial Regex Profile76Regex();

    internal static Regex Hdr10PlusPathRegex => HdrFormatDetection.Hdr10PlusTokenRegex();

    internal static Regex DolbyVisionPathRegex => HdrFormatDetection.DolbyVisionTokenRegex();

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataNotifierService"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="capabilityStore">Device profiles captured from PlaybackInfo requests.</param>
    /// <param name="logger">Logger.</param>
    public MetadataNotifierService(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ClientCapabilityStore capabilityStore,
        ILogger<MetadataNotifierService> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _capabilityStore = capabilityStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("Metadata Notifier service started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _serviceCancellation?.Cancel();
        _serviceCancellation?.Dispose();
        _serviceCancellation = null;
        _lastActiveAudioTrack.Clear();

        foreach (var pending in _pendingStartupToasts.Values)
        {
            try
            {
                pending.Cancel();
                pending.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        _pendingStartupToasts.Clear();

        _logger.LogInformation("Metadata Notifier service stopped.");
        return Task.CompletedTask;
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session != null)
        {
            _lastActiveAudioTrack.TryRemove(e.Session.Id, out _);
            if (_pendingStartupToasts.TryRemove(e.Session.Id, out var pendingCts))
            {
                try
                {
                    pendingCts.Cancel();
                    pendingCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        var session = e.Session;
        if (session == null)
        {
            return;
        }

        if (_pendingStartupToasts.TryRemove(session.Id, out var existingCts))
        {
            try
            {
                existingCts.Cancel();
                existingCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCancellation?.Token ?? CancellationToken.None);
        _pendingStartupToasts[session.Id] = cts;

        _ = HandlePlaybackStartAsync(e, cts);
    }

    private async Task HandlePlaybackStartAsync(PlaybackProgressEventArgs e, CancellationTokenSource cts)
    {
        var cancellationToken = cts.Token;
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.IsEnabled)
            {
                return;
            }

            var session = e.Session;
            if (session == null)
            {
                return;
            }

            if (IsUserExcluded(session, config))
            {
                return;
            }

            var item = e.Item;
            if (item == null || ShouldIgnoreItem(item, config, _libraryManager))
            {
                return;
            }

            int? initialAudioIndex = session.PlayState?.AudioStreamIndex;
            if (initialAudioIndex.HasValue)
            {
                _lastActiveAudioTrack[session.Id] = initialAudioIndex.Value;
            }

            var originalDeviceId = session.DeviceId;
            session = await WaitForUpdatedSessionAsync(session, originalDeviceId, config.StartupDelayMs, cancellationToken).ConfigureAwait(false);

            if (!session.IsActive)
            {
                return;
            }

            if (session.PlayState?.AudioStreamIndex is int postDelayAudioIndex)
            {
                _lastActiveAudioTrack[session.Id] = postDelayAudioIndex;
            }

            MediaSourceInfo? activeSource = null;
            IReadOnlyList<MediaStream>? mediaStreams = null;
            MediaStream? videoStream = null;
            MediaStream? activeAudioStream = null;

            bool needStreams = config.ShowSdr || config.ShowHdr10Plus || config.ShowHdr10 || config.ShowDolbyVision || config.ShowHlg || config.ShowAudio || config.ShowBitrate;
            if (needStreams)
            {
                activeSource = GetActiveMediaSource(item, session);
                mediaStreams = GetMediaStreams(item, session, activeSource);
                videoStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                activeAudioStream = GetActiveAudioStream(mediaStreams, session.PlayState?.AudioStreamIndex);
            }

            var capabilities = GetClientCapabilities(session);
            var hdrInfo = string.Empty;
            var hdrRule = "disabled";
            if (config.ShowSdr || config.ShowHdr10Plus || config.ShowHdr10 || config.ShowDolbyVision || config.ShowHlg)
            {
                var hdrResult = MetadataFormatter.ShouldReportToneMappedSdr(config, session.TranscodingInfo)
                    ? new HdrDetectionResult(config.ShowSdr ? "SDR" : string.Empty, "unsupported-hdr-transcode")
                    : GetHdrInfo(videoStream, item.Path ?? string.Empty, item.Name ?? string.Empty, session, config, capabilities, activeSource?.Container);
                hdrInfo = hdrResult.Value;
                hdrRule = hdrResult.Rule;
            }

            var audioInfo = string.Empty;
            if (config.ShowAudio)
            {
                audioInfo = GetAudioInfo(activeAudioStream, session, config);
            }

            var playbackInfo = string.Empty;
            if (config.ShowTranscoding || config.ShowDirectPlay)
            {
                var transcodingInfo = MetadataFormatter.FormatPlayback(session.TranscodingInfo, config.ShowTranscodeReasons);
                if (!string.IsNullOrEmpty(transcodingInfo))
                {
                    if (string.Equals(transcodingInfo, "Direct Stream", StringComparison.Ordinal))
                    {
                        if (config.ShowDirectPlay)
                        {
                            playbackInfo = transcodingInfo;
                        }
                    }
                    else if (config.ShowTranscoding)
                    {
                        playbackInfo = transcodingInfo;
                    }
                }
                else
                {
                    if (config.ShowDirectPlay)
                    {
                        playbackInfo = "Direct Play";
                    }
                }
            }

            var bitrateInfo = string.Empty;
            if (config.ShowBitrate)
            {
                bitrateInfo = GetBitrateInfo(item, session, activeSource, mediaStreams, activeAudioStream);
            }

            var header = GetNotificationHeader(item);
            var messageText = MetadataFormatter.BuildMessageText(config, hdrInfo, audioInfo, playbackInfo, bitrateInfo, header);

            _logger.LogDebug(
                "Metadata detection for session {SessionId}: media source {MediaSourceId}, HDR rule {HdrRule} (client capabilities from {CapabilitySource}), HDR {HdrInfo}, audio {AudioInfo}, playback {PlaybackInfo}, transcode reasons {TranscodeReasons}",
                session.Id,
                session.PlayState?.MediaSourceId,
                hdrRule,
                capabilities != null ? "device profile" : "client name",
                hdrInfo,
                audioInfo,
                playbackInfo,
                session.TranscodingInfo?.TranscodeReasons);

            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            if (!session.IsActive)
            {
                _logger.LogWarning(
                    "Session {SessionId} ({Client} / {Device}) has no active WebSocket controllers at send time — toast may not be delivered.",
                    session.Id,
                    session.Client,
                    session.DeviceName);
            }

            _logger.LogInformation(
                "Sending metadata toast to session {SessionId} ({Client} / {Device}): {Header} | {Message}",
                session.Id,
                session.Client,
                session.DeviceName,
                header,
                messageText);

            var messageCommand = new MessageCommand
            {
                Header = string.Empty,
                Text = messageText,
                TimeoutMs = config.NotificationDurationMs
            };

            await _sessionManager.SendMessageCommand(
                session.Id,
                session.Id,
                messageCommand,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Metadata notification was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending metadata toast notification.");
        }
        finally
        {
            if (e.Session != null && _pendingStartupToasts.TryGetValue(e.Session.Id, out var current) && current == cts)
            {
                _pendingStartupToasts.TryRemove(e.Session.Id, out _);
            }
            cts.Dispose();
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        var session = e.Session;
        if (session == null || !session.IsActive)
        {
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.IsEnabled || !config.NotifyOnAudioTrackChange)
        {
            return;
        }

        int? currentAudioIndex = session.PlayState?.AudioStreamIndex;
        if (!currentAudioIndex.HasValue)
        {
            return;
        }

        if (_lastActiveAudioTrack.TryGetValue(session.Id, out var previousIndex))
        {
            if (previousIndex == currentAudioIndex.Value)
            {
                return;
            }
        }
        else
        {
            _lastActiveAudioTrack[session.Id] = currentAudioIndex.Value;
            return;
        }

        _ = HandlePlaybackProgressAsync(e, _serviceCancellation?.Token ?? CancellationToken.None);
    }

    private async Task HandlePlaybackProgressAsync(PlaybackProgressEventArgs e, CancellationToken cancellationToken)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.IsEnabled || !config.NotifyOnAudioTrackChange)
            {
                return;
            }

            var session = e.Session;
            if (session == null || !session.IsActive)
            {
                return;
            }

            int? currentAudioIndex = session.PlayState?.AudioStreamIndex;
            if (!currentAudioIndex.HasValue)
            {
                return;
            }

            _lastActiveAudioTrack[session.Id] = currentAudioIndex.Value;

            if (IsUserExcluded(session, config))
            {
                return;
            }

            var item = e.Item;
            if (item == null || ShouldIgnoreItem(item, config, _libraryManager))
            {
                return;
            }

            var mediaStreams = GetMediaStreams(item, session);
            var activeAudioStream = GetActiveAudioStream(mediaStreams, currentAudioIndex.Value);
            var audioInfo = GetAudioInfo(activeAudioStream, session, config);
            if (string.IsNullOrEmpty(audioInfo))
            {
                return;
            }

            var header = GetNotificationHeader(item);
            var toastText = $"Audio: {audioInfo}";

                _logger.LogInformation(
                    "Audio track changed in session {SessionId} ({Client}): {Text}",
                    session.Id,
                    session.Client,
                    toastText);

                var messageCommand = new MessageCommand
                {
                    Header = string.Empty,
                    Text = toastText,
                    TimeoutMs = config.NotificationDurationMs
                };

            await _sessionManager.SendMessageCommand(
                session.Id,
                session.Id,
                messageCommand,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Audio track notification was cancelled because the service is stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling audio track change event.");
        }
    }

    private const int SessionPollIntervalMs = 250;

    private async Task<SessionInfo> WaitForUpdatedSessionAsync(
        SessionInfo originalSession,
        string deviceId,
        int startupDelayMs,
        CancellationToken cancellationToken)
    {
        if (startupDelayMs <= 0)
        {
            var activeSession = _sessionManager.Sessions.FirstOrDefault(
                candidate => string.Equals(candidate.Id, originalSession.Id, StringComparison.Ordinal));
            return ResolveActiveSession(activeSession ?? originalSession, deviceId);
        }

        var session = originalSession;
        var elapsed = 0;

        while (elapsed < startupDelayMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var delay = Math.Min(SessionPollIntervalMs, startupDelayMs - elapsed);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            elapsed += delay;

            var activeSession = _sessionManager.Sessions.FirstOrDefault(
                candidate => string.Equals(candidate.Id, originalSession.Id, StringComparison.Ordinal));
            if (activeSession != null)
            {
                session = activeSession;
                if (!session.IsActive)
                {
                    break;
                }
            }
        }

        return ResolveActiveSession(session, deviceId);
    }

    /// <summary>
    /// Returns <paramref name="session"/> if it has at least one active WebSocket controller.
    /// Otherwise falls back to the most-recently-active session for the same <paramref name="deviceId"/>.
    /// This handles the case where the client's WebSocket reconnected during the startup delay
    /// and Jellyfin assigned a new <see cref="SessionInfo"/> to the same physical device.
    /// </summary>
    private SessionInfo ResolveActiveSession(SessionInfo session, string deviceId)
    {
        if (session.IsActive)
        {
            return session;
        }

        if (string.IsNullOrEmpty(deviceId))
        {
            return session;
        }

        // Find the most-recently-active session for the same device that actually has an open WS connection.
        var fallback = _sessionManager.Sessions
            .Where(s => string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                        && s.IsActive
                        && !string.Equals(s.Id, session.Id, StringComparison.Ordinal))
            .OrderByDescending(s => s.LastActivityDate)
            .FirstOrDefault();

        if (fallback != null)
        {
            _logger.LogDebug(
                "Original session {OriginalId} has no active WebSocket; falling back to session {FallbackId} for device {DeviceId}.",
                session.Id,
                fallback.Id,
                deviceId);
            return fallback;
        }

        return session;
    }

    internal static bool ShouldIgnoreItem(BaseItem item, PluginConfiguration config, ILibraryManager libraryManager)
    {
        if (config.IgnoreAudioMedia && (item is Audio || item.MediaType == MediaType.Audio))
        {
            return true;
        }

        if (config.ExcludedLibraryIds != null && config.ExcludedLibraryIds.Count > 0)
        {
            return IsInExcludedLibrary(GetLibraryIds(item, libraryManager), config.ExcludedLibraryIds);
        }

        return false;
    }

    /// <summary>
    /// The configuration page stores library IDs from <c>Library/VirtualFolders</c>, which are
    /// collection folder IDs. The physical top-level folder an item lives in has a different ID,
    /// so the collection folders have to come from the library manager.
    /// </summary>
    private static IEnumerable<Guid> GetLibraryIds(BaseItem item, ILibraryManager libraryManager)
    {
        IEnumerable<Guid> collectionFolderIds;
        try
        {
            collectionFolderIds = libraryManager.GetCollectionFolders(item).Select(folder => folder.Id).ToList();
        }
        catch (Exception)
        {
            collectionFolderIds = Array.Empty<Guid>();
        }

        var topParentId = GetTopParentId(item);
        return topParentId.HasValue ? collectionFolderIds.Append(topParentId.Value) : collectionFolderIds;
    }

    internal static bool IsInExcludedLibrary(IEnumerable<Guid> libraryIds, IEnumerable<string> excludedLibraryIds)
    {
        var excluded = new HashSet<Guid>();
        foreach (var id in excludedLibraryIds)
        {
            if (Guid.TryParse(id, out var guid))
            {
                excluded.Add(guid);
            }
        }

        return excluded.Count > 0 && libraryIds.Any(excluded.Contains);
    }

    private static bool IsUserExcluded(SessionInfo session, PluginConfiguration config)
    {
        if (config.ExcludedUserIds != null && config.ExcludedUserIds.Count > 0 && session.UserId != Guid.Empty)
        {
            var userIdStr = session.UserId.ToString();
            var userIdN = session.UserId.ToString("N");
            return config.ExcludedUserIds.Contains(userIdStr, StringComparer.OrdinalIgnoreCase)
                || config.ExcludedUserIds.Contains(userIdN, StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    private static Guid? GetTopParentId(BaseItem item)
    {
        var parent = item;
        while (parent != null)
        {
            if (parent is Folder folder && folder.IsTopParent)
            {
                return folder.Id;
            }

            parent = parent.GetParent();
        }

        return item.ParentId != Guid.Empty ? item.ParentId : null;
    }

    private static string GetNotificationHeader(BaseItem item)
    {
        if (item is Episode episode)
        {
            var seriesName = episode.SeriesName;
            if (string.IsNullOrWhiteSpace(seriesName) && episode.Series != null)
            {
                seriesName = episode.Series.Name;
            }

            var seasonNum = episode.ParentIndexNumber;
            var epNum = episode.IndexNumber;

            if (!string.IsNullOrWhiteSpace(seriesName))
            {
                if (seasonNum.HasValue && epNum.HasValue)
                {
                    return $"{seriesName} S{seasonNum.Value:D2}E{epNum.Value:D2}";
                }

                return $"{seriesName} - {episode.Name}";
            }
        }

        return item.Name ?? "Media Info";
    }

    /// <summary>
    /// The device profile the client last sent with a PlaybackInfo request, or the one in its
    /// session capabilities. Null when the client reports neither (e.g. some Kodi add-ons).
    /// </summary>
    private ClientVideoCapabilities? GetClientCapabilities(SessionInfo session)
    {
        return _capabilityStore.Get(session.DeviceId)
            ?? ClientVideoCapabilities.FromDeviceProfile(session.Capabilities?.DeviceProfile);
    }

    internal static bool IsSamsungClient(SessionInfo session)
    {
        return ClientDeviceDetection.ContainsSamsungTvIndicator(session.Client)
            || ClientDeviceDetection.ContainsSamsungTvIndicator(session.DeviceName);
    }

    internal static bool IsFireTvClient(SessionInfo session)
    {
        return ContainsFireTvIndicator(session.Client) || ContainsFireTvIndicator(session.DeviceName);
    }

    internal static bool ContainsFireTvIndicator(string? value) => ClientDeviceDetection.ContainsFireTvIndicator(value);

    internal static bool IsNonHdr10PlusClient(SessionInfo session)
    {
        var client = session.Client ?? string.Empty;
        var deviceName = session.DeviceName ?? string.Empty;

        return client.Contains("webOS", StringComparison.OrdinalIgnoreCase)
            || LgWordRegex().IsMatch(client)
            || client.Contains("Sony", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Bravia", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Roku", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Shield", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Sharp", StringComparison.OrdinalIgnoreCase)
            || client.Contains("Toshiba", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("webOS", StringComparison.OrdinalIgnoreCase)
            || LgWordRegex().IsMatch(deviceName)
            || deviceName.Contains("Sony", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Bravia", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Roku", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Shield", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Sharp", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Toshiba", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Name-based guess, used only when the client reports no device profile.
    /// </summary>
    internal static bool IsNonDolbyVisionClient(SessionInfo session, PluginConfiguration? config = null)
    {
        if (IsSamsungClient(session))
        {
            return true;
        }

        if (config?.SuppressDvOnFireTv == true && IsFireTvClient(session))
        {
            return true;
        }

        var client = session.Client ?? string.Empty;
        var deviceName = session.DeviceName ?? string.Empty;

        // Check if client is Android
        bool isAndroid = client.Contains("Android", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Android", StringComparison.OrdinalIgnoreCase);

        if (isAndroid)
        {
            // TV clients running Android TV / Google TV / Fire TV / Shield DO support Dolby Vision
            bool isAndroidTvOrBox = client.Contains("Android TV", StringComparison.OrdinalIgnoreCase)
                || client.Contains("AndroidTV", StringComparison.OrdinalIgnoreCase)
                || client.Contains("Google TV", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("Shield", StringComparison.OrdinalIgnoreCase)
                || ContainsFireTvIndicator(deviceName)
                || deviceName.Contains("BRAVIA", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("TV", StringComparison.OrdinalIgnoreCase)
                || deviceName.Contains("Box", StringComparison.OrdinalIgnoreCase);

            if (!isAndroidTvOrBox)
            {
                return true;
            }
        }

        return false;
    }

    internal static HdrDetectionResult GetHdrInfo(BaseItem item, SessionInfo session, PluginConfiguration config)
    {
        if (MetadataFormatter.ShouldReportToneMappedSdr(config, session.TranscodingInfo))
        {
            return new(config.ShowSdr ? "SDR" : string.Empty, "unsupported-hdr-transcode");
        }

        var mediaStreams = GetMediaStreams(item, session);
        var videoStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        return GetHdrInfo(videoStream, item.Path ?? string.Empty, item.Name ?? string.Empty, session, config);
    }

    internal static HdrDetectionResult GetHdrInfo(
        MediaStream? videoStream,
        string itemPath,
        string itemName,
        SessionInfo session,
        PluginConfiguration config,
        ClientVideoCapabilities? capabilities = null,
        string? container = null)
    {
        if (videoStream == null)
        {
            return new(string.Empty, "no-video-stream");
        }

        var rangeType = videoStream.VideoRangeType;
        var range = videoStream.VideoRange;
        var profile = videoStream.Profile ?? string.Empty;
        var displayTitle = videoStream.DisplayTitle ?? string.Empty;

        // What the client receives: the re-encoded stream's codec/container when the server
        // transcodes the video, otherwise the source's.
        var transcoding = session.TranscodingInfo;
        var videoReencoded = transcoding != null && !transcoding.IsVideoDirect;
        var outputCodec = videoReencoded && !string.IsNullOrEmpty(transcoding!.VideoCodec) ? transcoding.VideoCodec : videoStream.Codec;
        var outputContainer = videoReencoded ? transcoding!.Container : container;

        bool isNonDvClient = ShouldSuppressDolbyVision(session, config, capabilities, outputCodec, outputContainer);
        bool isNonHdr10Plus = ShouldSuppressHdr10Plus(session, capabilities, outputCodec, outputContainer);

        // Detect HDR10+ via multilayer check: probe flag, VideoRangeType, stream metadata, or filename
        bool hasHdr10Plus = IsHdr10Plus(videoStream, itemPath, itemName);

        // Detect Dolby Vision via probe profile/flag, VideoRangeType, stream metadata, or filename
        bool hasDolbyVision = IsDolbyVision(videoStream, itemPath, itemName);

        // Non-supporting TVs/clients (e.g. LG, Sony, Roku) do not support HDR10+ hardware decoding
        if (hasHdr10Plus && isNonHdr10Plus && config.SuppressHdr10PlusOnLg)
        {
            if (hasDolbyVision && config.ShowDolbyVision)
            {
                return new(config.UseDetailedVideoNames ? GetDetailedDolbyVisionInfo(videoStream) : "Dolby Vision", "hdr10plus-client-fallback-dolby-vision");
            }

            if (HasHdr10BaseLayer(videoStream) && config.ShowHdr10)
            {
                return new(config.UseDetailedVideoNames ? GetDetailedHdr10Info() : "HDR10", "hdr10plus-client-fallback-hdr10");
            }

            if ((rangeType == VideoRangeType.DOVIWithHLG || rangeType == VideoRangeType.HLG || displayTitle.Contains("HLG", StringComparison.OrdinalIgnoreCase)) && config.ShowHlg)
            {
                return new("HLG", "hdr10plus-client-fallback-hlg");
            }

            if (config.ShowSdr)
            {
                return new("SDR", "hdr10plus-client-fallback-sdr");
            }

            return new(string.Empty, "hdr10plus-client-fallback-hidden");
        }

        // Samsung and non-DV Android clients do not support Dolby Vision hardware decoding
        if (hasDolbyVision)
        {
            if (isNonDvClient)
            {
                // Fall back to HDR10+ if the media contains HDR10+ metadata
                if (hasHdr10Plus && config.ShowHdr10Plus)
                {
                    return new(config.UseDetailedVideoNames ? GetDetailedHdr10PlusInfo(profile, displayTitle) : "HDR10+", "dolby-vision-client-fallback-hdr10plus");
                }

                // Fall back to HDR10 base layer (Profile 7/8, DOVIWithHDR10, DOVIWithEL, or standard HDR)
                if (HasHdr10BaseLayer(videoStream) && config.ShowHdr10)
                {
                    return new(config.UseDetailedVideoNames ? GetDetailedHdr10Info() : "HDR10", "dolby-vision-client-fallback-hdr10");
                }

                // Fall back to HLG if present
                if (rangeType == VideoRangeType.DOVIWithHLG && config.ShowHlg)
                {
                    return new("HLG", "dolby-vision-client-fallback-hlg");
                }

                // Fall back to SDR base layer (e.g. DOVI Profile 8.2 with SDR)
                if ((rangeType == VideoRangeType.DOVIWithSDR || rangeType == VideoRangeType.SDR) && config.ShowSdr)
                {
                    return new("SDR", "dolby-vision-client-fallback-sdr");
                }

                return new(string.Empty, "dolby-vision-client-fallback-hidden");
            }

            if (config.ShowDolbyVision)
            {
                return new(config.UseDetailedVideoNames ? GetDetailedDolbyVisionInfo(videoStream) : "Dolby Vision", "dolby-vision");
            }
        }

        if (hasHdr10Plus && config.ShowHdr10Plus)
        {
            return new(config.UseDetailedVideoNames ? GetDetailedHdr10PlusInfo(profile, displayTitle) : "HDR10+", "hdr10plus");
        }

        if (rangeType == VideoRangeType.HLG
            || displayTitle.Contains("HLG", StringComparison.OrdinalIgnoreCase)
            || string.Equals(videoStream.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase))
        {
            if (config.ShowHlg)
            {
                return new("HLG", "hlg");
            }
        }

        if (rangeType == VideoRangeType.HDR10
            || range == VideoRange.HDR
            || displayTitle.Contains("HDR10", StringComparison.OrdinalIgnoreCase)
            || string.Equals(videoStream.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase))
        {
            if (config.ShowHdr10)
            {
                return new(config.UseDetailedVideoNames ? GetDetailedHdr10Info() : "HDR10", "hdr10");
            }
        }

        if (config.ShowSdr)
        {
            return new("SDR", "sdr");
        }

        return new(string.Empty, "hidden-by-configuration");
    }

    /// <summary>
    /// Whether the toast should report the Dolby Vision fallback layer instead of Dolby Vision.
    /// Only clients that really decode DV allow plain <c>DOVI</c> (profile 5 has no fallback
    /// layer), so that entry in the device profile decides; names are a fallback for clients
    /// that report no profile.
    /// </summary>
    internal static bool ShouldSuppressDolbyVision(
        SessionInfo session,
        PluginConfiguration config,
        ClientVideoCapabilities? capabilities,
        string? codec,
        string? container)
    {
        // The profile describes the player, not the TV it is plugged into, so an explicit
        // "this Fire TV feeds a non-DV display" setting always wins.
        if (config.SuppressDvOnFireTv && IsFireTvClient(session))
        {
            return true;
        }

        if (!config.SuppressDvOnSamsung)
        {
            return false;
        }

        return (capabilities?.Supports(codec, VideoRangeType.DOVI, container) ?? RangeSupport.Unknown) switch
        {
            RangeSupport.Supported => false,
            RangeSupport.Unsupported => true,
            _ => IsNonDolbyVisionClient(session, config)
        };
    }

    /// <summary>
    /// Whether HDR10+ should be reported as its base layer. A profile that rejects
    /// <c>HDR10Plus</c> is conclusive. One that allows it is not: web clients allow HDR10+
    /// wherever they allow HDR10 (LG webOS included), so the known non-HDR10+ brands still apply.
    /// </summary>
    internal static bool ShouldSuppressHdr10Plus(
        SessionInfo session,
        ClientVideoCapabilities? capabilities,
        string? codec,
        string? container)
    {
        return capabilities?.Supports(codec, VideoRangeType.HDR10Plus, container) == RangeSupport.Unsupported
            || IsNonHdr10PlusClient(session);
    }

    internal static bool HasHdr10BaseLayer(MediaStream? videoStream)
    {
        if (videoStream == null)
        {
            return false;
        }

        // Profile 5 has no HDR10 base layer (uses proprietary IPT color space)
        if (videoStream.DvProfile == 5
            || (videoStream.Profile != null && (
                videoStream.Profile.Contains("dvhe.05", StringComparison.OrdinalIgnoreCase)
                || videoStream.Profile.Contains("dvh1.05", StringComparison.OrdinalIgnoreCase)
                || videoStream.Profile.Contains("Profile 5", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (videoStream.VideoRangeType is VideoRangeType.DOVIWithSDR or VideoRangeType.DOVIWithHLG)
        {
            return false;
        }

        var rangeType = videoStream.VideoRangeType;
        var range = videoStream.VideoRange;
        var profile = videoStream.Profile ?? string.Empty;
        var displayTitle = videoStream.DisplayTitle ?? string.Empty;

        // If it's a Dolby Vision stream, ST 2084 transfer curve alone does not imply an HDR10 base layer
        bool isDvStream = rangeType == VideoRangeType.DOVI
            || videoStream.DvProfile > 0
            || profile.Contains("dv", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("dovi", StringComparison.OrdinalIgnoreCase);

        if (isDvStream)
        {
            return rangeType is VideoRangeType.DOVIWithHDR10
                    or VideoRangeType.DOVIWithEL
                    or VideoRangeType.DOVIWithHDR10Plus
                    or VideoRangeType.DOVIWithELHDR10Plus
                || videoStream.DvProfile is 7 or 8
                || profile.Contains("dvhe.08", StringComparison.OrdinalIgnoreCase)
                || profile.Contains("dvh1.08", StringComparison.OrdinalIgnoreCase)
                || profile.Contains("dvhe.07", StringComparison.OrdinalIgnoreCase)
                || profile.Contains("dvh1.07", StringComparison.OrdinalIgnoreCase)
                || profile.Contains("Profile 8", StringComparison.OrdinalIgnoreCase)
                || profile.Contains("Profile 7", StringComparison.OrdinalIgnoreCase)
                || displayTitle.Contains("HDR10", StringComparison.OrdinalIgnoreCase);
        }

        return rangeType is VideoRangeType.HDR10
            || range == VideoRange.HDR
            || string.Equals(videoStream.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
            || displayTitle.Contains("HDR10", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDolbyVision(MediaStream? videoStream, string itemPath, string itemName)
    {
        return HdrFormatDetection.IsDolbyVision(videoStream, itemPath, itemName);
    }

    internal static bool IsHdr10Plus(MediaStream? videoStream, string itemPath, string itemName)
    {
        return HdrFormatDetection.IsHdr10Plus(videoStream, itemPath, itemName);
    }

    private static string GetDetailedDolbyVisionInfo(MediaStream videoStream)
    {
        var profile = videoStream.Profile ?? string.Empty;
        var displayTitle = videoStream.DisplayTitle ?? string.Empty;
        var combined = $"{profile} {displayTitle}";

        if (combined.Contains("dvhe.08.09", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.08.09", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 8.1", StringComparison.OrdinalIgnoreCase)
            || Profile81Regex().IsMatch(combined))
        {
            return "DV Profile 8.1";
        }

        if (combined.Contains("dvhe.08.06", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.08.06", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 8.4", StringComparison.OrdinalIgnoreCase)
            || Profile84Regex().IsMatch(combined))
        {
            return "DV Profile 8.4";
        }

        if (combined.Contains("dvhe.08", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.08", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 8", StringComparison.OrdinalIgnoreCase)
            || videoStream.DvProfile == 8)
        {
            return "DV Profile 8";
        }

        if (combined.Contains("dvhe.07.06", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.07.06", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 7.6", StringComparison.OrdinalIgnoreCase)
            || Profile76Regex().IsMatch(combined))
        {
            return "DV Profile 7.6";
        }

        if (combined.Contains("dvhe.07", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.07", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 7", StringComparison.OrdinalIgnoreCase)
            || videoStream.DvProfile == 7)
        {
            return "DV Profile 7";
        }

        if (combined.Contains("dvhe.05", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("dvh1.05", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Profile 5", StringComparison.OrdinalIgnoreCase)
            || videoStream.DvProfile == 5)
        {
            return "DV Profile 5";
        }

        if (videoStream.DvProfile.HasValue && videoStream.DvProfile.Value > 0)
        {
            return $"DV Profile {videoStream.DvProfile.Value}";
        }

        if (!string.IsNullOrEmpty(profile) && !profile.Equals("DOVI", StringComparison.OrdinalIgnoreCase))
        {
            return $"Dolby Vision ({profile})";
        }

        return "Dolby Vision";
    }

    private static string GetDetailedHdr10PlusInfo(string profile, string displayTitle)
    {
        var combined = $"{profile} {displayTitle}";
        if (combined.Contains("Profile", StringComparison.OrdinalIgnoreCase))
        {
            return $"HDR10+ ({displayTitle})";
        }

        return "HDR10+ (SMPTE ST 2094-40)";
    }

    private static string GetDetailedHdr10Info()
    {
        return "HDR10 (SMPTE ST 2084)";
    }

    internal static MediaStream? GetActiveAudioStream(BaseItem item, SessionInfo session)
    {
        var mediaStreams = GetMediaStreams(item, session);
        return GetActiveAudioStream(mediaStreams, session.PlayState?.AudioStreamIndex);
    }

    internal static MediaStream? GetActiveAudioStream(IReadOnlyList<MediaStream> mediaStreams, int? targetAudioIndex)
    {
        if (targetAudioIndex.HasValue)
        {
            var activeStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == targetAudioIndex.Value);
            if (activeStream != null)
            {
                return activeStream;
            }
        }

        return mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
            ?? mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
    }

    internal static IReadOnlyList<MediaStream> GetMediaStreams(BaseItem item, SessionInfo session, MediaSourceInfo? activeSource = null)
    {
        activeSource ??= GetActiveMediaSource(item, session);
        if (activeSource?.MediaStreams is { Count: > 0 } mediaStreams)
        {
            return mediaStreams;
        }

        return item.GetMediaStreams();
    }

    internal static MediaSourceInfo? GetActiveMediaSource(BaseItem item, SessionInfo session)
    {
        if (item is not IHasMediaSources hasMediaSources)
        {
            return null;
        }

        var mediaSources = hasMediaSources.GetMediaSources(false);
        if (mediaSources == null || mediaSources.Count == 0)
        {
            return null;
        }

        var mediaSourceId = session.PlayState?.MediaSourceId;
        return string.IsNullOrEmpty(mediaSourceId)
            ? mediaSources[0]
            : mediaSources.FirstOrDefault(source => string.Equals(source.Id, mediaSourceId, StringComparison.Ordinal));
    }

    internal static string GetAudioInfo(BaseItem item, SessionInfo session, PluginConfiguration config)
    {
        var audioStream = GetActiveAudioStream(item, session);
        return GetAudioInfo(audioStream, session, config);
    }

    internal static string GetAudioInfo(MediaStream? audioStream, SessionInfo session, PluginConfiguration config)
    {
        if (audioStream == null)
        {
            return string.Empty;
        }

        var codec = audioStream.Codec?.ToUpperInvariant() ?? string.Empty;
        var profile = audioStream.Profile ?? string.Empty;
        var title = audioStream.Title ?? string.Empty;
        var displayTitle = audioStream.DisplayTitle ?? string.Empty;
        var combined = $"{profile} {title} {displayTitle}";

        bool isAtmos = combined.Contains("Atmos", StringComparison.OrdinalIgnoreCase);
        bool isDtsx = combined.Contains("DTS:X", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("DTS-X", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("DTSX", StringComparison.OrdinalIgnoreCase);

        string displayCodec;

        if (isAtmos)
        {
            displayCodec = config.UseDetailedAudioNames ? $"{MetadataFormatter.GetBaseCodec(codec, true)} Atmos" : "Dolby Atmos";
        }
        else if (isDtsx)
        {
            displayCodec = config.UseDetailedAudioNames ? $"{MetadataFormatter.GetBaseCodec(codec, true)} DTS:X" : "DTS:X";
        }
        else if (combined.Contains("DTS-HD MA", StringComparison.OrdinalIgnoreCase)
                 || combined.Contains("DTS-HD MASTER", StringComparison.OrdinalIgnoreCase)
                 || (codec == "DTS" && profile.Equals("MA", StringComparison.OrdinalIgnoreCase)))
        {
            displayCodec = "DTS-HD MA";
        }
        else if (combined.Contains("DTS-HD HRA", StringComparison.OrdinalIgnoreCase)
                 || combined.Contains("High Resolution Audio", StringComparison.OrdinalIgnoreCase)
                 || (codec == "DTS" && profile.Equals("HRA", StringComparison.OrdinalIgnoreCase)))
        {
            displayCodec = "DTS-HD HRA";
        }
        else if (combined.Contains("DTS-HD", StringComparison.OrdinalIgnoreCase))
        {
            displayCodec = "DTS-HD";
        }
        else
        {
            displayCodec = MetadataFormatter.GetBaseCodec(codec, config.UseDetailedAudioNames);
        }

        string channelStr = MetadataFormatter.FormatAudioChannels(audioStream);
        string displayAudio = !string.IsNullOrEmpty(channelStr) ? $"{displayCodec} {channelStr}" : displayCodec;

        // Audio conversion visualization: e.g. "Dolby TrueHD 7.1 ➔ Dolby Digital 5.1"
        if (config.ShowAudioConversion
            && session.TranscodingInfo != null
            && !session.TranscodingInfo.IsAudioDirect
            && !string.IsNullOrWhiteSpace(session.TranscodingInfo.AudioCodec))
        {
            var targetCodec = MetadataFormatter.GetBaseCodec(session.TranscodingInfo.AudioCodec.ToUpperInvariant(), config.UseDetailedAudioNames);
            var targetChannels = session.TranscodingInfo.AudioChannels.HasValue
                ? MetadataFormatter.FormatChannelCount(session.TranscodingInfo.AudioChannels.Value)
                : string.Empty;

            var targetAudio = !string.IsNullOrEmpty(targetChannels) ? $"{targetCodec} {targetChannels}" : targetCodec;
            return $"{displayAudio} ➔ {targetAudio}";
        }

        return displayAudio;
    }

    internal static string GetBitrateInfo(
        BaseItem item,
        SessionInfo session,
        MediaSourceInfo? activeSource = null,
        IReadOnlyList<MediaStream>? mediaStreams = null,
        MediaStream? activeAudioStream = null)
    {
        // 1. Check transcoding bitrate first if actively transcoding
        if (session.TranscodingInfo?.Bitrate > 0)
        {
            return MetadataFormatter.FormatBitrate((long)session.TranscodingInfo.Bitrate);
        }

        // 2. Try container / media source total bitrate
        activeSource ??= GetActiveMediaSource(item, session);
        if (activeSource?.Bitrate is int totalBitrate && totalBitrate > 0)
        {
            return MetadataFormatter.FormatBitrate(totalBitrate);
        }

        if (item.TotalBitrate is int itemBitrate && itemBitrate > 0)
        {
            return MetadataFormatter.FormatBitrate(itemBitrate);
        }

        // 3. Fall back to summing video stream and active audio stream
        mediaStreams ??= GetMediaStreams(item, session, activeSource);
        var videoStream = mediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        activeAudioStream ??= GetActiveAudioStream(mediaStreams, session.PlayState?.AudioStreamIndex);
        if (videoStream?.BitRate is int videoBitrate && videoBitrate > 0)
        {
            long totalBps = videoBitrate + (activeAudioStream?.BitRate ?? 0);
            return MetadataFormatter.FormatBitrate(totalBps);
        }

        if (activeAudioStream?.BitRate is int audioOnlyBitrate && audioOnlyBitrate > 0)
        {
            return MetadataFormatter.FormatBitrate(audioOnlyBitrate);
        }

        return string.Empty;
    }

}
