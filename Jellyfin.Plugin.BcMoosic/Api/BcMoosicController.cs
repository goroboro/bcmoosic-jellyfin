using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.BcMoosic.Bandcamp;
using Jellyfin.Plugin.BcMoosic.Download;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BcMoosic.Api;

/// <summary>
/// Serves the bcMoosic web app and all its API endpoints under /BcMoosic/.
/// Access requires no Jellyfin auth — relies on network-level security (VPN / LAN).
/// </summary>
[ApiController]
[Route("BcMoosic")]
[AllowAnonymous]
public class BcMoosicController : ControllerBase
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav", ".aiff", ".aif" };

    private readonly BandcampClient _bc;
    private readonly DownloadManager _dm;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<BcMoosicController> _log;

    public BcMoosicController(
        BandcampClient bc,
        DownloadManager dm,
        ILibraryManager libraryManager,
        ILogger<BcMoosicController> log)
    {
        _bc = bc;
        _dm = dm;
        _libraryManager = libraryManager;
        _log = log;
    }

    // ---------------------------------------------------------------
    // Web app — serve embedded HTML/JS/CSS
    // ---------------------------------------------------------------

    [HttpGet]
    public IActionResult Index() => ServeResource("Web.index.html", "text/html; charset=utf-8");

    [HttpGet("static/{filename}")]
    public IActionResult Static(string filename)
    {
        var (resourceSuffix, contentType) = filename switch
        {
            "app.js"       => ("Web.app.js",        "application/javascript; charset=utf-8"),
            "style.css"    => ("Web.style.css",      "text/css; charset=utf-8"),
            "manifest.json"=> ("Web.manifest.json",  "application/manifest+json"),
            _              => (null, null),
        };
        if (resourceSuffix is null) return NotFound();
        return ServeResource(resourceSuffix, contentType!);
    }

    private IActionResult ServeResource(string suffix, string contentType)
    {
        var name = $"Jellyfin.Plugin.BcMoosic.{suffix}";
        var stream = GetType().Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            _log.LogError("Embedded resource not found: {Name}", name);
            return NotFound();
        }
        return File(stream, contentType);
    }

    // ---------------------------------------------------------------
    // Auth
    // ---------------------------------------------------------------

    [HttpGet("api/auth/status")]
    public async Task<ActionResult<AuthStatusResponse>> AuthStatus(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        _log.LogError("BcMoosic AuthStatus: cfg.MusicDirectory={Dir} cfg.BandcampUsername={U}",
            cfg?.MusicDirectory ?? "(null)", cfg?.BandcampUsername ?? "(null)");
        if (cfg is not null) _bc.LoadFromConfig(cfg);
        var authenticated = await _bc.VerifyAsync(ct).ConfigureAwait(false);
        return new AuthStatusResponse(
            authenticated,
            _bc.Username,
            cfg?.DefaultFormat ?? "mp3-320",
            GetMusicDir(cfg));
    }

    [HttpPost("api/auth/cookies")]
    public async Task<IActionResult> SetCookies([FromBody] CookieRequest req, CancellationToken ct)
    {
        var cookies = new Dictionary<string, string> { ["identity"] = req.Identity };
        _bc.Configure(req.Username, cookies);

        var username = req.Username.Trim();
        if (string.IsNullOrEmpty(username))
            username = await _bc.DetectUsernameAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(username))
            _log.LogWarning("SetCookies: username detection failed; collection will not load without a username");

        _bc.Configure(username, cookies);
        SaveToCfg(cfg =>
        {
            cfg.BandcampUsername = username;
            cfg.BandcampCookies = JsonSerializer.Serialize(cookies);
        });
        _log.LogInformation("SetCookies: saved cookies for username={U}", username);
        return Ok(new { ok = true, username });
    }

    // ---------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------

    [HttpGet("api/settings")]
    public IActionResult GetSettings()
    {
        var cfg = Plugin.Instance?.Configuration;
        return Ok(new
        {
            defaultFormat = cfg?.DefaultFormat ?? "mp3-320",
            musicDir = GetMusicDir(cfg),
            tempDir = cfg?.TempDirectory ?? "/tmp/bcmoosic",
        });
    }

    [HttpPost("api/settings")]
    public IActionResult UpdateSettings([FromBody] SettingsRequest req)
    {
        _log.LogError("BcMoosic UpdateSettings: req.MusicDir={Dir} req.DefaultFormat={Fmt}",
            req.MusicDir ?? "(null)", req.DefaultFormat ?? "(null)");
        SaveToCfg(cfg =>
        {
            if (req.DefaultFormat is not null) cfg.DefaultFormat = req.DefaultFormat;
            if (req.MusicDir is not null) cfg.MusicDirectory = req.MusicDir;
            if (req.TempDir is not null) cfg.TempDirectory = req.TempDir;
        });
        var savedDir = Plugin.Instance?.Configuration.MusicDirectory ?? "(null)";
        _log.LogError("BcMoosic UpdateSettings: after save cfg.MusicDirectory={Dir}", savedDir);
        return Ok(new { ok = true });
    }

    // ---------------------------------------------------------------
    // Bandcamp collection, wishlist, following
    // ---------------------------------------------------------------

    [HttpGet("api/purchases")]
    public async Task<ActionResult<CollectionResponse>> GetPurchases(CancellationToken ct)
    {
        try
        {
            var result = await _bc.GetCollectionAsync(ct).ConfigureAwait(false);
            return new CollectionResponse(
                result.Items.Select(i => new CollectionItemDto(
                    i.SaleItemId, i.ItemType, i.Artist, i.Title, i.Purchased, i.ArtUrl, i.RedownloadUrl, i.Token))
                .ToList(),
                result.MoreAvailable,
                result.LastToken);
        }
        catch (BandcampException ex)
        {
            _log.LogWarning("GetPurchases failed: {Msg}", ex.Message);
            return Problem(ex.Message, statusCode: 502);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetPurchases unexpected error");
            throw;
        }
    }

    [HttpGet("api/wishlist")]
    public async Task<ActionResult<WishlistResponse>> GetWishlist(CancellationToken ct)
    {
        try
        {
            var result = await _bc.GetWishlistAsync(ct).ConfigureAwait(false);
            var dtos = result.Items.Select(i => new WishlistItemDto(i.Artist, i.Title, i.ItemType, i.ArtUrl, i.ItemUrl)).ToList();
            _log.LogError("BcMoosic GetWishlist controller: {Count} DTOs, first={A}/{T}",
                dtos.Count, dtos.FirstOrDefault()?.Artist ?? "(none)", dtos.FirstOrDefault()?.Title ?? "(none)");
            return new WishlistResponse(dtos);
        }
        catch (BandcampException ex)
        {
            _log.LogWarning("GetWishlist failed: {Msg}", ex.Message);
            return Problem(ex.Message, statusCode: 502);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetWishlist unexpected error");
            throw;
        }
    }

    [HttpGet("api/following")]
    public async Task<ActionResult<FollowingResponse>> GetFollowing(CancellationToken ct)
    {
        try
        {
            var result = await _bc.GetFollowingAsync(ct).ConfigureAwait(false);
            return new FollowingResponse(
                result.Bands.Select(b => new FollowingBandDto(b.Name, b.Url, b.ImageUrl)).ToList());
        }
        catch (BandcampException ex)
        {
            _log.LogWarning("GetFollowing failed: {Msg}", ex.Message);
            return Problem(ex.Message, statusCode: 502);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetFollowing unexpected error");
            throw;
        }
    }

    // ---------------------------------------------------------------
    // Downloads
    // ---------------------------------------------------------------

    [HttpPost("api/downloads")]
    public async Task<IActionResult> QueueDownload([FromBody] DownloadRequest req, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var format = req.Format ?? cfg?.DefaultFormat ?? "mp3-320";
        var jobId = await _dm.EnqueueAsync(
            req.SaleItemId, req.ItemType, req.RedownloadUrl, req.Artist, req.Title, format, ct)
            .ConfigureAwait(false);
        return Ok(new { jobId });
    }

    [HttpGet("api/downloads")]
    public ActionResult<IReadOnlyList<DownloadJobResponse>> ListDownloads()
        => Ok(_dm.ListJobs().Select(DtoMapper.ToDto).ToList());

    [HttpGet("api/downloads/{id:guid}")]
    public ActionResult<DownloadJobResponse> GetDownload(Guid id)
    {
        var job = _dm.GetJob(id);
        if (job is null) return NotFound();
        return DtoMapper.ToDto(job);
    }

    [HttpDelete("api/downloads/{id:guid}")]
    public IActionResult CancelDownload(Guid id)
        => _dm.CancelJob(id) ? Ok(new { ok = true }) : NotFound();

    // ---------------------------------------------------------------
    // Local collection browser
    // ---------------------------------------------------------------

    [HttpGet("api/collection/browse")]
    public ActionResult<LocalCollectionResponse> BrowseCollection()
    {
        var cfg = Plugin.Instance?.Configuration;
        var musicDir = GetMusicDir(cfg);

        _log.LogError("BcMoosic BrowseCollection: musicDir={Dir} exists={Exists}", musicDir, Directory.Exists(musicDir));
        if (!Directory.Exists(musicDir))
            return new LocalCollectionResponse(Array.Empty<ArtistDto>());
        var topDirs = Directory.GetDirectories(musicDir);
        _log.LogError("BcMoosic BrowseCollection: {N} top-level dirs, first few: [{Names}]",
            topDirs.Length,
            string.Join(", ", topDirs.Take(3).Select(Path.GetFileName)));

        // Diagnostic: inspect the first artist dir
        var firstDir = topDirs.OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (firstDir != null)
        {
            var firstFiles = Directory.GetFiles(firstDir);
            var firstSubdirs = Directory.GetDirectories(firstDir);
            _log.LogError("BcMoosic BrowseCollection: first artist='{Name}' files={FC} subdirs={DC} exts=[{Exts}]",
                Path.GetFileName(firstDir), firstFiles.Length, firstSubdirs.Length,
                string.Join(", ", firstFiles.Take(5).Select(Path.GetExtension)));
            if (firstSubdirs.Length > 0)
            {
                var firstAlbumFiles = Directory.GetFiles(firstSubdirs[0]);
                _log.LogError("BcMoosic BrowseCollection: first album='{Alb}' files={FC} exts=[{Exts}]",
                    Path.GetFileName(firstSubdirs[0]), firstAlbumFiles.Length,
                    string.Join(", ", firstAlbumFiles.Take(5).Select(Path.GetExtension)));
            }
        }

        var artists = new List<ArtistDto>();
        foreach (var artistDir in Directory.GetDirectories(musicDir).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            var albums = new List<AlbumDto>();

            // Tracks directly in the artist dir (flat structure, no album subdir)
            var directTracks = Directory.GetFiles(artistDir)
                .Count(f => AudioExtensions.Contains(Path.GetExtension(f)));
            if (directTracks > 0)
                albums.Add(new AlbumDto(string.Empty, directTracks));

            // Tracks inside album subdirectories (Artist/Album/tracks structure)
            foreach (var albumDir in Directory.GetDirectories(artistDir).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var tracks = Directory.GetFiles(albumDir)
                    .Count(f => AudioExtensions.Contains(Path.GetExtension(f)));
                if (tracks > 0)
                    albums.Add(new AlbumDto(Path.GetFileName(albumDir), tracks));
            }

            if (albums.Count > 0)
                artists.Add(new ArtistDto(Path.GetFileName(artistDir), albums));
        }

        _log.LogError("BcMoosic BrowseCollection: returning {N} artists", artists.Count);
        return new LocalCollectionResponse(artists);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private string GetMusicDir(Configuration.PluginConfiguration? cfg)
    {
        if (cfg is not null && !string.IsNullOrEmpty(cfg.MusicDirectory))
            return cfg.MusicDirectory;

        _log.LogError("BcMoosic GetMusicDir: cfg.MusicDirectory={Dir}", cfg?.MusicDirectory ?? "(null)");

        // Auto-detect from Jellyfin's music libraries
        var folders = _libraryManager.GetVirtualFolders();
        _log.LogError("BcMoosic GetMusicDir: {Count} virtual folders: [{Names}]",
            folders.Count,
            string.Join(", ", folders.Select(f => $"{f.Name}({f.CollectionType})")));
        foreach (var folder in folders)
        {
            if (folder.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.music)
                if (folder.Locations is { Length: > 0 })
                    return folder.Locations[0];
        }

        return "/music";
    }

    private static void SaveToCfg(Action<Configuration.PluginConfiguration> mutate)
    {
        if (Plugin.Instance is null) return;
        var cfg = Plugin.Instance.Configuration;
        mutate(cfg);
        Plugin.Instance.SaveConfiguration();
    }
}
