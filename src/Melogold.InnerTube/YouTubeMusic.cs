using Melogold.Core.Domain;
using Melogold.Core.Music;
using System.Text.Json.Nodes;

namespace Melogold.InnerTube;

/// <summary>Фильтр выдачи YouTube Music (параметры чипов поиска).</summary>
public enum MusicSearchFilter
{
    Songs,
    Videos,
    Albums,
    Artists,
    CommunityPlaylists,
    FeaturedPlaylists,
}

/// <summary>Фильтр выдачи обычного YouTube (REWRITE §4.8.1 Android).</summary>
public enum WebSearchFilter
{
    Videos,
    Channels,
    Playlists,
    Live,
}

/// <summary>
/// Каталог YouTube Music и обычного YouTube: поиск, страницы, «Далее», тексты. Ошибки — <see cref="YouTubeException"/>
/// с классом для экрана («Нет соединения», «Проверка на бота», «YouTube изменил страницу»).
/// </summary>
public sealed class YouTubeMusic(InnerTubeClient client)
{
    public InnerTubeClient Client { get; } = client;

    private static string Params(MusicSearchFilter filter) => filter switch
    {
        MusicSearchFilter.Songs => "EgWKAQIIAWoSEAMQCRAEEAUQChAQEBUQDhAR",
        MusicSearchFilter.Videos => "EgWKAQIQAWoSEAMQCRAEEAUQChAQEBUQDhAR",
        MusicSearchFilter.Albums => "EgWKAQIYAWoSEAMQCRAEEAUQChAQEBUQDhAR",
        MusicSearchFilter.Artists => "EgWKAQIgAWoSEAMQCRAEEAUQChAQEBUQDhAR",
        MusicSearchFilter.CommunityPlaylists => "EgeKAQQoAEABahIQAxAJEAQQBRAKEBAQFRAOEBE=",
        MusicSearchFilter.FeaturedPlaylists => "EgeKAQQoADgBahIQAxAJEAQQBRAKEBAQFRAOEBE=",
        _ => throw new ArgumentOutOfRangeException(nameof(filter)),
    };

    private static string Params(WebSearchFilter filter) => filter switch
    {
        WebSearchFilter.Videos => "EgIQAQ==",
        WebSearchFilter.Channels => "EgIQAg==",
        WebSearchFilter.Playlists => "EgIQAw==",
        WebSearchFilter.Live => "EgJAAQ==",
        _ => throw new ArgumentOutOfRangeException(nameof(filter)),
    };

    private Task<JsonNode> Music(string endpoint, JsonObject body, CancellationToken ct) =>
        Client.PostAsync(ClientProfile.WebRemix, endpoint, body, ct);

    private Task<JsonNode> Web(string endpoint, JsonObject body, CancellationToken ct) =>
        Client.PostAsync(ClientProfile.Web, endpoint, body, ct);

    // ---------- Поиск ----------

    /// <summary>YTM без фильтра: лучший результат и смешанная выдача (секция «Всё», REWRITE §4.8.5).</summary>
    public async Task<SearchSummary> SearchSummaryAsync(string query, CancellationToken ct = default)
    {
        var response = await Music("search", new JsonObject { ["query"] = query }, ct).ConfigureAwait(false);
        var sections = response.At("contents", "tabbedSearchResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents");
        MusicItem? top = null;
        var items = new List<MusicItem>();
        foreach (var section in sections.Items())
        {
            if (section.At("musicCardShelfRenderer") is { } card)
            {
                top ??= CardTop(card);
                items.AddRange(MusicParsers.ItemsOf(card.At("contents")));
            }
            else if (section.At("itemSectionRenderer") is { } itemSection) items.AddRange(MusicParsers.ItemsOf(itemSection.At("contents")));
            else if (section.At("musicShelfRenderer") is { } shelf) items.AddRange(MusicParsers.ItemsOf(shelf.At("contents")));
        }
        return new SearchSummary(top, Distinct(items));
    }

