using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PositionedSubtitles.Configuration;
using Jellyfin.Plugin.PositionedSubtitles.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PositionedSubtitles.Tasks;

public class ConvertPositionedSubtitlesTask : IScheduledTask
{
    private const int PageSize = 250;

    private static readonly string[] SrtCodecs = { "subrip", "srt" };

    private static readonly string[] AssCodecs = { "ass", "ssa" };

    private readonly ILibraryManager _libraryManager;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IPathManager _pathManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<ConvertPositionedSubtitlesTask> _logger;

    public ConvertPositionedSubtitlesTask(
        ILibraryManager libraryManager,
        ISubtitleEncoder subtitleEncoder,
        IMediaEncoder mediaEncoder,
        IPathManager pathManager,
        IFileSystem fileSystem,
        ILocalizationManager localization,
        ILogger<ConvertPositionedSubtitlesTask> logger)
    {
        _libraryManager = libraryManager;
        _subtitleEncoder = subtitleEncoder;
        _mediaEncoder = mediaEncoder;
        _pathManager = pathManager;
        _fileSystem = fileSystem;
        _localization = localization;
        _logger = logger;
    }

    public string Name => "Positioned subtitles maintenance";

    public string Key => "PositionedSubtitlesConvert";

    public string Description => "Repairs shifted subtitle cache indices. With sidecar files disabled (live styling) it removes .ass files it wrote earlier; otherwise it writes them.";

    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var stats = new Stats();
        var repair = new SubtitleCacheRepair(_logger, config.DryRun);
        string styleLine = AssBuilder.BuildStyleLine(config);
        string styleHash = AssBuilder.StyleHash(styleLine);
        var languages = ParseLanguages(config.Languages);

        _logger.LogInformation("Positioned subtitles: start (languages {Languages}, dry run {DryRun})", string.Join(',', languages), config.DryRun);

