using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Melogold.Core.Music;

/// <summary>
/// Разбор рендереров YouTube Music (WEB_REMIX). Тип элемента определяется по переходу (<c>pageType</c>,
/// <c>musicVideoType</c>), а не по локализованной подписи; поля трека — по ссылкам внутри подписи (<c>UC…</c> —
/// исполнитель, <c>MPREb_…</c> — альбом), а не по позиции (REWRITE §4.8.2 Android).
/// </summary>
internal static partial class MusicParsers
{
    [GeneratedRegex(@"^\d{1,2}(:\d{2}){1,2}$")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex YearRegex();

    /// <summary>Подписи типа в смешанной выдаче: первая группа подписи, её выбрасываем.</summary>
    private static readonly HashSet<string> TypeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Song", "Video", "Album", "Single", "EP", "Playlist", "Artist", "Episode", "Podcast", "Profile", "Composition",
        "Композиция", "Песня", "Трек", "Видео", "Клип", "Альбом", "Сингл", "Плейлист", "Исполнитель", "Выпуск", "Подкаст", "Профиль",
    };

    private const string PageAlbum = "MUSIC_PAGE_TYPE_ALBUM";
    private const string PageAudiobook = "MUSIC_PAGE_TYPE_AUDIOBOOK";
    private const string PageArtist = "MUSIC_PAGE_TYPE_ARTIST";
    private const string PageUserChannel = "MUSIC_PAGE_TYPE_USER_CHANNEL";
    private const string PagePlaylist = "MUSIC_PAGE_TYPE_PLAYLIST";

    public static string? VideoTypeOf(string? musicVideoType) => musicVideoType switch
    {
        "MUSIC_VIDEO_TYPE_ATV" => "song",
        "MUSIC_VIDEO_TYPE_OMV" or "MUSIC_VIDEO_TYPE_OFFICIAL_SOURCE_MUSIC" => "video",
        "MUSIC_VIDEO_TYPE_UGC" => "ugc",
        "MUSIC_VIDEO_TYPE_PODCAST_EPISODE" => "podcast_episode",
        null => null,
        _ => "video",
    };

    private static string? MusicVideoType(JsonNode? watchEndpoint) =>
        watchEndpoint.Str("watchEndpointMusicSupportedConfigs", "watchEndpointMusicConfig", "musicVideoType");

    private static bool IsArtistRun(Run run) =>
        run.BrowseId is { } id && (id.StartsWith("UC", StringComparison.Ordinal) || id.StartsWith("FEmusic_library_privately_owned_artist", StringComparison.Ordinal))
        || run.PageType is PageArtist or PageUserChannel;

    private static bool IsAlbumRun(Run run) => run.BrowseId?.StartsWith("MPREb_", StringComparison.Ordinal) == true || run.PageType == PageAlbum;

    /// <summary>Подпись, разбитая по « • » на группы.</summary>
    private static List<List<Run>> Groups(IEnumerable<Run> runs)
    {
        var groups = new List<List<Run>> { new() };
        foreach (var run in runs)
        {
            if (run.IsSeparator)
            {
                if (groups[^1].Count > 0) groups.Add([]);
                continue;
            }
            groups[^1].Add(run);
        }
        if (groups[^1].Count == 0) groups.RemoveAt(groups.Count - 1);
        return groups;
    }

    private static string GroupText(List<Run> group) => string.Concat(group.Select(r => r.Text)).Trim();

    private static IReadOnlyList<ArtistRef> ArtistsOf(List<Run> group) =>
        group.Where(IsArtistRun).Select(r => new ArtistRef(r.BrowseId, r.Text.Trim())).ToList() is { Count: > 0 } linked
            ? linked
            : [new ArtistRef(null, GroupText(group))];

    private static bool IsExplicit(JsonNode? node) =>
        node.Items("badges").Any(b => b.Str("musicInlineBadgeRenderer", "icon", "iconType") == "MUSIC_EXPLICIT_BADGE");

