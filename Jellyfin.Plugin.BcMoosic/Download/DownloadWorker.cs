using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.BcMoosic.Bandcamp;
using Jellyfin.Plugin.BcMoosic.Organization;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BcMoosic.Download;

/// <summary>
/// Background service that drains the <see cref="DownloadManager"/> queue —
/// one download at a time (sequential to respect Bandcamp rate limits).
/// </summary>
public class DownloadWorker : BackgroundService
{
    private readonly DownloadManager _queue;
    private readonly BandcampClient _bc;
    private readonly TrackOrganizer _organizer;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<DownloadWorker> _log;

    private readonly HttpClient _http;

    public DownloadWorker(
        DownloadManager queue,
        BandcampClient bc,
        TrackOrganizer organizer,
        ILibraryMonitor libraryMonitor,
        ILogger<DownloadWorker> log)
    {
        _queue = queue;
        _bc = bc;
        _organizer = organizer;
        _libraryMonitor = libraryMonitor;
        _log = log;

        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DownloadWorker started");
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (job.Status == DownloadStatus.Cancelled) continue;
            await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessJobAsync(DownloadJob job, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var tempDir = cfg?.TempDirectory ?? "/tmp/bcmoosic";
        var musicDir = ResolveMusicDir(cfg);

        var jobTempDir = Path.Combine(tempDir, job.Id.ToString("N"));
        Directory.CreateDirectory(jobTempDir);

        try
        {
            // --- Step 1: resolve download URL ---
            job.Status = DownloadStatus.Downloading;
            _log.LogInformation("Resolving download URL for {Artist} — {Title} [{Format}]", job.Artist, job.Title, job.Format);
            var urls = await _bc.GetDownloadUrlsAsync(job.RedownloadUrl, ct).ConfigureAwait(false);

            if (!urls.Formats.TryGetValue(job.Format, out var downloadUrl))
                throw new InvalidOperationException($"Format '{job.Format}' not available. Available: {string.Join(", ", urls.Formats.Keys)}");

            // --- Step 2: download ZIP ---
            var zipPath = Path.Combine(jobTempDir, "download.zip");
            await DownloadFileAsync(downloadUrl, zipPath, job, ct).ConfigureAwait(false);

            // --- Step 3: extract ---
            job.Status = DownloadStatus.Extracting;
            job.Progress = 0;
            var extractDir = Path.Combine(jobTempDir, "extract");
            Directory.CreateDirectory(extractDir);
            _log.LogInformation("Extracting {Zip}", zipPath);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            File.Delete(zipPath);

            // --- Step 4: organise ---
            job.Status = DownloadStatus.Organizing;
            _log.LogInformation("Organising into {MusicDir}", musicDir);
            var destPath = await _organizer.OrganizeAsync(extractDir, musicDir, ct).ConfigureAwait(false);
            job.DestPath = destPath;

            // --- Step 5: notify Jellyfin ---
            if (!string.IsNullOrEmpty(destPath))
                _libraryMonitor.ReportFileSystemChanged(destPath);

            job.Status = DownloadStatus.Done;
            job.Progress = 100;
            _log.LogInformation("Download complete: {Dest}", destPath);
        }
        catch (OperationCanceledException)
        {
            job.Status = DownloadStatus.Cancelled;
        }
        catch (Exception ex)
        {
            job.Status = DownloadStatus.Error;
            job.Error = ex.Message;
            _log.LogError(ex, "Download failed for job {Id}", job.Id);
        }
        finally
        {
            try { Directory.Delete(jobTempDir, recursive: true); } catch { }
        }
    }

    private async Task DownloadFileAsync(string url, string destPath, DownloadJob job, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0L;
        var buffer = new byte[81920];
        long received = 0;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if (total > 0)
                job.Progress = (int)(received * 100 / total);
        }
    }

    private static string ResolveMusicDir(Configuration.PluginConfiguration? cfg)
    {
        if (cfg is not null && !string.IsNullOrEmpty(cfg.MusicDirectory))
            return cfg.MusicDirectory;
        return "/music";
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
