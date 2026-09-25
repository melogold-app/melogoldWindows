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
public sealed class LinkRouter(Navigator navigator, YouTubeMusic music, PlayerEngine engine, Library library, Snackbar snackbar)
{
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
        // melogold://server?… → «Сервер» с заполненным адресом; melogold://link?… — с экранами аккаунта (срез 5)
        navigator.Show("settings");
    }
}