    private static MusicItem? CardTop(JsonNode card)
    {
        var title = card.At("title").Runs().FirstOrDefault();
        if (title.Node is null) return null;
        var subtitleRuns = card.At("subtitle").Runs();
        var thumbnail = card.At("thumbnail", "musicThumbnailRenderer", "thumbnail", "thumbnails").BestThumbnail();
        var synthetic = new JsonObject
        {
            ["flexColumns"] = new JsonArray(
                new JsonObject { ["musicResponsiveListItemFlexColumnRenderer"] = new JsonObject { ["text"] = card.At("title")?.DeepClone() } },
                new JsonObject { ["musicResponsiveListItemFlexColumnRenderer"] = new JsonObject { ["text"] = card.At("subtitle")?.DeepClone() } }),
            ["thumbnail"] = card.At("thumbnail")?.DeepClone(),
            ["navigationEndpoint"] = card.At("title", "runs", 0, "navigationEndpoint")?.DeepClone(),
        };
        if (title.WatchVideoId is { } videoId) synthetic["playlistItemData"] = new JsonObject { ["videoId"] = videoId };
        _ = subtitleRuns;
        _ = thumbnail;
        return MusicParsers.ResponsiveItem(synthetic);
    }

    /// <summary>YTM с фильтром; продолжение — <see cref="SearchContinuationAsync"/>.</summary>
    public async Task<ItemsPage> SearchAsync(string query, MusicSearchFilter filter, CancellationToken ct = default)
    {
        var response = await Music("search", new JsonObject { ["query"] = query, ["params"] = Params(filter) }, ct).ConfigureAwait(false);
        var sections = response.At("contents", "tabbedSearchResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents");
        var items = new List<MusicItem>();
        string? continuation = null;
        foreach (var section in sections.Items())
        {
            var shelf = section.At("musicShelfRenderer") ?? section.At("itemSectionRenderer");
            if (shelf is null) continue;
            items.AddRange(MusicParsers.ItemsOf(shelf.At("contents")));
            continuation ??= MusicParsers.Continuation(shelf);
        }
        return new ItemsPage(Distinct(items), continuation);
    }

    public async Task<ItemsPage> SearchContinuationAsync(string continuation, CancellationToken ct = default)
    {
        var response = await Music("search", new JsonObject { ["continuation"] = continuation }, ct).ConfigureAwait(false);
        var shelf = response.At("continuationContents", "musicShelfContinuation");
        if (shelf is not null) return new ItemsPage(MusicParsers.ItemsOf(shelf.At("contents")).ToList(), MusicParsers.Continuation(shelf));
        var appended = response.At("onResponseReceivedCommands", 0, "appendContinuationItemsAction", "continuationItems")
                       ?? response.At("onResponseReceivedActions", 0, "appendContinuationItemsAction", "continuationItems");
        return new ItemsPage(MusicParsers.ItemsOf(appended).ToList(), MusicParsers.Continuation(appended));
    }

    public async Task<IReadOnlyList<string>> SuggestionsAsync(string input, CancellationToken ct = default)
    {
        var response = await Music("music/get_search_suggestions", new JsonObject { ["input"] = input }, ct).ConfigureAwait(false);
        return response.FindAll("searchSuggestionRenderer")
            .Select(s => s.At("suggestion").Text())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct()
            .Take(10)
            .ToList();
    }

    /// <summary>Обычный YouTube (клиент WEB): видео, каналы, плейлисты, трансляции.</summary>
    public async Task<ItemsPage> SearchWebAsync(string query, WebSearchFilter filter, CancellationToken ct = default)
    {
        var response = await Web("search", new JsonObject { ["query"] = query, ["params"] = Params(filter) }, ct).ConfigureAwait(false);
        var sections = response.At("contents", "twoColumnSearchResultsRenderer", "primaryContents", "sectionListRenderer", "contents");
        return WebParsers.SearchPage(sections);
    }

    public async Task<ItemsPage> SearchWebContinuationAsync(string continuation, CancellationToken ct = default)
    {
        var response = await Web("search", new JsonObject { ["continuation"] = continuation }, ct).ConfigureAwait(false);
        var items = response.At("onResponseReceivedCommands", 0, "appendContinuationItemsAction", "continuationItems");
        return WebParsers.SearchPage(items);
    }

    // ---------- Разделы ----------

    /// <summary>«Обзор» YTM: новые альбомы, настроения, «В тренде», новые клипы.</summary>
    public Task<List<Shelf>> ExploreAsync(CancellationToken ct = default) => BrowseShelvesAsync("FEmusic_explore", null, ct);

    public Task<List<Shelf>> HomeAsync(CancellationToken ct = default) => BrowseShelvesAsync("FEmusic_home", null, ct);

