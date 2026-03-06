using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.BcMoosic.Download;

/// <summary>
/// Holds the download queue (Channel) and the job registry.
/// The actual download work is done by <see cref="DownloadWorker"/>.
/// </summary>
public class DownloadManager
{
    private readonly Channel<DownloadJob> _channel = Channel.CreateBounded<DownloadJob>(
        new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.Wait });

    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();

    public ChannelReader<DownloadJob> Reader => _channel.Reader;

    /// <summary>Enqueue a new download job. Returns the job ID.</summary>
    public async Task<Guid> EnqueueAsync(
        long saleItemId,
        string itemType,
        string redownloadUrl,
        string artist,
        string title,
        string format,
        CancellationToken ct = default)
    {
        var job = new DownloadJob
        {
            SaleItemId = saleItemId,
            ItemType = itemType,
            RedownloadUrl = redownloadUrl,
            Artist = artist,
            Title = title,
            Format = format,
        };
        _jobs[job.Id] = job;
        await _channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
        return job.Id;
    }

    public DownloadJob? GetJob(Guid id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IReadOnlyList<DownloadJob> ListJobs()
        => _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    public bool CancelJob(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        if (job.Status == DownloadStatus.Queued)
        {
            job.Status = DownloadStatus.Cancelled;
            return true;
        }
        return false;
    }
}
