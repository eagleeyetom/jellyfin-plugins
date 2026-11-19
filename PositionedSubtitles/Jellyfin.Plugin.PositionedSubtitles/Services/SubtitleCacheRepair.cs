using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PositionedSubtitles.Services;

public enum CacheState
{
    Missing,
    Consistent,
    Shifted,
    Deleted
}

/// <summary>
/// Jellyfin stores extracted subtitles as data/subtitles/xx/{id}/{streamIndex}.{ext} and validates
/// them only against the size/mtime of the video file. Stream indices are assigned as
/// [external subtitles][external audio][embedded streams], so adding or removing an external
/// file shifts every embedded index and the cache silently serves the wrong track.
/// This class keeps the cache names in sync with the current external stream count ("offset").
/// </summary>
public sealed class SubtitleCacheRepair
{
    public const string StateFileName = ".positioned-subs.offset";

    private static readonly Regex DataFile = new(@"^(\d+)\.([A-Za-z0-9]+)$", RegexOptions.Compiled);

    private readonly ILogger _logger;
    private readonly bool _dryRun;

    public SubtitleCacheRepair(ILogger logger, bool dryRun)
    {
        _logger = logger;
        _dryRun = dryRun;
    }

    public static int ExternalOffset(IReadOnlyList<MediaStream> streams) => streams.Count(s => s.IsExternal);

    public CacheState Repair(string folder, IReadOnlyList<MediaStream> streams, string itemPath)
    {
        if (!Directory.Exists(folder))
        {
            return CacheState.Missing;
        }

        int offsetNow = ExternalOffset(streams);
        var files = ListDataFiles(folder);
        if (files.Count == 0)
        {
            WriteState(folder, offsetNow);
            return CacheState.Consistent;
        }

        var (stateOffset, stateTime) = ReadState(folder);
        if (stateOffset == offsetNow)
        {
            return CacheState.Consistent;
        }

        if (stateOffset is int known)
        {
            // Files not newer than the state file were named with the recorded offset;
            // newer ones were written by Jellyfin afterwards, i.e. with the current offset.
            int delta = offsetNow - known;
            var toMove = files.Where(f => f.Modified <= stateTime).ToList();
            _logger.LogInformation("Subtitle cache of {Path}: recorded offset {Old}, current {New}; shifting {Count} file(s) by {Delta}", itemPath, known, offsetNow, toMove.Count, delta);
            Shift(folder, files, toMove, delta, deleteBelow: null);
            WriteState(folder, offsetNow);
            return CacheState.Shifted;
        }

        // No record: infer the offset the files were written with.
        var embeddedSubs = streams.Where(s => !s.IsExternal && s.Type == MediaStreamType.Subtitle).ToList();
        if (embeddedSubs.Count == 0)
        {
            // Only conversions of external files can be here; they are cheap to regenerate.
            if (files.Any(f => f.Index >= offsetNow))
            {
                Delete(folder, itemPath, "no embedded subtitles but stray cache files");
                return CacheState.Deleted;
            }

            WriteState(folder, offsetNow);
            return CacheState.Consistent;
        }

        var allFf = embeddedSubs.Select(s => s.Index - offsetNow).ToHashSet();
        var textFf = embeddedSubs.Where(s => s.IsTextSubtitleStream).Select(s => s.Index - offsetNow).ToHashSet();

        var valid = new List<(int Offset, bool Exact)>();
        int maxOffset = Math.Max(offsetNow, 0) + 16;
        for (int o = 0; o <= maxOffset; o++)
        {
            var mapped = files.Where(f => f.Index >= o).Select(f => f.Index - o).ToList();
            if (mapped.Count == 0 || !mapped.All(allFf.Contains))
            {
                continue;
            }

            bool exact = textFf.Count > 0 && textFf.IsSubsetOf(mapped);
            valid.Add((o, exact));
        }

        var exactOnes = valid.Where(v => v.Exact).ToList();
        int? chosen = null;
        if (exactOnes.Count == 1)
        {
            chosen = exactOnes[0].Offset;
        }
        else if (exactOnes.Count == 0 && valid.Count == 1)
        {
            chosen = valid[0].Offset;
        }
        else if (exactOnes.Count > 1 && exactOnes.Any(v => v.Offset == offsetNow))
        {
            chosen = offsetNow;
        }

        if (chosen is null)
        {
            Delete(folder, itemPath, valid.Count == 0 ? "cache matches no stream layout" : "cache layout is ambiguous");
            return CacheState.Deleted;
        }

        if (chosen == offsetNow)
        {
            WriteState(folder, offsetNow);
            return CacheState.Consistent;
        }

        int shift = offsetNow - chosen.Value;
        _logger.LogInformation("Subtitle cache of {Path} was written with offset {Old}, current offset is {New}; shifting by {Delta}", itemPath, chosen, offsetNow, shift);
        Shift(folder, files, files, shift, deleteBelow: chosen.Value);
        WriteState(folder, offsetNow);
        return CacheState.Shifted;
    }

