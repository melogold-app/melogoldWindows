using Melogold.Core.Domain;
using Melogold.Core.Music;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Melogold.InnerTube;

/// <summary>
/// Разбор ответов обычного YouTube (клиент WEB, REWRITE §4.8.1–§4.8.2 Android): <c>videoRenderer</c>,
/// <c>channelRenderer</c>, <c>lockupViewModel</c> (видео и плейлисты в новой разметке), страница канала.
/// Shorts, полки Shorts и промо отбрасываются.
/// </summary>
internal static partial class WebParsers
{
    [GeneratedRegex(@"^\d{1,2}(:\d{2}){1,2}$")]
    private static partial Regex DurationRegex();

    public static ItemsPage SearchPage(JsonNode? sections)
    {
        var items = new List<MusicItem>();
        string? continuation = null;
        foreach (var section in sections.Items())
        {
            if (section.At("itemSectionRenderer") is { } itemSection)
            {
                foreach (var item in itemSection.Items("contents"))
                {
                    if (Item(item) is { } parsed) items.Add(parsed);
                }
            }
            else if (section.At("continuationItemRenderer") is { } more)
            {
                continuation = more.Str("continuationEndpoint", "continuationCommand", "token");
            }
            else if (Item(section) is { } parsed) items.Add(parsed);
        }
        return new ItemsPage(items, continuation);
    }

    public static ItemsPage GridPage(JsonNode? contents)
    {
        var items = new List<MusicItem>();
        string? continuation = null;
        foreach (var item in contents.Items())
        {
            if (item.At("continuationItemRenderer") is { } more) continuation = more.Str("continuationEndpoint", "continuationCommand", "token");
            else if (Item(item.At("richItemRenderer", "content") ?? item) is { } parsed) items.Add(parsed);
        }
        return new ItemsPage(items, continuation);
    }

    private static MusicItem? Item(JsonNode? item)
    {
        if (item is null) return null;
        if (item.At("videoRenderer") is { } video) return Video(video);
        if (item.At("channelRenderer") is { } channel) return Channel(channel);
        if (item.At("lockupViewModel") is { } lockup) return Lockup(lockup);
        if (item.At("playlistRenderer") is { } playlist)
        {
            var id = playlist.Str("playlistId");
            var title = playlist.At("title").Text();
            if (id is null || string.IsNullOrEmpty(title)) return null;
            return new PlaylistItem
            {
                PlaylistId = id,
                Title = title,
                Subtitle = playlist.At("longBylineText").Text(),
                ThumbnailUrl = playlist.At("thumbnails", 0, "thumbnails").BestThumbnail(),
            };
        }
        return null;
    }

    public static Track? Video(JsonNode r)
    {
        var videoId = r.Str("videoId");
        var title = r.At("title").Text();
        if (videoId is null || string.IsNullOrEmpty(title)) return null;
        var isShort = r.At("navigationEndpoint", "reelWatchEndpoint") is not null
                      || r.Items("thumbnailOverlays").Any(o => o.Str("thumbnailOverlayTimeStatusRenderer", "style") == "SHORTS");
        if (isShort) return null;
        var isLive = r.Items("badges").Any(b => b.Str("metadataBadgeRenderer", "style") == "BADGE_STYLE_TYPE_LIVE_NOW")
                     || r.Items("thumbnailOverlays").Any(o => o.Str("thumbnailOverlayTimeStatusRenderer", "style") == "LIVE");
        var owner = r.At("ownerText").Runs().FirstOrDefault();
        if (owner.Node is null) owner = r.At("longBylineText").Runs().FirstOrDefault();
        var channelName = owner.Node is null ? null : owner.Text.Trim();
        var duration = isLive ? null : r.At("lengthText").Text();
        return new Track
        {
            VideoId = videoId,
            Title = title,
            Artists = channelName is null ? [] : [new ArtistRef(owner.BrowseId, channelName)],
            ArtistsText = channelName,
            DurationText = duration,
            DurationMs = Durations.ParseText(duration),
            ThumbnailUrl = r.At("thumbnail", "thumbnails").BestThumbnail(),
            VideoType = isLive ? "live" : "ugc",
            ViewsText = r.At("shortViewCountText").Text() ?? r.At("viewCountText").Text(),
        };
    }

    private static ArtistItem? Channel(JsonNode r)
    {
        var id = r.Str("channelId");
        var title = r.At("title").Text();
        if (id is null || string.IsNullOrEmpty(title)) return null;
        var thumbnail = r.At("thumbnail", "thumbnails").BestThumbnail();
        if (thumbnail is not null && thumbnail.StartsWith("//", StringComparison.Ordinal)) thumbnail = "https:" + thumbnail;
        return new ArtistItem
        {
            BrowseId = id,
            Name = title,
            Subtitle = r.At("videoCountText").Text() ?? r.At("subscriberCountText").Text(),
            ThumbnailUrl = thumbnail,
            IsChannel = true,
        };
    }

