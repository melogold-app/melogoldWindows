using Melogold.App.Views;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Melogold.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace Melogold.App.Services;

/// <summary>
/// Разбор входов (docs/PROMPT.md §3, REWRITE §2.3 Android): аргументы запуска и второго экземпляра, ссылки
/// <c>melogold://</c> (API §7.2), ссылки YouTube и текст из поля поиска. Автоматического входа или одобрения не бывает.
/// </summary>
public sealed class LinkRouter(Navigator navigator, YouTubeMusic music, PlayerEngine engine, Library library, Snackbar snackbar, ExternalLinkResolver external)
{
    /// <summary>
    /// Что откроется по тексту, для подсказки поиска: «Открыть ссылку: видео YouTube», «… плейлист Melogold», «… Spotify»;
    /// null — это не ссылка (обычный запрос).
    /// </summary>
    public static string? Describe(string text)
    {
        text = text.Trim();
        if (ShareLinkParser.Parse(text) is not null) return Loc.Format("OpenLinkFormat", Loc.Get("LinkKindShare"));
        if (text.StartsWith("melogold://", StringComparison.OrdinalIgnoreCase)) return Loc.Format("OpenLinkFormat", "Melogold");
        if (MusicServiceLinkParser.Parse(text) is { } service)
            return service.Kind == MusicLinkKind.Playlist ? Loc.Get("LinkImportLater") : Loc.Format("OpenLinkFormat", ServiceName(service.Service));
        return YouTubeLinkParser.Parse(text) switch
        {
            LinkTarget.Search or LinkTarget.Unsupported => null,
            LinkTarget.Video => Loc.Format("OpenLinkFormat", Loc.Get("LinkKindVideo")),
            LinkTarget.Playlist => Loc.Format("OpenLinkFormat", Loc.Get("LinkKindPlaylist")),
            LinkTarget.Album => Loc.Format("OpenLinkFormat", Loc.Get("LinkKindAlbum")),
            LinkTarget.External => Loc.Get("LinkImportLater"),
            _ => Loc.Format("OpenLinkFormat", Loc.Get("LinkKindChannel")),
        };
    }

    public static string ServiceName(MusicService service) => service switch
    {
        MusicService.Spotify => "Spotify",
        MusicService.AppleMusic => "Apple Music",
        MusicService.YandexMusic => Loc.Get("ServiceYandexMusic"),
        MusicService.Deezer => "Deezer",
        MusicService.Tidal => "Tidal",
        _ => "SoundCloud",
    };

    /// <summary>Аргументы командной строки: первая строка, похожая на ссылку, или весь текст.</summary>
    public void OpenArguments(IReadOnlyList<string> args)
    {
        var text = args.FirstOrDefault(a => a.Contains("://", StringComparison.Ordinal) || a.StartsWith("vnd.youtube:", StringComparison.OrdinalIgnoreCase))
                   ?? string.Join(' ', args);
        if (!string.IsNullOrWhiteSpace(text)) OpenText(text);
    }