        var query = new InternalItemsQuery
        {
            Recursive = true,
            HasSubtitles = true,
            IsVirtualItem = false,
            IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.MusicVideo },
            MediaTypes = new[] { MediaType.Video },
            SourceTypes = new[] { SourceType.Library },
            DtoOptions = new DtoOptions(false),
            Limit = PageSize
        };

        int total = _libraryManager.GetCount(query);
        var seen = new HashSet<Guid>();
        int done = 0;
        for (int start = 0; start < total; start += PageSize)
        {
            query.StartIndex = start;
            foreach (var item in _libraryManager.GetItemList(query))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var video in ExpandVersions(item))
                {
                    if (!seen.Add(video.Id))
                    {
                        continue;
                    }

                    try
                    {
                        await ProcessVideo(video, config, languages, styleLine, styleHash, repair, stats, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        stats.Errors++;
                        _logger.LogError(ex, "Positioned subtitles: failed for {Path}", video.Path);
                    }
                }

                done++;
                progress.Report(100.0 * done / Math.Max(total, 1));
            }
        }

        progress.Report(100);
        _logger.LogInformation(
            "Positioned subtitles: done. videos {Videos}, ASS created {Created}, updated {Updated}, unchanged {Unchanged}, skipped user files {UserFiles}, cache shifted {Shifted}, cache deleted {Deleted}, extracted {Extracted}, removed sidecars {Removed}, errors {Errors}",
            seen.Count, stats.Created, stats.Updated, stats.Unchanged, stats.UserFiles, stats.Shifted, stats.Deleted, stats.Extracted, stats.Removed, stats.Errors);
    }

    private IEnumerable<Video> ExpandVersions(BaseItem item)
    {
        var result = new List<Video>();
        if (item is Video self)
        {
            result.Add(self);
        }

        foreach (var source in item.GetMediaSources(false))
        {
            if (Guid.TryParse(source.Id, out var id) && id != item.Id && _libraryManager.GetItemById(id) is Video other)
            {
                result.Add(other);
            }
        }

        return result;
    }

    private async Task ProcessVideo(
        Video video,
        PluginConfiguration config,
        IReadOnlyCollection<string> languages,
        string styleLine,
        string styleHash,
        SubtitleCacheRepair repair,
        Stats stats,
        CancellationToken ct)
    {
        if (!video.IsFileProtocol || video.VideoType != VideoType.VideoFile || video.IsPlaceHolder
            || string.IsNullOrEmpty(video.Path) || !File.Exists(video.Path))
        {
            return;
        }

        string sourceId = video.Id.ToString("N");
        string? folder = _pathManager.GetSubtitleFolderPath(sourceId);
        if (folder is null)
        {
            return;
        }

        var streams = video.GetMediaStreams();
        if (config.RepairShiftedCache)
        {
            switch (repair.Repair(folder, streams, video.Path))
            {
                case CacheState.Shifted: stats.Shifted++; break;
                case CacheState.Deleted: stats.Deleted++; break;
            }
        }

        if (!config.WriteSidecarFiles)
        {
            await RemoveOwnSidecars(video, folder, streams, config, repair, stats, ct).ConfigureAwait(false);

            // Live styling decides per request from the cache whether an SRT track has {\anN}; make sure it exists.
            var current = (_libraryManager.GetItemById(video.Id) as Video ?? video).GetMediaStreams();
            bool missing = current.Any(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal
                                            && SrtCodecs.Contains(s.Codec ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                                            && MatchesLanguage(s.Language, languages)
                                            && _pathManager.GetSubtitlePath(sourceId, s.Index, ".srt") is string p && !File.Exists(p));
            if (missing && config.ExtractMissing && !config.DryRun)
            {
                var mediaSource = video.GetMediaSources(false).FirstOrDefault(m => string.Equals(m.Id, sourceId, StringComparison.OrdinalIgnoreCase));
                if (mediaSource is not null)
                {
                    _logger.LogInformation("Positioned subtitles: extracting embedded subtitles of {Path}", video.Path);
                    await _subtitleEncoder.ExtractAllExtractableSubtitles(mediaSource, ct).ConfigureAwait(false);
                    repair.WriteState(folder, SubtitleCacheRepair.ExternalOffset(current));
                    stats.Extracted++;
                }
            }

            return;
        }

        var candidates = streams
            .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal
                        && (SrtCodecs.Contains(s.Codec ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                            || (config.RestyleEmbeddedAss && AssCodecs.Contains(s.Codec ?? string.Empty, StringComparer.OrdinalIgnoreCase)))
                        && MatchesLanguage(s.Language, languages))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        bool extracted = false;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0;
        string dir = Path.GetDirectoryName(video.Path)!;
        string baseName = Path.GetFileNameWithoutExtension(video.Path);

        foreach (var stream in candidates)
        {
            bool isAss = AssCodecs.Contains(stream.Codec ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            string? cachePath = isAss ? FindAssCache(sourceId, stream.Index) : _pathManager.GetSubtitlePath(sourceId, stream.Index, ".srt");
            if (cachePath is null)
            {
                continue;
            }

            if (!File.Exists(cachePath) && config.ExtractMissing && !extracted && !config.DryRun)
            {
                var mediaSource = video.GetMediaSources(false).FirstOrDefault(m => string.Equals(m.Id, sourceId, StringComparison.OrdinalIgnoreCase));
                if (mediaSource is not null)
                {
                    _logger.LogInformation("Positioned subtitles: extracting embedded subtitles of {Path}", video.Path);
                    await _subtitleEncoder.ExtractAllExtractableSubtitles(mediaSource, ct).ConfigureAwait(false);
                    repair.WriteState(folder, SubtitleCacheRepair.ExternalOffset(streams));
                    stats.Extracted++;
                }

                extracted = true;
                if (isAss)
                {
                    cachePath = FindAssCache(sourceId, stream.Index) ?? cachePath;
                }
            }

            if (!File.Exists(cachePath))
            {
                _logger.LogDebug("Positioned subtitles: no cached subtitle {Index} for {Path}", stream.Index, video.Path);
                continue;
            }

            string source = await File.ReadAllTextAsync(cachePath, Encoding.UTF8, ct).ConfigureAwait(false);
            int positioned = AssBuilder.PositionTag.Matches(source).Count;
            if (!isAss && config.OnlyPositioned && positioned == 0)
            {
                continue;
            }

            string ass;
            if (isAss)
            {
                ass = AssBuilder.Restyle(source, config.PrimaryColor, styleHash, out int changedStyles);
                if (changedStyles == 0)
                {
                    _logger.LogDebug("Positioned subtitles: {Path} stream {Index} has no white styles to recolour", video.Path, stream.Index);
                    continue;
                }

                if (AssBuilder.CountDialogues(ass) != AssBuilder.CountDialogues(source))
                {
                    stats.Errors++;
                    _logger.LogWarning("Positioned subtitles: restyle check failed for {Path} stream {Index}", video.Path, stream.Index);
                    continue;
                }
            }
            else
            {
                ass = AssBuilder.Patch(await AssBuilder.ConvertAsync(_mediaEncoder.EncoderPath, cachePath, ct).ConfigureAwait(false), styleLine, styleHash);
                int dialogues = AssBuilder.CountDialogues(ass);
                int cues = AssBuilder.CountSrtCues(source);
                int assPositioned = AssBuilder.PositionTag.Matches(ass).Count;
                if (dialogues == 0 || dialogues < cues * 0.9 || assPositioned != positioned)
                {
                    stats.Errors++;
                    _logger.LogWarning("Positioned subtitles: conversion check failed for {Path} stream {Index} (dialogues {D}/{C}, positioned {P}/{PS})", video.Path, stream.Index, dialogues, cues, assPositioned, positioned);
                    continue;
                }
            }

            string target = TargetPath(dir, baseName, stream, streams, usedNames);
            bool exists = File.Exists(target);
            if (exists && !AssBuilder.IsOwnFile(target, out _))
            {
                stats.UserFiles++;
                _logger.LogInformation("Positioned subtitles: {Target} exists and was not created by this plugin, skipping", target);
                continue;
            }

            if (exists && string.Equals(await File.ReadAllTextAsync(target, Encoding.UTF8, ct).ConfigureAwait(false), ass, StringComparison.Ordinal))
            {
                stats.Unchanged++;
                continue;
            }

            if (config.DryRun)
            {
                _logger.LogInformation("[dry run] would {Action} {Target} ({Positioned} positioned lines)", exists ? "update" : "create", target, positioned);
            }
            else
            {
                string tmp = target + ".tmp";
                await File.WriteAllTextAsync(tmp, ass, new UTF8Encoding(false), ct).ConfigureAwait(false);
                File.Move(tmp, target, true);
                _logger.LogInformation("Positioned subtitles: {Action} {Target} ({Positioned} positioned lines)", exists ? "updated" : "created", target, positioned);
            }

            if (exists)
            {
                stats.Updated++;
            }
            else
            {
                stats.Created++;
                created++;
            }
        }

        if (created > 0 && !config.DryRun)
        {
            await RefreshWithCacheShift(video, folder, streams, created, repair, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Live styling replaces sidecar files: delete the ones this plugin wrote earlier.</summary>
    private async Task RemoveOwnSidecars(Video video, string folder, IReadOnlyList<MediaStream> streams, PluginConfiguration config, SubtitleCacheRepair repair, Stats stats, CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(video.Path)!;
        string baseName = Path.GetFileNameWithoutExtension(video.Path);
        var own = Directory.EnumerateFiles(dir, baseName + ".*.ass")
            .Where(f => AssBuilder.IsOwnFile(f, out _))
            .ToList();
        if (own.Count == 0)
        {
            return;
        }

        // Count only files Jellyfin actually knows as external streams of this item.
        int known = streams.Count(s => s.IsExternal && s.Path is not null && own.Contains(s.Path, StringComparer.Ordinal));
        foreach (string f in own)
        {
            if (config.DryRun)
            {
                _logger.LogInformation("[dry run] would remove {File}", f);
                continue;
            }

            File.Delete(f);
            stats.Removed++;
            _logger.LogInformation("Positioned subtitles: removed {File} (live styling replaces it)", f);
        }

        if (!config.DryRun)
        {
            await RefreshWithCacheShift(video, folder, streams, -known, repair, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// New external files shift every embedded stream index. Rename the cache first so that
    /// providers running during the refresh (e.g. Subtitle Extract) see consistent files,
    /// then correct for anything else the refresh added (e.g. downloaded subtitles).
    /// </summary>
    private async Task RefreshWithCacheShift(Video video, string folder, IReadOnlyList<MediaStream> before, int delta, SubtitleCacheRepair repair, CancellationToken ct)
    {
        int offsetBefore = SubtitleCacheRepair.ExternalOffset(before);
        int predicted = offsetBefore + delta;
        repair.ShiftAll(folder, delta, predicted);

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false
        };
        await video.RefreshMetadata(options, ct).ConfigureAwait(false);

        var after = (_libraryManager.GetItemById(video.Id) as Video ?? video).GetMediaStreams();
        int offsetAfter = SubtitleCacheRepair.ExternalOffset(after);
        if (offsetAfter != predicted)
        {
            _logger.LogInformation("Positioned subtitles: {Path} has {After} external streams after refresh, expected {Expected}; adjusting cache", video.Path, offsetAfter, predicted);
            repair.ShiftAll(folder, offsetAfter - predicted, offsetAfter);
        }
    }

    private string? FindAssCache(string sourceId, int index)
    {
        string? ass = _pathManager.GetSubtitlePath(sourceId, index, ".ass");
        string? ssa = _pathManager.GetSubtitlePath(sourceId, index, ".ssa");
        if (ssa is not null && File.Exists(ssa) && (ass is null || !File.Exists(ass)))
        {
            return ssa;
        }

        return ass;
    }

    private string TargetPath(string dir, string baseName, MediaStream stream, IReadOnlyList<MediaStream> streams, HashSet<string> used)
    {
        string lang = stream.Language ?? "und";
        var culture = _localization.FindLanguageInfo(lang);
        if (!string.IsNullOrEmpty(culture?.TwoLetterISOLanguageName))
        {
            lang = culture.TwoLetterISOLanguageName;
        }

        string flags = (stream.IsForced ? ".forced" : string.Empty) + (stream.IsHearingImpaired ? ".sdh" : string.Empty);
        string name = $"{baseName}.{lang}{flags}.ass";
        if (!used.Add(name))
        {
            int ffIndex = stream.Index - SubtitleCacheRepair.ExternalOffset(streams);
            name = $"{baseName}.{lang}{flags}.track{ffIndex}.ass";
            used.Add(name);
        }

        return Path.Combine(dir, name);
    }

    private bool MatchesLanguage(string? language, IReadOnlyCollection<string> wanted)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        if (wanted.Contains(language))
        {
            return true;
        }

        var culture = _localization.FindLanguageInfo(language);
        if (culture is null)
        {
            return false;
        }

        return wanted.Contains(culture.TwoLetterISOLanguageName)
               || culture.ThreeLetterISOLanguageNames.Any(wanted.Contains);
    }

    private static HashSet<string> ParseLanguages(string? value)
    {
        return (value ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Stats
    {
        public int Created;
        public int Updated;
        public int Unchanged;
        public int UserFiles;
        public int Shifted;
        public int Deleted;
        public int Extracted;
        public int Removed;
        public int Errors;
    }
}