    /// <summary>Renames the whole cache by <paramref name="delta"/> before Jellyfin re-indexes the item.</summary>
    public void ShiftAll(string folder, int delta, int newOffset)
    {
        if (!Directory.Exists(folder) || delta == 0)
        {
            return;
        }

        var files = ListDataFiles(folder);
        var (_, stateTime) = ReadState(folder);
        var toMove = stateTime == DateTime.MinValue ? files : files.Where(f => f.Modified <= stateTime).ToList();
        Shift(folder, files, toMove, delta, deleteBelow: null);
        WriteState(folder, newOffset);
    }

    public void WriteState(string folder, int offset)
    {
        if (_dryRun || !Directory.Exists(folder))
        {
            return;
        }

        File.WriteAllText(Path.Combine(folder, StateFileName), offset.ToString(CultureInfo.InvariantCulture));
    }

    private void Shift(string folder, List<CacheFile> all, List<CacheFile> toMove, int delta, int? deleteBelow)
    {
        if (_dryRun)
        {
            _logger.LogInformation("[dry run] would shift {Count} cache file(s) in {Folder} by {Delta}", toMove.Count, folder, delta);
            return;
        }

        var moving = new HashSet<string>(toMove.Select(f => f.Path), StringComparer.Ordinal);
        var staying = all.Where(f => !moving.Contains(f.Path)).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var staged = new List<(string Tmp, string Final, string? TmpMeta, string FinalMeta)>();

        foreach (var f in toMove)
        {
            int newIndex = f.Index + delta;
            if (newIndex < 0 || (deleteBelow is int below && f.Index < below))
            {
                DeleteWithMeta(f.Path);
                continue;
            }

            string finalName = newIndex.ToString(CultureInfo.InvariantCulture) + "." + f.Extension;
            if (staying.Contains(finalName))
            {
                // A newer file already sits at the target index; it was written with the right index.
                DeleteWithMeta(f.Path);
                continue;
            }

            string tmp = Path.Combine(folder, ".posubs-tmp-" + finalName);
            File.Move(f.Path, tmp, true);
            string meta = f.Path + ".meta";
            string? tmpMeta = null;
            if (File.Exists(meta))
            {
                tmpMeta = tmp + ".meta";
                File.Move(meta, tmpMeta, true);
            }

            string final = Path.Combine(folder, finalName);
            staged.Add((tmp, final, tmpMeta, final + ".meta"));
        }

        foreach (var s in staged)
        {
            File.Move(s.Tmp, s.Final, true);
            if (s.TmpMeta is not null)
            {
                File.Move(s.TmpMeta, s.FinalMeta, true);
            }
            else if (File.Exists(s.FinalMeta))
            {
                File.Delete(s.FinalMeta);
            }
        }
    }

    private void Delete(string folder, string itemPath, string reason)
    {
        _logger.LogWarning("Deleting subtitle cache of {Path} ({Reason}); Jellyfin will extract it again", itemPath, reason);
        if (!_dryRun)
        {
            Directory.Delete(folder, true);
        }
    }

    private static void DeleteWithMeta(string path)
    {
        File.Delete(path);
        if (File.Exists(path + ".meta"))
        {
            File.Delete(path + ".meta");
        }
    }

    private static (int? Offset, DateTime Time) ReadState(string folder)
    {
        string path = Path.Combine(folder, StateFileName);
        if (!File.Exists(path))
        {
            return (null, DateTime.MinValue);
        }

        string text = File.ReadAllText(path).Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? (v, File.GetLastWriteTimeUtc(path))
            : (null, DateTime.MinValue);
    }

    private static List<CacheFile> ListDataFiles(string folder)
    {
        var result = new List<CacheFile>();
        foreach (string path in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(path);
            var m = DataFile.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
            {
                result.Add(new CacheFile(idx, m.Groups[2].Value, name, path, File.GetLastWriteTimeUtc(path)));
            }
        }

        return result;
    }

    private sealed record CacheFile(int Index, string Extension, string Name, string Path, DateTime Modified);
}
