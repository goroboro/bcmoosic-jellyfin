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

        // Read tags from all files; group by artist/album
        string? destDir = null;

        foreach (var filePath in audioFiles)
        {
            ct.ThrowIfCancellationRequested();

            var (artist, album) = ReadTags(filePath);

            // Sanitize
            artist = SanitizeName(artist.Length > 0 ? artist : Path.GetFileName(Path.GetDirectoryName(filePath) ?? "Unknown Artist"));
            album = SanitizeName(album.Length > 0 ? album : Path.GetFileName(Path.GetDirectoryName(filePath) ?? "Unknown Album"));

            var artistDir = FindOrCreateArtistDir(musicDir, artist);
            var albumDir = Path.Combine(artistDir, album);
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
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>Find an existing artist folder (case-insensitive match) or create one.</summary>
    private static string FindOrCreateArtistDir(string musicDir, string artist)
    {
        if (Directory.Exists(musicDir))
        {
            foreach (var dir in Directory.GetDirectories(musicDir))
            {
                if (string.Equals(Path.GetFileName(dir), artist, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        var newDir = Path.Combine(musicDir, artist);
        Directory.CreateDirectory(newDir);
        return newDir;
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidPathChars();
        var result = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return result.Trim().TrimEnd('.');
    }

    private static void MoveOrReplace(string src, string dest)
    {
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(src, dest);
    }
}
