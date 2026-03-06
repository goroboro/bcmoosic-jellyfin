# bcMoosic for Jellyfin

A Jellyfin plugin that connects your Bandcamp account to your self-hosted music library.
Browse your purchases, wishlist, and followed artists — then download albums directly into Jellyfin in any format Bandcamp offers.

## Features

- **Owned** — browse all purchased albums and tracks with cover art; queue downloads in one tap
- **Wishlist** — browse wishlisted items with direct links to buy on Bandcamp
- **Following** — see all artists and labels you follow, with links to their Bandcamp pages
- **Downloads** — real-time download queue with progress bars; supports mp3-320, FLAC, AAC, Vorbis, ALAC, WAV, AIFF
- **Collection browser** — A–Z browsable view of your local music library, with Bandcamp follow indicators
- **Settings** — configure music directory, default format, and temp directory from inside the app
- **Mobile-first PWA** — add to home screen for a native-app feel; works great as a phone remote
- **Jellyfin integration** — links back to the Jellyfin dashboard; accessible from the plugin config page

## Requirements

- **Jellyfin** 10.11.6 or later
- **.NET 9 SDK** (for building)
- A Bandcamp account
- Network access between Jellyfin and the internet (to reach bandcamp.com)

## Installation

### Build from source

```bash
git clone https://github.com/goroboro/bcmoosic-jellyfin.git
cd bcmoosic-jellyfin
dotnet build Jellyfin.Plugin.BcMoosic/Jellyfin.Plugin.BcMoosic.csproj -c Release
```

The compiled DLLs will be at:

```
Jellyfin.Plugin.BcMoosic/bin/Release/net9.0/
```

### Deploy to Jellyfin

1. Stop Jellyfin
2. Create a plugin directory (if it doesn't exist):
   ```bash
   mkdir -p /var/lib/jellyfin/plugins/BcMoosic_1.0.0/
   ```
3. Copy all DLLs from the build output:
   ```bash
   cp Jellyfin.Plugin.BcMoosic/bin/Release/net9.0/*.dll \
      /var/lib/jellyfin/plugins/BcMoosic_1.0.0/
   ```
4. Create a `meta.json` in that directory:
   ```json
   {
     "guid": "d8f3c2a4-e1b7-4f5d-8c9e-2a3b4c5d6e7f",
     "name": "bcMoosic",
     "version": "1.0.0.0"
   }
   ```
5. Set correct ownership:
   ```bash
   chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/BcMoosic_1.0.0/
   ```
6. Start Jellyfin

### Access the app

Navigate to:

```
http://<your-jellyfin-host>:<port>/BcMoosic/
```

Or from the Jellyfin dashboard: **Admin → Plugins → bcMoosic → ⋮ → Settings → Open bcMoosic**.

For quick mobile access, open the app in your phone browser and use **Add to Home Screen** — bcMoosic ships with a PWA manifest for a native-app experience.

## First-time Setup

bcMoosic uses Bandcamp's session cookie for authentication — no password is ever stored.

### Getting your identity cookie

1. On your phone or desktop, open **Firefox**
2. Install the **Cookie Editor** extension/add-on
3. Navigate to [bandcamp.com](https://bandcamp.com) and sign in
4. Open Cookie Editor → find the **`identity`** cookie → tap it → copy the **Value** field
5. In bcMoosic → **Settings** tab → paste the value into the *Identity cookie value* field → tap **Save & verify**

The app will detect your Bandcamp username automatically and load your purchases.

> **Why cookies?** Bandcamp doesn't offer a public API. Cookie-based auth is the same method used by the official Bandcamp mobile app and browser session.

## Configuration

All configuration lives inside the bcMoosic app (Settings tab):

| Setting | Default | Description |
|---|---|---|
| Music directory | Auto-detected from Jellyfin library | Where downloaded music is placed |
| Default format | mp3-320 | Format used when you tap "Get" (overridable per download) |
| Temp directory | `/tmp/bcmoosic` | Staging area for in-progress downloads |

Music is organised as `<music dir>/<Artist>/<Album>/<tracks>` automatically using audio tags read from the downloaded files.

## Project Structure

```
Jellyfin.Plugin.BcMoosic/
├── Api/
│   ├── BcMoosicController.cs   # All HTTP endpoints + static file serving
│   └── Dtos.cs                  # Request/response records
├── Bandcamp/
│   ├── BandcampClient.cs        # HTTP client: auth, collection, wishlist, following, downloads
│   ├── BandcampException.cs     # Domain exception
│   └── BandcampModels.cs        # Internal result types
├── Configuration/
│   ├── configPage.html          # Jellyfin dashboard config page
│   └── PluginConfiguration.cs   # Persisted settings (XML via Jellyfin)
├── Download/
│   ├── DownloadJob.cs           # Job state machine
│   ├── DownloadManager.cs       # In-memory job queue
│   └── DownloadWorker.cs        # Background hosted service
├── Organization/
│   └── TrackOrganizer.cs        # Tag reading + file placement
├── Web/
│   ├── index.html               # Mobile-first SPA shell
│   ├── app.js                   # Vanilla JS — no framework
│   ├── style.css                # Dark theme, mobile-first
│   └── manifest.json            # PWA manifest
├── Plugin.cs                    # BasePlugin entry point
└── ServiceRegistrator.cs        # DI registrations
```

## Notes

- The plugin serves its web app at `/BcMoosic/` — entirely separate from Jellyfin's own web UI
- Jellyfin's JSON serialiser uses PascalCase by default; all response DTOs have explicit `[JsonPropertyName]` attributes for camelCase compatibility with the frontend
- Cookie values are stored in Jellyfin's plugin XML configuration (not in code or environment variables)
- The `CookieInjector` delegating handler is used instead of `CookieContainer` because Bandcamp's identity cookie contains URL-encoded characters that `CookieContainer` mangles

## License

MIT — see [LICENSE](LICENSE).
