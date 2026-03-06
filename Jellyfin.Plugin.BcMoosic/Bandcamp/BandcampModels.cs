using System.Collections.Generic;

namespace Jellyfin.Plugin.BcMoosic.Bandcamp;

public record CollectionItem(
    long SaleItemId,
    string ItemType,
    string Artist,
    string Title,
    string Purchased,
    string? ArtUrl,
    string RedownloadUrl,
    string Token);

public record CollectionResult(
    IReadOnlyList<CollectionItem> Items,
    bool MoreAvailable,
    string? LastToken);

public record WishlistItem(
    string Artist,
    string Title,
    string ItemType,
    string? ArtUrl,
    string? ItemUrl);

public record WishlistResult(IReadOnlyList<WishlistItem> Items);

public record FollowingBand(string Name, string Url, string? ImageUrl);

public record FollowingResult(IReadOnlyList<FollowingBand> Bands);

public record DownloadUrls(
    string Artist,
    string Title,
    IReadOnlyDictionary<string, string> Formats);
