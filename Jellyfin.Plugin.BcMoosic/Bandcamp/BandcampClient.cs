using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BcMoosic.Bandcamp;

/// <summary>
/// HTTP client for the Bandcamp website — port of bandcamp.py.
/// Cookie-based auth; no passwords stored.
/// </summary>
public sealed class BandcampClient : IDisposable
{
    private const string Base = "https://bandcamp.com";

    // Domains that bcMoosic is allowed to make requests to.
    // Redownload URLs must originate from bandcamp.com; download file URLs are validated
    // separately (HTTPS-only) because Bandcamp's CDN uses various subdomains.
    private static readonly string[] AllowedRedownloadHosts = ["bandcamp.com"];

    // Injects the raw Cookie header on every request, bypassing CookieContainer
    // which mangles URL-encoded values (the Bandcamp identity cookie contains % and " chars).
    private sealed class CookieInjector : DelegatingHandler
    {
        private volatile string _header = string.Empty;
        public void Set(IReadOnlyDictionary<string, string> cookies)
            => _header = string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        public void Clear() => _header = string.Empty;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var h = _header;
            if (!string.IsNullOrEmpty(h))
                request.Headers.TryAddWithoutValidation("Cookie", h);
            return base.SendAsync(request, ct);
        }
    }

    private readonly HttpClient _http;
    private readonly CookieInjector _cookieInjector;
    private readonly ILogger<BandcampClient> _log;
    private readonly object _lock = new();

    private Dictionary<string, string> _cookies = new();
    private long? _cachedFanId;
    private bool _hasCookies;

    public string Username { get; private set; } = string.Empty;

    public BandcampClient(ILogger<BandcampClient> log)
    {
        _log = log;
        _cookieInjector = new CookieInjector
        {
            InnerHandler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            }
        };
        _http = new HttpClient(_cookieInjector, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    // ---------------------------------------------------------------
    // Configuration
    // ---------------------------------------------------------------

    /// <summary>Load persisted credentials from plugin configuration (called on first use).</summary>
    public void LoadFromConfig(Configuration.PluginConfiguration cfg)
    {
        if (_hasCookies) { _log.LogDebug("BcMoosic LoadFromConfig: already has cookies, skipping"); return; }
        if (string.IsNullOrEmpty(cfg.BandcampCookies)) { _log.LogInformation("BcMoosic LoadFromConfig: no cookies in config"); return; }
        try
        {
            var cookies = JsonSerializer.Deserialize<Dictionary<string, string>>(cfg.BandcampCookies);
            if (cookies is { Count: > 0 })
            {
                Configure(cfg.BandcampUsername ?? string.Empty, cookies);
                _log.LogInformation("BandcampClient: loaded cookies from config (username={U})", Username);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "BandcampClient: failed to deserialize saved cookies");
        }
    }

    /// <summary>Apply a new username + cookie set (called from settings API).</summary>
    public void Configure(string username, IReadOnlyDictionary<string, string> cookies)
    {
        lock (_lock)
        {
            Username = username;
            _cachedFanId = null;
            ReplaceCookies(cookies);
        }
    }

    /// <summary>Return currently stored cookies (for persisting to plugin config).</summary>
    public IReadOnlyDictionary<string, string> GetCookies() => _cookies;

    private void ReplaceCookies(IReadOnlyDictionary<string, string> cookies)
    {
        _cookies = new Dictionary<string, string>(cookies);
        _hasCookies = cookies.Count > 0;
        if (_hasCookies)
            _cookieInjector.Set(_cookies);
        else
            _cookieInjector.Clear();
    }

    // ---------------------------------------------------------------
    // Auth
    // ---------------------------------------------------------------

    /// <summary>Return true if the current session is authenticated with Bandcamp.</summary>
    public async Task<bool> VerifyAsync(CancellationToken ct = default)
    {
        if (!_hasCookies)
        {
            _log.LogError("BcMoosic VerifyAsync: no cookies configured");
            return false;
        }
        try
        {
            var resp = await _http.GetAsync($"{Base}/settings/", ct).ConfigureAwait(false);
            // Check the path component only — avoids false positives from "/login" in a domain name
            var finalPath = resp.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
            var ok = !finalPath.Contains("/login", StringComparison.OrdinalIgnoreCase);
            _log.LogDebug("BcMoosic VerifyAsync: status={Status} finalPath={Path} authenticated={Ok}",
                (int)resp.StatusCode, finalPath, ok);
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "VerifyAsync: request failed");
            return false;
        }
    }

    /// <summary>Detect the logged-in Bandcamp username from the homepage blob.</summary>
    public async Task<string> DetectUsernameAsync(CancellationToken ct = default)
    {
        try
        {
            var html = await _http.GetStringAsync(Base, ct).ConfigureAwait(false);
            var blob = TryParseBlob(html);
            if (blob.HasValue)
            {
                var b = blob.Value;
                if (b.TryGetProperty("fan_data", out var fd) && fd.TryGetProperty("username", out var u))
                    return u.GetString() ?? string.Empty;
                if (b.TryGetProperty("current_fan", out var cf) && cf.TryGetProperty("username", out var u2))
                    return u2.GetString() ?? string.Empty;
            }
            var m = Regex.Match(html, @"""username""\s*:\s*""([a-z0-9_\-]+)""", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DetectUsername failed");
        }
        return string.Empty;
    }

    // ---------------------------------------------------------------
    // Fan ID
    // ---------------------------------------------------------------

    private async Task<long> GetFanIdAsync(CancellationToken ct)
    {
        if (_cachedFanId.HasValue) return _cachedFanId.Value;

        // Fast path: embedded in the identity cookie payload
        var fromCookie = FanIdFromCookie();
        if (fromCookie.HasValue)
        {
            _cachedFanId = fromCookie.Value;
            return _cachedFanId.Value;
        }

        if (string.IsNullOrEmpty(Username))
            throw new BandcampException("Username not configured — cannot determine fan ID.");

        var html = await _http.GetStringAsync($"{Base}/{Username}", ct).ConfigureAwait(false);

        var m = Regex.Match(html, @"""fan_id""\s*:\s*(\d+)");
        if (m.Success)
        {
            _cachedFanId = long.Parse(m.Groups[1].Value);
            return _cachedFanId.Value;
        }

        var blob = TryParseBlob(html);
        if (blob.HasValue && blob.Value.TryGetProperty("fan_data", out var fd) && fd.TryGetProperty("fan_id", out var fid))
        {
            _cachedFanId = fid.GetInt64();
            return _cachedFanId.Value;
        }

        throw new BandcampException("Could not determine fan ID. Check username and cookie.");
    }

    private long? FanIdFromCookie()
    {
        if (!_cookies.TryGetValue("identity", out var value) || string.IsNullOrEmpty(value)) return null;
        var decoded = Uri.UnescapeDataString(value);
        var parts = decoded.Split('\t');
        if (parts.Length >= 3)
        {
            try
            {
                using var doc = JsonDocument.Parse(parts[2]);
                if (doc.RootElement.TryGetProperty("id", out var id))
                    return id.GetInt64();
            }
            catch { }
        }
        var m = Regex.Match(decoded, @"""id""\s*:\s*(\d+)");
        if (m.Success) return long.Parse(m.Groups[1].Value);
        return null;
    }

    // ---------------------------------------------------------------
    // Collection
    // ---------------------------------------------------------------

    public async Task<CollectionResult> GetCollectionAsync(CancellationToken ct = default)
    {
        RequireUsername();
        var html = await _http.GetStringAsync($"{Base}/{Username}", ct).ConfigureAwait(false);
        var blob = ParseBlob(html);

        _log.LogDebug("BcMoosic GetCollection: blob keys = [{Keys}]",
            string.Join(", ", blob.EnumerateObject().Select(p => p.Name)));

        if (!blob.TryGetProperty("collection_data", out var cd))
            throw new BandcampException("collection_data not found in page blob.");
        if (!blob.TryGetProperty("item_cache", out var icRoot) || !icRoot.TryGetProperty("collection", out var ic))
            throw new BandcampException("item_cache.collection not found in page blob.");

        var rdu = cd.TryGetProperty("redownload_urls", out var r) ? r : default;
        var sequence = cd.TryGetProperty("sequence", out var seq) ? seq : default;

        _log.LogDebug("BcMoosic GetCollection: sequence kind={SeqKind} rdu kind={RduKind} rdu keys={RduKeys}",
            sequence.ValueKind,
            rdu.ValueKind,
            rdu.ValueKind == JsonValueKind.Object
                ? string.Join(", ", rdu.EnumerateObject().Take(5).Select(p => p.Name))
                : "n/a");

        var items = new List<CollectionItem>();
        int seqCount = 0, noPurchased = 0, noSaleId = 0, noRdUrl = 0;
        if (sequence.ValueKind == JsonValueKind.Array)
        {
            foreach (var keyEl in sequence.EnumerateArray())
            {
                seqCount++;
                var key = keyEl.GetString();
                if (key is null || !ic.TryGetProperty(key, out var raw)) continue;

                // Skip items without a "purchased" field (wishlisted items, etc.)
                if (!raw.TryGetProperty("purchased", out var pur) || pur.ValueKind == JsonValueKind.Null)
                    { noPurchased++; continue; }

                var saleItemId = raw.TryGetProperty("sale_item_id", out var sii) && sii.ValueKind == JsonValueKind.Number
                    ? sii.GetInt64() : 0L;
                if (saleItemId == 0) { noSaleId++; continue; }

                // Redownload URL is optional — fetched on demand at download time if missing
                string redownloadUrl = string.Empty;
                if (rdu.ValueKind == JsonValueKind.Object && rdu.TryGetProperty($"p{saleItemId}", out var rdUrl))
                    redownloadUrl = rdUrl.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(redownloadUrl)) noRdUrl++;

                var artId = raw.TryGetProperty("item_art_id", out var aid) && aid.ValueKind == JsonValueKind.Number ? aid.GetInt64().ToString() : null;
                var ttype = raw.TryGetProperty("tralbum_type", out var tt) ? tt.GetString() : "a";

                items.Add(new CollectionItem(
                    SaleItemId: saleItemId,
                    ItemType: ttype == "t" ? "track" : "album",
                    Artist: Str(raw, "band_name"),
                    Title: Str(raw, "item_title"),
                    Purchased: Str(raw, "purchased"),
                    ArtUrl: artId is not null ? $"https://f4.bcbits.com/img/a{artId}_16.jpg" : null,
                    RedownloadUrl: redownloadUrl,
                    Token: key));
            }
        }

        _log.LogDebug("BcMoosic GetCollection: seq={Seq} noPurchased={NP} noSaleId={NS} noRdUrl={NR} kept={Kept}",
            seqCount, noPurchased, noSaleId, noRdUrl, items.Count);

        var moreAvailable = cd.TryGetProperty("more_available", out var ma) && ma.GetBoolean();
        var lastToken = items.Count > 0 ? items[^1].Token : null;
        return new CollectionResult(items, moreAvailable, lastToken);
    }

    // ---------------------------------------------------------------
    // Download URL resolution
    // ---------------------------------------------------------------

    public async Task<DownloadUrls> GetDownloadUrlsAsync(string redownloadUrl, CancellationToken ct = default)
    {
        ValidateRedownloadUrl(redownloadUrl);
        var html = await _http.GetStringAsync(redownloadUrl, ct).ConfigureAwait(false);

        // Try #pagedata blob first
        var maybeBlob = TryParseBlob(html);
        if (maybeBlob.HasValue)
        {
            try { return ExtractDownloadUrls(maybeBlob.Value); } catch { }
        }

        // Fallback: search for digital_items in inline script JSON
        var m = Regex.Match(html, @"(\{[""'\s]*""digital_items"".*)", RegexOptions.Singleline);
        if (m.Success)
        {
            var candidate = m.Groups[1].Value;
            int depth = 0, end = 0;
            for (int i = 0; i < candidate.Length; i++)
            {
                if (candidate[i] == '{') depth++;
                else if (candidate[i] == '}') { depth--; if (depth == 0) { end = i + 1; break; } }
            }
            if (end > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(candidate[..end]);
                    return ExtractDownloadUrls(doc.RootElement.Clone());
                }
                catch { }
            }
        }

        throw new BandcampException("Could not parse Bandcamp download page. The page format may have changed.");
    }

    private static DownloadUrls ExtractDownloadUrls(JsonElement blob)
    {
        if (!blob.TryGetProperty("digital_items", out var di))
            throw new BandcampException("No digital_items in download blob.");
        var first = di.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
            throw new BandcampException("No downloadable items found.");

        var formats = new Dictionary<string, string>();
        if (first.TryGetProperty("downloads", out var downloads))
            foreach (var entry in downloads.EnumerateObject())
                if (entry.Value.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url)
                    formats[entry.Name] = url;

        return new DownloadUrls(Str(first, "artist"), Str(first, "title"), formats);
    }

    // ---------------------------------------------------------------
    // Wishlist
    // ---------------------------------------------------------------

    public async Task<WishlistResult> GetWishlistAsync(CancellationToken ct = default)
    {
        RequireUsername();
        var html = await _http.GetStringAsync($"{Base}/{Username}/wishlist", ct).ConfigureAwait(false);
        var blob = ParseBlob(html);

        var wd = blob.TryGetProperty("wishlist_data", out var w) ? w : default;
        var ic = blob.TryGetProperty("item_cache", out var icRoot) && icRoot.TryGetProperty("wishlist", out var wic) ? wic : default;
        var sequence = wd.ValueKind != JsonValueKind.Undefined && wd.TryGetProperty("sequence", out var seq) ? seq : default;

        _log.LogDebug("BcMoosic GetWishlist: wd={Wd} ic={Ic} seq={Seq} ic_keys=[{IcKeys}]",
            wd.ValueKind, ic.ValueKind, sequence.ValueKind,
            ic.ValueKind == JsonValueKind.Object
                ? string.Join(", ", ic.EnumerateObject().Take(5).Select(p => p.Name))
                : "n/a");

        var items = new List<WishlistItem>();
        if (sequence.ValueKind == JsonValueKind.Array)
        {
            var seqItems = sequence.EnumerateArray().ToList();
            _log.LogDebug("BcMoosic GetWishlist: seq len={Len} first kind={Kind} first val={Val}",
                seqItems.Count,
                seqItems.Count > 0 ? seqItems[0].ValueKind.ToString() : "n/a",
                seqItems.Count > 0 ? seqItems[0].ToString() : "n/a");

            foreach (var keyEl in seqItems)
            {
                // Sequence items may be strings or numbers depending on Bandcamp's format
                var key = keyEl.ValueKind == JsonValueKind.Number
                    ? keyEl.GetInt64().ToString()
                    : keyEl.GetString();
                if (key is null || ic.ValueKind == JsonValueKind.Undefined || !ic.TryGetProperty(key, out var raw)) continue;
                var artId = raw.TryGetProperty("item_art_id", out var aid) && aid.ValueKind == JsonValueKind.Number ? aid.GetInt64().ToString() : null;
                var ttype = raw.TryGetProperty("tralbum_type", out var tt) ? tt.GetString() : "a";
                items.Add(new WishlistItem(
                    Str(raw, "band_name"),
                    Str(raw, "item_title"),
                    ttype == "t" ? "track" : "album",
                    artId is not null ? $"https://f4.bcbits.com/img/a{artId}_16.jpg" : null,
                    raw.TryGetProperty("item_url", out var iu) ? iu.GetString() : null));
            }
        }
        _log.LogDebug("BcMoosic GetWishlist: returning {Count} items", items.Count);
        return new WishlistResult(items);
    }

    // ---------------------------------------------------------------
    // Following
    // ---------------------------------------------------------------

    public async Task<FollowingResult> GetFollowingAsync(CancellationToken ct = default)
    {
        RequireUsername();
        var html = await _http.GetStringAsync($"{Base}/{Username}/following/artists_and_labels", ct).ConfigureAwait(false);
        var blob = ParseBlob(html);

        var fd = blob.TryGetProperty("following_bands_data", out var f) ? f : default;
        var ic = blob.TryGetProperty("item_cache", out var icRoot) && icRoot.TryGetProperty("following_bands", out var fb) ? fb : default;
        var sequence = fd.ValueKind != JsonValueKind.Undefined && fd.TryGetProperty("sequence", out var seq) ? seq : default;
        var fanId = blob.TryGetProperty("fan_data", out var fanData) && fanData.TryGetProperty("fan_id", out var fid) ? (long?)fid.GetInt64() : null;
        var lastToken = fd.ValueKind != JsonValueKind.Undefined && fd.TryGetProperty("last_token", out var lt) ? lt.GetString() : null;
        var total = fd.ValueKind != JsonValueKind.Undefined && fd.TryGetProperty("item_count", out var cnt) ? cnt.GetInt32() : 0;

        var bands = new List<FollowingBand>();
        if (sequence.ValueKind == JsonValueKind.Array)
        {
            foreach (var idEl in sequence.EnumerateArray())
            {
                var bandKey = idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt64().ToString()
                    : idEl.GetString();
                if (bandKey is null || ic.ValueKind == JsonValueKind.Undefined || !ic.TryGetProperty(bandKey, out var raw)) continue;
                bands.Add(ParseFollowingBand(raw));
            }
        }

        // Paginate until we have all bands (cap at 50 pages = ~1 000 bands)
        const int maxPages = 50;
        int pages = 0;
        while (bands.Count < total && !string.IsNullOrEmpty(lastToken) && fanId.HasValue)
        {
            if (++pages > maxPages)
            {
                _log.LogWarning("GetFollowing: pagination cap reached ({Max} pages); stopping early", maxPages);
                break;
            }
            var body = JsonSerializer.Serialize(new { fan_id = fanId.Value, older_than_token = lastToken, count = 20 });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{Base}/api/fancollection/1/following_bands", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var pageJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var pageDoc = JsonDocument.Parse(pageJson);
            var page = pageDoc.RootElement;

            if (page.TryGetProperty("followeers", out var followeers))
                foreach (var b in followeers.EnumerateArray())
                    bands.Add(ParseFollowingBand(b));

            lastToken = page.TryGetProperty("last_token", out var newLt) ? newLt.GetString() : null;
            if (!page.TryGetProperty("more_available", out var ma) || !ma.GetBoolean()) break;
        }

        bands.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _log.LogInformation("GetFollowing: {Count}/{Total} bands", bands.Count, total);
        return new FollowingResult(bands);
    }

    private static FollowingBand ParseFollowingBand(JsonElement b)
    {
        var name = Str(b, "name");
        var imageId = b.TryGetProperty("image_id", out var img) && img.ValueKind == JsonValueKind.Number ? img.GetInt64() : 0L;
        var imageUrl = imageId > 0 ? $"https://f4.bcbits.com/img/a{imageId}_16.jpg" : null;
        var hints = b.TryGetProperty("url_hints", out var h) ? h : default;
        var custom = hints.ValueKind != JsonValueKind.Undefined && hints.TryGetProperty("custom_domain", out var cd) ? cd.GetString() : null;
        var subdomain = hints.ValueKind != JsonValueKind.Undefined && hints.TryGetProperty("subdomain", out var sd) ? sd.GetString() : null;
        var url = !string.IsNullOrEmpty(custom) ? $"https://{custom}"
            : !string.IsNullOrEmpty(subdomain) ? $"https://{subdomain}.bandcamp.com"
            : string.Empty;
        return new FollowingBand(name, url, imageUrl);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Validates that a redownload URL is an HTTPS URL on bandcamp.com.
    /// Throws <see cref="BandcampException"/> if the URL is invalid or off-domain.
    /// </summary>
    private static void ValidateRedownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new BandcampException($"Redownload URL must use HTTPS: {url}");

        var host = uri.Host;
        var allowed = Array.Exists(AllowedRedownloadHosts, h =>
            host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

        if (!allowed)
            throw new BandcampException($"Redownload URL host is not an allowed Bandcamp domain: {host}");
    }

    private static JsonElement ParseBlob(string html)
    {
        var maybeBlob = TryParseBlob(html);
        return maybeBlob ?? throw new BandcampException("Could not find pagedata blob in page HTML.");
    }

    private static JsonElement? TryParseBlob(string html)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        var el = doc.GetElementById("pagedata");
        var blobText = el?.GetAttribute("data-blob");
        if (string.IsNullOrEmpty(blobText)) return null;
        using var jdoc = JsonDocument.Parse(blobText);
        return jdoc.RootElement.Clone();
    }

    private static string Str(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) ? v.GetString() ?? string.Empty : string.Empty;

    private void RequireUsername()
    {
        if (string.IsNullOrEmpty(Username))
            throw new BandcampException("Bandcamp username not configured. Sign in via the bcMoosic app settings.");
    }

    public void Dispose() => _http.Dispose();
}
