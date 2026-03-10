using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BcMoosic.Organization;

/// <summary>
/// Port of organizer.py — reads audio tags from extracted files and moves them into
/// &lt;musicDir&gt;/&lt;Artist&gt;/&lt;Album&gt;/&lt;track&gt;.
/// </summary>
public class TrackOrganizer
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav", ".aiff", ".aif", ".alac" };

    // Computed once at startup — avoids calling GetInvalidFileNameChars() + Array.IndexOf
    // per character on every SanitizeName call.
    private static readonly HashSet<char> InvalidFileNameChars = new(Path.GetInvalidFileNameChars());

    private readonly ILogger<TrackOrganizer> _log;

    public TrackOrganizer(ILogger<TrackOrganizer> log)
    {
        _log = log;
    }

    /// <summary>
    /// Walk <paramref name="extractDir"/>, read audio tags, and move files into
    /// <paramref name="musicDir"/>. Returns the destination artist/album directory.
    /// </summary>
    public Task<string> OrganizeAsync(string extractDir, string musicDir, CancellationToken ct = default)
    {
        var audioFiles = Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        if (audioFiles.Count == 0)
        {
            _log.LogWarning("No audio files found in {ExtractDir}", extractDir);
            return Task.FromResult(string.Empty);
        }

        // Resolve music dir once for bounds-checking
        var musicDirFull = Path.GetFullPath(musicDir).TrimEnd(Path.DirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;

        // Build a case-insensitive name→path cache of existing artist dirs once, rather than
        // doing a full Directory.GetDirectories scan for every file in the archive (O(n×m) → O(n+m)).
        var artistDirCache = BuildArtistDirCache(musicDir);

        string? destDir = null;

        foreach (var filePath in audioFiles)
        {
            ct.ThrowIfCancellationRequested();

            var (artist, album) = ReadTags(filePath);

            artist = SanitizeName(artist.Length > 0 ? artist : Path.GetFileName(Path.GetDirectoryName(filePath) ?? "Unknown Artist"));
            album  = SanitizeName(album.Length  > 0 ? album  : Path.GetFileName(Path.GetDirectoryName(filePath) ?? "Unknown Album"));

            var artistDir = FindOrCreateArtistDir(musicDir, artist, artistDirCache);
            var albumDir  = Path.Combine(artistDir, album);

            // Prevent path traversal: resolved path must stay within musicDir
            var albumDirFull = Path.GetFullPath(albumDir).TrimEnd(Path.DirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;
            if (!albumDirFull.StartsWith(musicDirFull, StringComparison.Ordinal))
            {
                _log.LogWarning("Skipping file with path-traversal tags: artist={A} album={B}", artist, album);
                continue;
            }

            Directory.CreateDirectory(albumDir);

            var destFile = Path.Combine(albumDir, Path.GetFileName(filePath));
            MoveOrReplace(filePath, destFile);
            _log.LogDebug("Moved {File} → {Dest}", filePath, destFile);

            destDir ??= albumDir;
        }

        return Task.FromResult(destDir ?? musicDir);
    }

    private static (string artist, string album) ReadTags(string filePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var artist =
                tagFile.Tag.AlbumArtists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
                ?? tagFile.Tag.Performers.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
                ?? string.Empty;
            var album = tagFile.Tag.Album ?? string.Empty;
            return (artist.Trim(), album.Trim());
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException
                                       or TagLib.CorruptFileException
                                       or IOException
                                       or UnauthorizedAccessException)
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Scan <paramref name="musicDir"/> once and return a case-insensitive
    /// dictionary of existing artist folder names to their full paths.
    /// </summary>
    private static Dictionary<string, string> BuildArtistDirCache(string musicDir)
    {
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(musicDir)) return cache;
        foreach (var dir in Directory.GetDirectories(musicDir))
            cache[Path.GetFileName(dir)] = dir;
        return cache;
    }

    /// <summary>
    /// Return an existing artist directory (case-insensitive) from the cache, or
    /// create a new one and add it to the cache for subsequent files in the same batch.
    /// </summary>
    private static string FindOrCreateArtistDir(string musicDir, string artist, Dictionary<string, string> cache)
    {
        if (cache.TryGetValue(artist, out var existing)) return existing;
        var newDir = Path.Combine(musicDir, artist);
        Directory.CreateDirectory(newDir);
        cache[artist] = newDir;
        return newDir;
    }

    /// <summary>
    /// Strip characters that are invalid in file/directory names and prevent
    /// path traversal via special names such as ".." or ".".
    /// Uses a pre-built <see cref="HashSet{T}"/> for O(1) per-character lookup.
    /// </summary>
    private static string SanitizeName(string name)
    {
        var result = new string(name.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c).ToArray());
        result = result.Trim().TrimEnd('.');

        // Reject path-traversal sequences and empty results
        if (result is ".." or "." || result.Length == 0)
            result = "_";

        return result;
    }

    private static void MoveOrReplace(string src, string dest)
    {
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(src, dest);
    }
}