    public void OpenText(string text)
    {
        text = text.Trim();
        if (text.StartsWith("melogold://", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("Open: melogold link");
            OpenMelogold(text);
            return;
        }
        // Снимок плейлиста Melogold: https://<сервер>/s/<код> (tasks/0016)
        if (ShareLinkParser.Parse(text) is { } share)
        {
            Log.Info("Open: shared playlist");
            navigator.Open(typeof(SharedPlaylistPage), share);
            return;
        }
        // Spotify, Apple Music, Яндекс Музыка, Deezer, Tidal, SoundCloud — тот же трек или альбом поиском
        if (MusicServiceLinkParser.Parse(text) is { } service)
        {
            Log.Info($"Open: {service.Service} {service.Kind} link");
            _ = OpenServiceAsync(service);
            return;
        }

        switch (YouTubeLinkParser.Parse(text))
        {
            case LinkTarget.Search search:
                if (!App.Services.GetRequiredService<SettingsStore>().PauseSearchHistory) library.AddSearch(search.Query);
                navigator.Open(typeof(SearchPage), new SearchRequest(search.Query));
                break;
            case LinkTarget.Video video:
                _ = PlayVideoAsync(video);
                break;
            case LinkTarget.Playlist playlist when playlist.PlaylistId.StartsWith("RD", StringComparison.Ordinal) && !playlist.PlaylistId.StartsWith("RDCLAK", StringComparison.Ordinal):
                snackbar.Show(Loc.Get("LinkUnsupported"));
                break;
            case LinkTarget.Playlist playlist:
                navigator.Open(typeof(PlaylistPage), playlist.PlaylistId);
                break;
            case LinkTarget.Album album:
                navigator.Open(typeof(AlbumPage), album.BrowseId);
                break;
            case LinkTarget.Channel channel:
                navigator.Open(typeof(ArtistPage), channel.ChannelId);
                break;
            case LinkTarget.Handle handle:
                _ = ResolveAsync($"https://www.youtube.com/@{handle.Name}");
                break;
            case LinkTarget.LegacyChannel legacy:
                _ = ResolveAsync(legacy.Url);
                break;
            case LinkTarget.External:
                snackbar.Show(Loc.Get("LinkImportLater"));
                break;
            case LinkTarget.Unsupported { Reason: LinkTarget.ReasonEmpty }:
                break;
            default:
                snackbar.Show(Loc.Get("LinkUnsupported"));
                break;
        }
    }

    /// <summary>Видео по ссылке: одиночный трек и радио; с <c>list=</c> — очередь плейлиста с этого видео; <c>t=</c> — позиция.</summary>
    private async Task PlayVideoAsync(LinkTarget.Video video)
    {
        try
        {
            if (video.PlaylistId is { } list && !list.StartsWith("RD", StringComparison.Ordinal))
            {
                var tracks = await music.PlaylistTracksAsync(list, 1000);
                var index = tracks.FindIndex(t => t.VideoId == video.VideoId);
                if (index >= 0)
                {
                    engine.PlayList(tracks, index);
                    return;
                }
            }
            var next = await music.NextAsync(video.VideoId);
            var track = next.Tracks.FirstOrDefault(t => t.VideoId == video.VideoId) ?? new Track { VideoId = video.VideoId, Title = video.VideoId };
            engine.PlaySingle(track, video.StartMs ?? 0);
        }
        catch (YouTubeException e)
        {
            Log.Warn("Link video failed", e);
            engine.PlaySingle(new Track { VideoId = video.VideoId, Title = video.VideoId }, video.StartMs ?? 0);
        }
    }

    private async Task ResolveAsync(string url)
    {
        try
        {
            if (await music.ResolveUrlAsync(url) is { } browseId) navigator.Open(typeof(ArtistPage), browseId);
            else snackbar.Show(Loc.Get("LinkUnsupported"));
        }
        catch (YouTubeException e)
        {
            Log.Warn("resolve_url failed", e);
            snackbar.Show(Loc.Get("ErrorOffline"));
        }
    }

    private void OpenMelogold(string link)
    {
        // melogold://share → «Плейлист по ссылке» (API §7.2): без входа, сохранить — только кнопкой
        if (ShareLinkParser.Parse(link) is { } share)
        {
            navigator.Open(typeof(SharedPlaylistPage), share);
            return;
        }
        if (link.StartsWith("melogold://share", StringComparison.OrdinalIgnoreCase))
        {
            snackbar.Show(Loc.Get("ShareGone"));
            return;
        }
        // melogold://server?… → «Сервер» с заполненным адресом; melogold://link?… — с экранами аккаунта (срез 5)
        navigator.Show("settings");
    }

    /// <summary>Ссылка другого сервиса: название и исполнитель со страницы — в поиск по музыке.</summary>
    private async Task OpenServiceAsync(MusicServiceLink link)
    {
        if (link.Kind == MusicLinkKind.Playlist)
        {
            snackbar.Show(Loc.Get("LinkImportLater"));
            return;
        }
        snackbar.Show(Loc.Format("LinkLookingFormat", ServiceName(link.Service)));
        switch (await external.ResolveAsync(link))
        {
            case ExternalResolution.Search search:
                snackbar.Dismiss(false);
                navigator.Open(typeof(SearchPage), new SearchRequest(search.Query, SearchScope.Music));
                break;
            case ExternalResolution.Playlist:
                snackbar.Show(Loc.Get("LinkImportLater"));
                break;
            case ExternalResolution.Offline:
                snackbar.Show(Loc.Get("ErrorOffline"));
                break;
            default:
                snackbar.Show(Loc.Get("LinkUnsupported"));
                break;
        }
    }
}