    public Task<List<Shelf>> MoodsAsync(CancellationToken ct = default) => BrowseShelvesAsync("FEmusic_moods_and_genres", null, ct);

    public Task<List<Shelf>> NewReleasesAsync(CancellationToken ct = default) => BrowseShelvesAsync("FEmusic_new_releases_albums", null, ct);

    public Task<List<Shelf>> NewVideosAsync(CancellationToken ct = default) => BrowseShelvesAsync("FEmusic_new_releases_videos", null, ct);

    /// <summary>Полки произвольной страницы: настроение, «Все» полки исполнителя и т. п.</summary>
    public async Task<List<Shelf>> BrowseShelvesAsync(string browseId, string? parameters, CancellationToken ct = default)
    {
        var body = new JsonObject { ["browseId"] = browseId };
        if (parameters is not null) body["params"] = parameters;
        var response = await Music("browse", body, ct).ConfigureAwait(false);
        var sections = response.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents")
                       ?? response.At("contents", "twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer", "contents");
        if (sections is null) throw new YouTubeException(YouTubeErrorKind.Parser, $"No sections in {browseId}");
        return MusicParsers.Shelves(sections);
    }

    // ---------- Страницы ----------

    public async Task<AlbumDetails> AlbumAsync(string browseId, CancellationToken ct = default)
    {
        var response = await Music("browse", new JsonObject { ["browseId"] = browseId }, ct).ConfigureAwait(false);
        var header = response.At("contents", "twoColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0, "musicResponsiveHeaderRenderer");
        var secondary = response.At("contents", "twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer", "contents");
        if (header is null || secondary is null) throw new YouTubeException(YouTubeErrorKind.Parser, $"Album {browseId} has no header");

        var title = header.At("title").Text() ?? "";
        var subtitle = header.At("subtitle").Runs();
        var year = subtitle.Select(r => r.Text.Trim()).FirstOrDefault(t => t.Length == 4 && t.All(char.IsDigit));
        var typeText = subtitle.FirstOrDefault(r => !r.IsSeparator && r.Text.Trim().Length > 0).Text?.Trim();
        var artistRuns = header.At("straplineTextOne").Runs();
        var artists = artistRuns.Where(r => r.BrowseId is not null).Select(r => new ArtistRef(r.BrowseId, r.Text)).ToList();
        var artistsText = header.At("straplineTextOne").Text();
        var thumbnail = header.At("thumbnail", "musicThumbnailRenderer", "thumbnail", "thumbnails").BestThumbnail();

        var shelf = secondary.Items().Select(s => s.At("musicShelfRenderer")).FirstOrDefault(s => s is not null);
        var rows = MusicParsers.ItemsOf(shelf.At("contents")).OfType<Track>().ToList();
        var playlistId = shelf.At("contents").Items()
            .Select(c => c.Str("musicResponsiveListItemRenderer", "flexColumns", 0, "musicResponsiveListItemFlexColumnRenderer", "text", "runs", 0, "navigationEndpoint", "watchEndpoint", "playlistId"))
            .FirstOrDefault(p => p is not null);

        var album = new AlbumItem
        {
            BrowseId = browseId,
            Title = title,
            Artists = artists,
            ArtistsText = artistsText,
            Year = year,
            TypeText = typeText,
            ThumbnailUrl = thumbnail,
            PlaylistId = playlistId,
        };
        var tracks = rows.Select(t => t with
        {
            AlbumId = browseId,
            AlbumTitle = title,
            ThumbnailUrl = t.ThumbnailUrl ?? thumbnail,
            Artists = t.Artists.Count > 0 && t.Artists.Any(a => a.Id is not null) ? t.Artists : artists,
            ArtistsText = t.Artists.Any(a => a.Id is not null) ? t.ArtistsText : artistsText,
            VideoType = t.VideoType ?? "song",
        }).ToList();
        return new AlbumDetails
        {
            Album = album,
            Description = header.At("description", "musicDescriptionShelfRenderer", "description").Text(),
            CountText = header.At("secondSubtitle").Text(),
            Tracks = tracks,
            Shelves = MusicParsers.Shelves(secondary).Where(s => s.Items.Count > 0 && s.Items[0] is not Track).ToList(),
        };
    }

    public async Task<PlaylistDetails> PlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        var browseId = playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId : "VL" + playlistId;
        var response = await Music("browse", new JsonObject { ["browseId"] = browseId }, ct).ConfigureAwait(false);
        var header = response.At("contents", "twoColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0, "musicResponsiveHeaderRenderer")
                     ?? response.At("contents", "twoColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0, "musicEditablePlaylistDetailHeaderRenderer", "header", "musicResponsiveHeaderRenderer")
                     ?? response.At("header", "musicDetailHeaderRenderer");
        var secondary = response.At("contents", "twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer", "contents")
                        ?? response.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents");
        var shelf = secondary.Items().Select(s => s.At("musicPlaylistShelfRenderer") ?? s.At("musicShelfRenderer")).FirstOrDefault(s => s is not null);
        if (header is null && shelf is null) throw new YouTubeException(YouTubeErrorKind.Parser, $"Playlist {playlistId} has no content");

        var id = browseId[2..];
        var title = header.At("title").Text() ?? "";
        var thumbnail = header.At("thumbnail", "musicThumbnailRenderer", "thumbnail", "thumbnails").BestThumbnail()
                        ?? header.At("thumbnail", "croppedSquareThumbnailRenderer", "thumbnail", "thumbnails").BestThumbnail();
        var author = header.At("straplineTextOne").Text() ?? header.Str("facepile", "avatarStackViewModel", "text", "content");
        var tracks = MusicParsers.ItemsOf(shelf.At("contents")).OfType<Track>().ToList();
        return new PlaylistDetails
        {
            Playlist = new PlaylistItem { PlaylistId = id, Title = title, ThumbnailUrl = thumbnail, Subtitle = author },
            AuthorText = author,
            Description = header.At("description", "musicDescriptionShelfRenderer", "description").Text(),
            CountText = header.At("secondSubtitle").Text(),
            Tracks = tracks,
            Continuation = MusicParsers.Continuation(shelf),
        };
    }

    /// <summary>Следующая страница плейлиста (ответы «старые» и «новые» — REWRITE §4.8.3).</summary>
    public async Task<ItemsPage> PlaylistContinuationAsync(string continuation, CancellationToken ct = default)
    {
        var response = await Music("browse", new JsonObject { ["continuation"] = continuation }, ct).ConfigureAwait(false);
        var old = response.At("continuationContents", "musicPlaylistShelfContinuation") ?? response.At("continuationContents", "musicShelfContinuation");
        if (old is not null) return new ItemsPage(MusicParsers.ItemsOf(old.At("contents")).ToList(), MusicParsers.Continuation(old));
        var appended = response.At("onResponseReceivedActions", 0, "appendContinuationItemsAction", "continuationItems");
        return new ItemsPage(MusicParsers.ItemsOf(appended).ToList(), MusicParsers.Continuation(appended));
    }

    /// <summary>Весь плейлист со всеми продолжениями (для «Сохранить», «Слушать» длинного списка).</summary>
    public async Task<List<Track>> PlaylistTracksAsync(string playlistId, int max = 5000, CancellationToken ct = default)
    {
        var page = await PlaylistAsync(playlistId, ct).ConfigureAwait(false);
        var tracks = page.Tracks.ToList();
        var seen = tracks.Select(t => t.VideoId).ToHashSet();
        var continuation = page.Continuation;
        while (continuation is not null && tracks.Count < max)
        {
            var next = await PlaylistContinuationAsync(continuation, ct).ConfigureAwait(false);
            var fresh = next.Items.OfType<Track>().Where(t => seen.Add(t.VideoId)).ToList();
            tracks.AddRange(fresh);
            if (next.Continuation == continuation || next.Items.Count == 0) break;
            continuation = next.Continuation;
        }
        return tracks;
    }

    /// <summary>Исполнитель YTM; если музыкального профиля нет — канал обычного YouTube (REWRITE §4.8.5).</summary>
    public async Task<ArtistDetails> ArtistAsync(string browseId, CancellationToken ct = default)
    {
        var response = await Music("browse", new JsonObject { ["browseId"] = browseId }, ct).ConfigureAwait(false);
        var header = response.At("header", "musicImmersiveHeaderRenderer") ?? response.At("header", "musicVisualHeaderRenderer")
                     ?? response.At("header", "musicHeaderRenderer");
        var sections = response.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents");
        var shelves = sections is null ? [] : MusicParsers.Shelves(sections);
        if (header is null || shelves.Count == 0)
        {
            var channel = await ChannelAsync(browseId, ct).ConfigureAwait(false);
            return new ArtistDetails
            {
                BrowseId = browseId,
                Name = channel.Name,
                ThumbnailUrl = channel.ThumbnailUrl,
                SubscribersText = channel.SubscribersText,
                Description = channel.Description,
                IsChannel = true,
                Shelves = channel.Videos.Count > 0 ? [new Shelf(null, channel.Videos)] : [],
            };
        }

        var songsShelf = sections.Items().Select(s => s.At("musicShelfRenderer")).FirstOrDefault(s => s is not null);
        var songsBrowse = songsShelf.At("title", "runs", 0, "navigationEndpoint", "browseEndpoint", "browseId")
                          ?? songsShelf.At("bottomEndpoint", "browseEndpoint", "browseId");
        string? songsPlaylist = songsBrowse is JsonValue sv && sv.TryGetValue<string>(out var s) && s.StartsWith("VL", StringComparison.Ordinal) ? s[2..] : null;
        return new ArtistDetails
        {
            BrowseId = browseId,
            Name = header.At("title").Text() ?? "",
            Description = header.At("description").Text(),
            ThumbnailUrl = header.At("thumbnail", "musicThumbnailRenderer", "thumbnail", "thumbnails").BestThumbnail(),
            SubscribersText = header.At("subscriptionButton", "subscribeButtonRenderer", "longSubscriberCountText").Text()
                              ?? header.At("monthlyListenerCount").Text(),
            Shelves = shelves,
            SongsPlaylistId = songsPlaylist,
            RadioPlaylistId = header.Str("startRadioButton", "buttonRenderer", "navigationEndpoint", "watchPlaylistEndpoint", "playlistId")
                              ?? header.Str("startRadioButton", "buttonRenderer", "navigationEndpoint", "watchEndpoint", "playlistId"),
        };
    }

    /// <summary>Канал обычного YouTube (клиент WEB): шапка и вкладка «Видео».</summary>
    public async Task<ChannelPage> ChannelAsync(string channelId, CancellationToken ct = default)
    {
        var response = await Web("browse", new JsonObject { ["browseId"] = channelId, ["params"] = "EgZ2aWRlb3PyBgQKAjoA" }, ct).ConfigureAwait(false);
        return WebParsers.Channel(channelId, response);
    }

    public async Task<ItemsPage> ChannelContinuationAsync(string continuation, CancellationToken ct = default)
    {
        var response = await Web("browse", new JsonObject { ["continuation"] = continuation }, ct).ConfigureAwait(false);
        var items = response.At("onResponseReceivedActions", 0, "appendContinuationItemsAction", "continuationItems");
        return WebParsers.GridPage(items);
    }

    /// <summary>
    /// Ссылка <c>/@handle</c>, <c>/c/…</c>, <c>/user/…</c> → browseId канала (<c>navigation/resolve_url</c> клиента WEB).
    /// </summary>
    public async Task<string?> ResolveUrlAsync(string url, CancellationToken ct = default)
    {
        var response = await Web("navigation/resolve_url", new JsonObject { ["url"] = url }, ct).ConfigureAwait(false);
        return response.Str("endpoint", "browseEndpoint", "browseId");
    }

    // ---------- «Далее», текст, похожие ----------

    /// <summary>
    /// Очередь «Далее»: радио по треку (<c>RDAMVM&lt;videoId&gt;</c>, REWRITE §4.10.5) или плейлист с этого трека.
    /// </summary>
    public async Task<NextPage> NextAsync(string videoId, string? playlistId = null, string? parameters = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["videoId"] = videoId, ["isAudioOnly"] = true, ["enablePersistentPlaylistPanel"] = true, ["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL" };
        if (playlistId is not null) body["playlistId"] = playlistId;
        if (parameters is not null) body["params"] = parameters;
        var response = await Music("next", body, ct).ConfigureAwait(false);
        var tabs = response.At("contents", "singleColumnMusicWatchNextResultsRenderer", "tabbedRenderer", "watchNextTabbedResultsRenderer", "tabs");
        var panel = tabs.At(0, "tabRenderer", "content", "musicQueueRenderer", "content", "playlistPanelRenderer");
        string? lyrics = null, related = null;
        foreach (var tab in tabs.Items())
        {
            var browse = tab.At("tabRenderer", "endpoint", "browseEndpoint");
            var pageType = browse.Str("browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");
            if (pageType == "MUSIC_PAGE_TYPE_TRACK_LYRICS" && !tab.Bool("tabRenderer", "unselectable")) lyrics = browse.Str("browseId");
            if (pageType == "MUSIC_PAGE_TYPE_TRACK_RELATED") related = browse.Str("browseId");
        }
        return new NextPage
        {
            Tracks = PanelTracks(panel),
            Continuation = MusicParsers.Continuation(panel),
            PlaylistId = panel.Str("playlistId"),
            LyricsBrowseId = lyrics,
            RelatedBrowseId = related,
        };
    }

    public async Task<NextPage> NextContinuationAsync(string continuation, string? playlistId, CancellationToken ct = default)
    {
        var body = new JsonObject { ["continuation"] = continuation, ["isAudioOnly"] = true, ["enablePersistentPlaylistPanel"] = true };
        if (playlistId is not null) body["playlistId"] = playlistId;
        var response = await Music("next", body, ct).ConfigureAwait(false);
        var panel = response.At("continuationContents", "playlistPanelContinuation");
        return new NextPage { Tracks = PanelTracks(panel), Continuation = MusicParsers.Continuation(panel), PlaylistId = playlistId };
    }

    private static List<Track> PanelTracks(JsonNode? panel)
    {
        var tracks = new List<Track>();
        foreach (var item in panel.Items("contents"))
        {
            var renderer = item.At("playlistPanelVideoRenderer") ?? item.At("playlistPanelVideoWrapperRenderer", "primaryRenderer", "playlistPanelVideoRenderer");
            if (MusicParsers.PanelVideo(renderer) is { } track) tracks.Add(track);
        }
        return tracks;
    }

    /// <summary>Обычный текст песни из вкладки «Текст» и его источник.</summary>
    public async Task<(string Text, string? Source)?> LyricsAsync(string lyricsBrowseId, CancellationToken ct = default)
    {
        var response = await Music("browse", new JsonObject { ["browseId"] = lyricsBrowseId }, ct).ConfigureAwait(false);
        var shelf = response.Find("musicDescriptionShelfRenderer");
        var text = shelf.At("description").Text();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return (text, shelf.At("footer").Text());
    }

    /// <summary>
    /// Синхронный текст YouTube Music: ту же вкладку клиент ANDROID_MUSIC отдаёт со временем строк
    /// (<c>timedLyricsModel</c>). Возвращает LRC или null, если времени у строк нет.
    /// </summary>
    public async Task<string?> TimedLyricsAsync(string lyricsBrowseId, CancellationToken ct = default)
    {
        var response = await Client.PostAsync(ClientProfile.AndroidMusic, "browse", new JsonObject { ["browseId"] = lyricsBrowseId }, ct).ConfigureAwait(false);
        var data = response.Find("timedLyricsData");
        var lines = new List<string>();
        foreach (var line in data.Items())
        {
            if (line.Long("cueRange", "startTimeMilliseconds") is not { } start) continue;
            var centis = start / 10;
            lines.Add($"[{centis / 6000:00}:{centis / 100 % 60:00}.{centis % 100:00}]{line.Str("lyricLine")}");
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>Вкладка «Похожие»: треки, альбомы, исполнители (для «Для вас» и автовоспроизведения).</summary>
    public async Task<List<Shelf>> RelatedAsync(string relatedBrowseId, CancellationToken ct = default)
    {
        var response = await Music("browse", new JsonObject { ["browseId"] = relatedBrowseId }, ct).ConfigureAwait(false);
        var sections = response.At("contents", "sectionListRenderer", "contents");
        return MusicParsers.Shelves(sections);
    }

    private static List<MusicItem> Distinct(IEnumerable<MusicItem> items)
    {
        var seen = new HashSet<string>();
        var result = new List<MusicItem>();
        foreach (var item in items)
        {
            var key = item switch
            {
                Track t => "t:" + t.VideoId,
                AlbumItem a => "a:" + a.BrowseId,
                ArtistItem r => "r:" + r.BrowseId,
                PlaylistItem p => "p:" + p.PlaylistId,
                MoodItem m => "m:" + m.BrowseId + m.Params,
                _ => null,
            };
            if (key is null || seen.Add(key)) result.Add(item);
        }
        return result;
    }
}
