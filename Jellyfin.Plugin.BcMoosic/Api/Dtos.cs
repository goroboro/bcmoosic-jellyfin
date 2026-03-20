using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.BcMoosic.Download;

namespace Jellyfin.Plugin.BcMoosic.Api;

// ---- Auth ----

public record AuthStatusResponse(
    [property: JsonPropertyName("authenticated")] bool Authenticated,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("defaultFormat")] string DefaultFormat,
    [property: JsonPropertyName("musicDir")] string MusicDir,
    [property: JsonPropertyName("tempDir")] string TempDir);

public record CookieRequest(
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("username")] string Username = "");

// ---- Settings ----

public record SettingsRequest(
    [property: JsonPropertyName("defaultFormat")] string? DefaultFormat = null,
    [property: JsonPropertyName("musicDir")] string? MusicDir = null,
    [property: JsonPropertyName("tempDir")] string? TempDir = null);

// ---- Collection ----

public record CollectionItemDto(
    [property: JsonPropertyName("saleItemId")] long SaleItemId,
    [property: JsonPropertyName("itemType")] string ItemType,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("purchased")] string Purchased,
    [property: JsonPropertyName("artUrl")] string? ArtUrl,
    [property: JsonPropertyName("redownloadUrl")] string RedownloadUrl,
    [property: JsonPropertyName("token")] string Token);

public record CollectionResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<CollectionItemDto> Items,
    [property: JsonPropertyName("moreAvailable")] bool MoreAvailable,
    [property: JsonPropertyName("lastToken")] string? LastToken);

// ---- Wishlist ----

public record WishlistItemDto(
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("itemType")] string ItemType,
    [property: JsonPropertyName("artUrl")] string? ArtUrl,
    [property: JsonPropertyName("itemUrl")] string? ItemUrl);

public record WishlistResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<WishlistItemDto> Items);

// ---- Following ----

public record FollowingBandDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl);

public record FollowingResponse(
    [property: JsonPropertyName("bands")] IReadOnlyList<FollowingBandDto> Bands);

// ---- Downloads ----

public record DownloadRequest(
    [property: JsonPropertyName("saleItemId")] long SaleItemId,
    [property: JsonPropertyName("itemType")] string ItemType,
    [property: JsonPropertyName("redownloadUrl")] string RedownloadUrl,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("format")] string? Format = null);

public record DownloadJobResponse(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("saleItemId")] long SaleItemId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("progress")] int Progress,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("destPath")] string? DestPath);

// ---- Local collection ----

public record AlbumDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tracks")] int Tracks);

public record ArtistDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("albums")] IReadOnlyList<AlbumDto> Albums);

public record LocalCollectionResponse(
    [property: JsonPropertyName("artists")] IReadOnlyList<ArtistDto> Artists);

// ---- Helpers ----

internal static class DtoMapper
{
    public static DownloadJobResponse ToDto(DownloadJob job) => new(
        JobId: job.Id.ToString(),
        SaleItemId: job.SaleItemId,
        Status: job.Status.ToString().ToLowerInvariant(),
        Progress: job.Progress,
        Artist: job.Artist,
        Title: job.Title,
        Format: job.Format,
        Error: job.Error,
        DestPath: job.DestPath);
}
