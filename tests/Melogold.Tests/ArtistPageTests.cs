using System.Text.Json.Nodes;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Xunit;

namespace Melogold.Tests;

/// <summary>tasks/0024: страница исполнителя — широкое фото и данные листа «Об исполнителе».</summary>
public sealed class ArtistPageTests
{
    private static ArtistDetails Artist(string name, string browseId) =>
        YouTubeMusic.ParseArtist(browseId, JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "artist", name + ".json")))!)!;

    [Fact]
    public void AboutSheetHasOnlyWhatYouTubeMusicGives()
    {
        var kino = Artist("kino", "UCL9NQ06h7I0CRUcGxPWMtkQ");
        Assert.Equal("Кино", kino.Name);
        // YouTube ставит неразрывные пробелы в числах — так и показываем
        Assert.Equal("14,3 млн слушателей в месяц", Spaces(kino.MonthlyListenersText));
        Assert.Equal("739 тыс.", Spaces(kino.SubscriberCount));
        Assert.Equal("Просмотров: 751 013 381", Spaces(kino.ViewsText));
        Assert.StartsWith("«Кино́» — одна из самых популярных", kino.Description);
        Assert.NotNull(kino.SongsPlaylistId);
        Assert.Contains(kino.Shelves, s => s.Items.Count > 0 && s.Items.All(i => i is Track));
        var mj = Artist("michael-jackson", "UCoIOOL7QKuBhQHVKL8y7BEQ");
        Assert.Equal("41,5 млн", Spaces(mj.SubscriberCount));
        Assert.Equal("Просмотров: 23 752 841 698", Spaces(mj.ViewsText));
    }

    private static string? Spaces(string? text) => text?.Replace((char)0xA0, ' ').Replace((char)0x202F, ' ');

    [Fact]
    public void HeaderPhotoIsWideAndStaysWide()
    {
        var url = Artist("michael-jackson", "UCoIOOL7QKuBhQHVKL8y7BEQ").ThumbnailUrl;
        var aspect = Thumbnails.Aspect(url);
        Assert.NotNull(aspect);
        Assert.InRange(aspect!.Value, 2.3, 2.5);
        var wide = Thumbnails.Wide(url, 1440)!;
        Assert.Matches("=w1440-h(599|600)-p-l90-rj$", wide);
        Assert.Equal(url![..url.LastIndexOf('=')], wide[..wide.LastIndexOf('=')]);
        // Хвост с лишними флагами (-dcga…) у «Кино» — тоже широкий
        Assert.InRange(Thumbnails.Aspect(Artist("kino", "UCL9NQ06h7I0CRUcGxPWMtkQ").ThumbnailUrl)!.Value, 2.3, 2.5);
        // Без размеров в адресе — квадрат, как раньше
        Assert.Null(Thumbnails.Aspect("https://lh3.googleusercontent.com/abc"));
        Assert.Equal("https://lh3.googleusercontent.com/abc=w400-h400-l90-rj", Thumbnails.Wide("https://lh3.googleusercontent.com/abc", 400));
        Assert.Null(Thumbnails.Aspect("https://i.ytimg.com/vi/aaaaaaaaaaa/hqdefault.jpg"));
    }
}
