using System;

namespace Jellyfin.Plugin.BcMoosic.Download;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Extracting,
    Organizing,
    Done,
    Error,
    Cancelled,
}

public class DownloadJob
{
    public Guid Id { get; } = Guid.NewGuid();
    public long SaleItemId { get; init; }
    public string ItemType { get; init; } = "album";
    public string Artist { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string RedownloadUrl { get; init; } = string.Empty;

    public volatile DownloadStatus Status = DownloadStatus.Queued;
    public int Progress;      // 0–100, updated during download phase
    public string? Error;
    public string? DestPath;

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
}