    /// <summary><c>lockupViewModel</c>: видео или плейлист новой разметки YouTube.</summary>
    private static MusicItem? Lockup(JsonNode r)
    {
        var id = r.Str("contentId");
        var type = r.Str("contentType");
        var meta = r.At("metadata", "lockupMetadataViewModel");
        var title = meta.Str("title", "content");
        if (id is null || string.IsNullOrEmpty(title)) return null;
        var rows = meta.At("metadata", "contentMetadataViewModel", "metadataRows").Items()
            .Select(row => row.Items("metadataParts").Select(p => p.Str("text", "content")).Where(t => !string.IsNullOrEmpty(t)).ToList())
            .Where(parts => parts.Count > 0)
            .ToList();
        var image = r.At("contentImage", "thumbnailViewModel", "image", "sources")
                    ?? r.At("contentImage", "collectionThumbnailViewModel", "primaryThumbnail", "thumbnailViewModel", "image", "sources");
        var thumbnail = image.BestThumbnail();
        var badge = r.FindAll("thumbnailBadgeViewModel").Select(b => b.Str("text")).FirstOrDefault(t => t is not null && DurationRegex().IsMatch(t));

        switch (type)
        {
            case "LOCKUP_CONTENT_TYPE_VIDEO":
                // В строках метаданных видео на канале — «просмотры · дата», в поиске — «канал», затем «просмотры · дата»
                var channelName = rows.Count > 1 ? rows[0].FirstOrDefault() : null;
                var channelId = meta.FindAll("browseEndpoint").Select(b => b.Str("browseId")).FirstOrDefault(b => b?.StartsWith("UC", StringComparison.Ordinal) == true);
                return new Track
                {
                    VideoId = id,
                    Title = title,
                    Artists = channelName is null ? [] : [new ArtistRef(channelId, channelName)],
                    ArtistsText = channelName,
                    DurationText = badge,
                    DurationMs = Durations.ParseText(badge),
                    ThumbnailUrl = thumbnail,
                    VideoType = badge is null && r.FindAll("thumbnailBadgeViewModel").Any(b => b.Str("badgeStyle") == "THUMBNAIL_OVERLAY_BADGE_STYLE_LIVE") ? "live" : "ugc",
                    ViewsText = rows.LastOrDefault()?.FirstOrDefault(),
                };
            case "LOCKUP_CONTENT_TYPE_PLAYLIST" or "LOCKUP_CONTENT_TYPE_ALBUM":
                // Миксы RD… — бесконечные очереди, а не плейлисты
                if (id.StartsWith("RD", StringComparison.Ordinal) && !id.StartsWith("RDCLAK", StringComparison.Ordinal)) return null;
                return new PlaylistItem
                {
                    PlaylistId = id,
                    Title = title,
                    Subtitle = rows.FirstOrDefault() is { } first ? string.Join(" · ", first) : null,
                    ThumbnailUrl = thumbnail,
                };
            default:
                return null;
        }
    }

    public static ChannelPage Channel(string channelId, JsonNode response)
    {
        var header = response.At("header", "pageHeaderRenderer", "content", "pageHeaderViewModel");
        var metadata = response.At("metadata", "channelMetadataRenderer");
        var name = metadata.Str("title") ?? header.Str("title", "dynamicTextViewModel", "text", "content") ?? response.Str("header", "pageHeaderRenderer", "pageTitle") ?? "";
        var avatar = metadata.At("avatar", "thumbnails").BestThumbnail()
                     ?? header.At("image", "decoratedAvatarViewModel", "avatar", "avatarViewModel", "image", "sources").BestThumbnail();
        var subscribers = header.At("metadata", "contentMetadataViewModel", "metadataRows").Items()
            .SelectMany(row => row.Items("metadataParts"))
            .Select(p => p.Str("text", "content"))
            .FirstOrDefault(t => t is not null && t.Any(char.IsDigit) && !t.StartsWith('@'));

        var selected = response.At("contents", "twoColumnBrowseResultsRenderer", "tabs").Items()
            .Select(t => t.At("tabRenderer"))
            .FirstOrDefault(t => t.Bool("selected"));
        var grid = selected.At("content", "richGridRenderer", "contents");
        var page = grid is null ? new ItemsPage([], null) : GridPage(grid);
        var videos = page.Items.OfType<Track>()
            .Select(t => t with { Artists = [new ArtistRef(channelId, name.Trim())], ArtistsText = name.Trim() })
            .ToList();
        return new ChannelPage
        {
            ChannelId = channelId,
            Name = name.Trim(),
            ThumbnailUrl = avatar,
            SubscribersText = subscribers,
            Description = metadata.Str("description"),
            Videos = videos,
            Continuation = page.Continuation,
        };
    }
}
