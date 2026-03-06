using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BcMoosic.Configuration;

/// <summary>
/// Plugin settings persisted by Jellyfin as XML under {data}/plugins/configurations/.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Initializes a new instance with sensible defaults.</summary>
    public PluginConfiguration()
    {
        BandcampUsername = string.Empty;
        BandcampCookies = string.Empty;
        DefaultFormat = "mp3-320";
        MusicDirectory = string.Empty;   // empty = auto-detect from Jellyfin's music library
        TempDirectory = "/tmp/bcmoosic";
    }

    /// <summary>Bandcamp username (profile slug).</summary>
    public string BandcampUsername { get; set; }

    /// <summary>
    /// Bandcamp session cookies serialised as a JSON object, e.g. {"identity":"..."}.
    /// The identity cookie is obtained via Firefox + Cookie Editor on mobile.
    /// </summary>
    public string BandcampCookies { get; set; }

    /// <summary>Default download format. One of: mp3-320, flac, aac-hi, vorbis, alac, wav, aiff-lossless.</summary>
    public string DefaultFormat { get; set; }

    /// <summary>
    /// Absolute path to the music collection root. When empty the plugin uses the
    /// first Music library path configured in Jellyfin.
    /// </summary>
    public string MusicDirectory { get; set; }

    /// <summary>Directory for in-progress downloads. Defaults to /tmp/bcmoosic.</summary>
    public string TempDirectory { get; set; }
}