    private static string? Thumb(JsonNode? renderer) =>
        renderer.At("thumbnail", "musicThumbnailRenderer", "thumbnail", "thumbnails").BestThumbnail()
        ?? renderer.At("thumbnailRenderer", "musicThumbnailRenderer", "thumbnail", "thumbnails").BestThumbnail()
        ?? renderer.At("thumbnail", "thumbnails").BestThumbnail();

    /// <summary>Что из подписи трека известно по группам: исполнители, альбом, длительность, просмотры.</summary>
    private sealed record TrackMeta(IReadOnlyList<ArtistRef> Artists, string? ArtistsText, string? AlbumId, string? AlbumTitle, string? Duration, string? Views, string? Year);

    private static TrackMeta MetaOf(List<List<Run>> groups)
    {
        List<Run>? artists = null;
        string? albumId = null, albumTitle = null, duration = null, views = null, year = null;
        var rest = new List<List<Run>>();
        var anyLinks = groups.Any(g => g.Any(r => r.BrowseId is not null));
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var text = GroupText(group);
            if (text.Length == 0) continue;
            if (group.Any(IsAlbumRun))
            {
                var album = group.First(IsAlbumRun);
                albumId = album.BrowseId;
                albumTitle = album.Text;
            }
            else if (artists is null && group.Any(IsArtistRun)) artists = group;
            else if (DurationRegex().IsMatch(text)) duration = text;
            else if (YearRegex().IsMatch(text)) year = text;
            else if (i == 0 && group.All(r => r.BrowseId is null) && (TypeLabels.Contains(text) || (anyLinks && groups.Count > 2))) continue;
            else if (group.All(r => r.BrowseId is null) && text.Any(char.IsDigit) && artists is not null) views ??= text;
            else rest.Add(group);
        }
        if (artists is null && rest.Count > 0)
        {
            artists = rest[0];
            rest.RemoveAt(0);
        }
        if (views is null && rest.Count > 0 && GroupText(rest[0]).Any(char.IsDigit)) views = GroupText(rest[0]);
        return new TrackMeta(
            artists is null ? [] : ArtistsOf(artists),
            artists is null ? null : GroupText(artists),
            albumId, albumTitle, duration, views, year);
    }

    // ---------- Строки ----------

    /// <summary><c>musicResponsiveListItemRenderer</c> → трек, альбом, исполнитель или плейлист; прочее (подкасты) — null.</summary>
    public static MusicItem? ResponsiveItem(JsonNode? r)
    {
        if (r is null) return null;
        var columns = r.Items("flexColumns").Select(c => c.At("musicResponsiveListItemFlexColumnRenderer", "text")).ToList();
        if (columns.Count == 0) return null;
        var title = columns[0].Text()?.Trim();
        if (string.IsNullOrEmpty(title)) return null;

        var browse = r.At("navigationEndpoint", "browseEndpoint");
        var browseId = browse.Str("browseId");
        var pageType = browse.Str("browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");
        var subtitleRuns = columns.Skip(1).SelectMany(c => c.Runs().Append(new Run(new JsonObject { ["text"] = " • " }))).ToList();
        var groups = Groups(subtitleRuns);
        var thumbnail = Thumb(r);
        var overlay = r.At("overlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint");

        if (browseId is not null)
        {
            switch (pageType)
            {
                case PageAlbum or PageAudiobook:
                {
                    var meta = MetaOf(groups);
                    return new AlbumItem
                    {
                        BrowseId = browseId,
                        Title = title,
                        Artists = meta.Artists,
                        ArtistsText = meta.ArtistsText,
                        Year = meta.Year,
                        TypeText = groups.Count > 0 && groups[0].All(x => x.BrowseId is null) && !YearRegex().IsMatch(GroupText(groups[0])) ? GroupText(groups[0]) : null,
                        ThumbnailUrl = thumbnail,
                        PlaylistId = overlay.Str("watchPlaylistEndpoint", "playlistId"),
                        Explicit = IsExplicit(r),
                    };
                }
                case PageArtist or PageUserChannel:
                    return new ArtistItem
                    {
                        BrowseId = browseId,
                        Name = title,
                        Subtitle = SubtitleWithoutType(groups),
                        ThumbnailUrl = thumbnail,
                        IsChannel = pageType == PageUserChannel,
                    };
                case PagePlaylist:
                    return new PlaylistItem
                    {
                        PlaylistId = browseId.StartsWith("VL", StringComparison.Ordinal) ? browseId[2..] : browseId,
                        Title = title,
                        Subtitle = SubtitleWithoutType(groups),
                        ThumbnailUrl = thumbnail,
                    };
            }
        }

        var titleWatch = columns[0].Runs().Select(x => x.Node.At("navigationEndpoint", "watchEndpoint")).FirstOrDefault(x => x is not null);
        var overlayWatch = overlay.At("watchEndpoint");
        var videoId = r.Str("playlistItemData", "videoId") ?? titleWatch.Str("videoId") ?? overlayWatch.Str("videoId");
        if (videoId is null) return null;
        var musicVideoType = MusicVideoType(titleWatch) ?? MusicVideoType(overlayWatch);
        if (musicVideoType == "MUSIC_VIDEO_TYPE_PODCAST_EPISODE") return null;

        var fixedText = r.Items("fixedColumns").Select(c => c.At("musicResponsiveListItemFixedColumnRenderer", "text").Text()).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var trackMeta = MetaOf(groups);
        var durationText = fixedText is not null && DurationRegex().IsMatch(fixedText.Trim()) ? fixedText.Trim() : trackMeta.Duration;
        return new Track
        {
            VideoId = videoId,
            Title = title,
            Artists = trackMeta.Artists,
            ArtistsText = trackMeta.ArtistsText,
            AlbumId = trackMeta.AlbumId,
            AlbumTitle = trackMeta.AlbumTitle,
            DurationText = durationText,
            DurationMs = Domain.Durations.ParseText(durationText),
            ThumbnailUrl = thumbnail,
            Explicit = IsExplicit(r),
            VideoType = VideoTypeOf(musicVideoType),
            ViewsText = trackMeta.Views,
            Unavailable = r.Str("musicItemRendererDisplayPolicy") == "MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT",
        };
    }

    private static string? SubtitleWithoutType(List<List<Run>> groups)
    {
        var parts = groups.Select(GroupText).Where(t => t.Length > 0).ToList();
        if (parts.Count > 1 && TypeLabels.Contains(parts[0])) parts.RemoveAt(0);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // ---------- Карточки ----------

    /// <summary><c>musicTwoRowItemRenderer</c> → альбом, исполнитель, плейлист или трек.</summary>
    public static MusicItem? TwoRowItem(JsonNode? r)
    {
        if (r is null) return null;
        var title = r.At("title").Text()?.Trim();
        if (string.IsNullOrEmpty(title)) return null;
        var subtitleRuns = r.At("subtitle").Runs();
        var groups = Groups(subtitleRuns);
        var thumbnail = Thumb(r);
        var nav = r.At("navigationEndpoint");

        if (nav.At("browseEndpoint") is { } browse)
        {
            var browseId = browse.Str("browseId");
            if (browseId is null) return null;
            var pageType = browse.Str("browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");
            switch (pageType)
            {
                case PageAlbum or PageAudiobook:
                {
                    var meta = MetaOf(groups);
                    var first = groups.Count > 0 ? GroupText(groups[0]) : null;
                    return new AlbumItem
                    {
                        BrowseId = browseId,
                        Title = title,
                        Artists = meta.Artists.Where(a => a.Id is not null).ToList(),
                        ArtistsText = meta.Artists.Any(a => a.Id is not null) ? meta.ArtistsText : null,
                        Year = meta.Year,
                        TypeText = first is not null && groups[0].All(x => x.BrowseId is null) && !YearRegex().IsMatch(first) ? first : null,
                        ThumbnailUrl = thumbnail,
                        PlaylistId = r.Str("thumbnailOverlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint", "watchPlaylistEndpoint", "playlistId"),
                        Explicit = r.Items("subtitleBadges").Any(b => b.Str("musicInlineBadgeRenderer", "icon", "iconType") == "MUSIC_EXPLICIT_BADGE"),
                    };
                }
                case PageArtist or PageUserChannel:
                    return new ArtistItem
                    {
                        BrowseId = browseId,
                        Name = title,
                        Subtitle = SubtitleWithoutType(groups),
                        ThumbnailUrl = thumbnail,
                        IsChannel = pageType == PageUserChannel,
                    };
                case PagePlaylist:
                    return new PlaylistItem
                    {
                        PlaylistId = browseId.StartsWith("VL", StringComparison.Ordinal) ? browseId[2..] : browseId,
                        Title = title,
                        Subtitle = SubtitleWithoutType(groups),
                        ThumbnailUrl = thumbnail,
                    };
                default:
                    return null;
            }
        }

        if (nav.At("watchEndpoint") is { } watch && watch.Str("videoId") is { } videoId)
        {
            var musicVideoType = MusicVideoType(watch);
            if (musicVideoType == "MUSIC_VIDEO_TYPE_PODCAST_EPISODE") return null;
            var meta = MetaOf(groups);
            return new Track
            {
                VideoId = videoId,
                Title = title,
                Artists = meta.Artists,
                ArtistsText = meta.ArtistsText,
                AlbumId = meta.AlbumId,
                AlbumTitle = meta.AlbumTitle,
                ThumbnailUrl = thumbnail,
                VideoType = VideoTypeOf(musicVideoType),
                ViewsText = meta.Views,
                DurationText = meta.Duration,
                DurationMs = Domain.Durations.ParseText(meta.Duration),
            };
        }
        return null;
    }

    /// <summary><c>musicNavigationButtonRenderer</c> → плитка настроения.</summary>
    public static MoodItem? NavigationButton(JsonNode? r)
    {
        var title = r.At("buttonText").Text();
        var browse = r.At("clickCommand", "browseEndpoint");
        var browseId = browse.Str("browseId");
        if (string.IsNullOrEmpty(title) || browseId is null) return null;
        var color = r.Long("solid", "leftStripeColor");
        return new MoodItem { Title = title, BrowseId = browseId, Params = browse.Str("params"), Color = color is null ? null : (uint)color.Value };
    }

    /// <summary>Строка очереди «Далее» (<c>playlistPanelVideoRenderer</c>).</summary>
    public static Track? PanelVideo(JsonNode? r)
    {
        if (r is null) return null;
        var videoId = r.Str("videoId") ?? r.Str("navigationEndpoint", "watchEndpoint", "videoId");
        var title = r.At("title").Text()?.Trim();
        if (videoId is null || string.IsNullOrEmpty(title)) return null;
        var meta = MetaOf(Groups(r.At("longBylineText").Runs()));
        var duration = r.At("lengthText").Text()?.Trim();
        var musicVideoType = MusicVideoType(r.At("navigationEndpoint", "watchEndpoint"));
        return new Track
        {
            VideoId = videoId,
            Title = title,
            Artists = meta.Artists,
            ArtistsText = meta.ArtistsText ?? r.At("shortBylineText").Text(),
            AlbumId = meta.AlbumId,
            AlbumTitle = meta.AlbumTitle,
            DurationText = duration,
            DurationMs = Domain.Durations.ParseText(duration),
            ThumbnailUrl = r.At("thumbnail", "thumbnails").BestThumbnail(),
            Explicit = IsExplicit(r),
            VideoType = VideoTypeOf(musicVideoType),
            Unavailable = r.Bool("unplayableText") || r.At("unplayableText") is not null,
        };
    }

    // ---------- Полки ----------

    public static IEnumerable<MusicItem> ItemsOf(JsonNode? contents)
    {
        foreach (var item in contents.Items())
        {
            MusicItem? parsed = null;
            if (item.At("musicResponsiveListItemRenderer") is { } row) parsed = ResponsiveItem(row);
            else if (item.At("musicTwoRowItemRenderer") is { } card) parsed = TwoRowItem(card);
            else if (item.At("musicNavigationButtonRenderer") is { } button) parsed = NavigationButton(button);
            else if (item.At("playlistPanelVideoRenderer") is { } panel) parsed = PanelVideo(panel);
            else if (item.At("musicMultiRowListItemRenderer") is not null) continue;
            if (parsed is not null) yield return parsed;
        }
    }

    /// <summary>Полки <c>sectionListRenderer.contents</c>: списки, карусели, сетки.</summary>
    public static List<Shelf> Shelves(JsonNode? sectionContents)
    {
        var shelves = new List<Shelf>();
        foreach (var section in sectionContents.Items())
        {
            if (section.At("musicShelfRenderer") is { } list)
            {
                var more = list.At("bottomEndpoint", "browseEndpoint") ?? list.At("title", "runs", 0, "navigationEndpoint", "browseEndpoint");
                shelves.Add(new Shelf(list.At("title").Text(), ItemsOf(list.At("contents")).ToList())
                {
                    MoreBrowseId = more.Str("browseId"),
                    MoreParams = more.Str("params"),
                });
            }
            else if (section.At("musicCarouselShelfRenderer") is { } carousel)
            {
                var header = carousel.At("header", "musicCarouselShelfBasicHeaderRenderer");
                var more = header.At("moreContentButton", "buttonRenderer", "navigationEndpoint", "browseEndpoint")
                           ?? header.At("title", "runs", 0, "navigationEndpoint", "browseEndpoint");
                shelves.Add(new Shelf(header.At("title").Text(), ItemsOf(carousel.At("contents")).ToList())
                {
                    MoreBrowseId = more.Str("browseId"),
                    MoreParams = more.Str("params"),
                });
            }
            else if (section.At("gridRenderer") is { } grid)
            {
                shelves.Add(new Shelf(grid.At("header", "gridHeaderRenderer", "title").Text(), ItemsOf(grid.At("items")).ToList()));
            }
            else if (section.At("musicPlaylistShelfRenderer") is { } playlist)
            {
                shelves.Add(new Shelf(null, ItemsOf(playlist.At("contents")).ToList()));
            }
            else if (section.At("itemSectionRenderer") is { } itemSection)
            {
                foreach (var inner in itemSection.Items("contents"))
                {
                    if (inner.At("gridRenderer") is { } innerGrid)
                        shelves.Add(new Shelf(innerGrid.At("header", "gridHeaderRenderer", "title").Text(), ItemsOf(innerGrid.At("items")).ToList()));
                }
            }
        }
        return shelves.Where(s => s.Items.Count > 0).ToList();
    }

    /// <summary>Токен продолжения: и старый <c>continuations[].nextContinuationData</c>, и новый <c>continuationItemRenderer</c>.</summary>
    public static string? Continuation(JsonNode? shelf)
    {
        var old = shelf.At("continuations", 0, "nextContinuationData", "continuation") ?? shelf.At("continuations", 0, "nextRadioContinuationData", "continuation");
        if (old is JsonValue v && v.TryGetValue<string>(out var token)) return token;
        var contents = shelf.At("contents") as JsonArray ?? shelf as JsonArray;
        return contents?.Select(c => c.Str("continuationItemRenderer", "continuationEndpoint", "continuationCommand", "token")).LastOrDefault(t => t is not null);
    }
}
