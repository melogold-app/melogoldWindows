using System.Globalization;
using System.Text;

namespace Melogold.Core.Music;

/// <summary>
/// Лучший результат поиска (tasks/0023, доктрина §4.6): первым, крупной карточкой. YouTube Music сам ставит его первой
/// полкой выдачи «Всё» (<c>musicCardShelfRenderer</c>); если её нет, а имя исполнителя из выдачи совпадает с запросом —
/// без учёта регистра, ё/е и знаков — лучшим становится он.
/// </summary>
public static class SearchTopResult
{
    public static MusicItem? Pick(SearchSummary summary, string query)
    {
        if (summary.TopResult is { } top) return top;
        var wanted = Normalize(query);
        return wanted.Length == 0 ? null : summary.Items.OfType<ArtistItem>().FirstOrDefault(a => Normalize(a.Name) == wanted);
    }

    /// <summary>«Ёлка-Палка!» → «елка палка»: строчные, ё → е, буквы и цифры, пробел между словами.</summary>
    public static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.ToLower(CultureInfo.InvariantCulture).Replace('ё', 'е'))
        {
            if (char.IsLetterOrDigit(c))
            {
                if (space && result.Length > 0) result.Append(' ');
                result.Append(c);
                space = false;
            }
            else space = true;
        }
        return result.ToString();
    }

    /// <summary>Тот же объект: лучший результат строкой ниже не повторяется.</summary>
    public static bool Same(MusicItem a, MusicItem? b) => b is not null && (a, b) switch
    {
        (Track x, Track y) => x.VideoId == y.VideoId,
        (AlbumItem x, AlbumItem y) => x.BrowseId == y.BrowseId,
        (ArtistItem x, ArtistItem y) => x.BrowseId == y.BrowseId,
        (PlaylistItem x, PlaylistItem y) => x.PlaylistId == y.PlaylistId,
        _ => false,
    };
}
