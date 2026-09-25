namespace Melogold.Core.Music;

/// <summary>Исполнитель или канал в подписи трека: <c>Id</c> — browseId (<c>UC…</c>), может отсутствовать.</summary>
public sealed record ArtistRef(string? Id, string Name);

/// <summary>Элемент выдачи, полки или страницы YouTube Music и YouTube.</summary>
public abstract record MusicItem;

/// <summary>
/// Трек — любое видео YouTube (DESIGN §3.3): песня YTM, клип, обычное видео, трансляция. <see cref="VideoType"/>:
/// <c>song | video | ugc | live | podcast_episode</c> (как в <c>TrackDto</c> сервера).
/// </summary>
public sealed record Track : MusicItem
{
    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<ArtistRef> Artists { get; init; } = [];

    /// <summary>Подпись исполнителей как в YouTube («A, B и C»); для видео — имя канала.</summary>
    public string? ArtistsText { get; init; }

    public string? AlbumId { get; init; }
    public string? AlbumTitle { get; init; }
    public long? DurationMs { get; init; }
    public string? DurationText { get; init; }
    public string? ThumbnailUrl { get; init; }
    public bool Explicit { get; init; }
    public string? VideoType { get; init; }

    /// <summary>Только для показа: «1,2 млн просмотров» (числа из строк YouTube не разбираются).</summary>
    public string? ViewsText { get; init; }

    public bool Unavailable { get; init; }

    public bool IsVideo => VideoType is "video" or "ugc" or "live" or "podcast_episode";

    public string Subtitle => string.Join(" · ", new[] { ArtistsText, AlbumTitle }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>Альбом, сингл или EP (<c>MPREb_…</c>).</summary>
public sealed record AlbumItem : MusicItem
{
    public required string BrowseId { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<ArtistRef> Artists { get; init; } = [];
    public string? ArtistsText { get; init; }
    public string? Year { get; init; }

    /// <summary>«Альбом», «Сингл», «EP» — как пришло от YouTube.</summary>
    public string? TypeText { get; init; }

    public string? ThumbnailUrl { get; init; }

    /// <summary><c>OLAK5uy_…</c> — плейлист альбома для «Слушать».</summary>
    public string? PlaylistId { get; init; }

    public bool Explicit { get; init; }

    public string Subtitle => string.Join(" · ", new[] { TypeText, ArtistsText, Year }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>Исполнитель YouTube Music или канал YouTube (<c>UC…</c>).</summary>
public sealed record ArtistItem : MusicItem
{
    public required string BrowseId { get; init; }
    public required string Name { get; init; }
    public string? Subtitle { get; init; }
    public string? ThumbnailUrl { get; init; }

    /// <summary>Канал обычного YouTube без музыкального профиля.</summary>
    public bool IsChannel { get; init; }
}

/// <summary>Плейлист YouTube; <see cref="PlaylistId"/> без префикса <c>VL</c>.</summary>
public sealed record PlaylistItem : MusicItem
{
    public required string PlaylistId { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ThumbnailUrl { get; init; }

    public string BrowseId => "VL" + PlaylistId;
}

/// <summary>Плитка «Настроения и жанры».</summary>
public sealed record MoodItem : MusicItem
{
    public required string Title { get; init; }
    public required string BrowseId { get; init; }
    public string? Params { get; init; }

    /// <summary>Цвет полоски плитки, ARGB.</summary>
    public uint? Color { get; init; }
}

/// <summary>Полка страницы: заголовок, элементы и переход «Все ›».</summary>
public sealed record Shelf(string? Title, IReadOnlyList<MusicItem> Items)
{
    public string? MoreBrowseId { get; init; }
    public string? MoreParams { get; init; }

    public IEnumerable<Track> Tracks => Items.OfType<Track>();
}

public sealed record SearchSummary(MusicItem? TopResult, IReadOnlyList<MusicItem> Items);

/// <summary>Страница выдачи с продолжением.</summary>
public sealed record ItemsPage(IReadOnlyList<MusicItem> Items, string? Continuation);

public sealed record AlbumPage
{
    public required AlbumItem Album { get; init; }
    public string? Description { get; init; }
    public string? CountText { get; init; }
    public IReadOnlyList<Track> Tracks { get; init; } = [];
    public IReadOnlyList<Shelf> Shelves { get; init; } = [];
}

public sealed record ArtistPage
{
    public required string BrowseId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? SubscribersText { get; init; }
    public bool IsChannel { get; init; }
    public IReadOnlyList<Shelf> Shelves { get; init; } = [];

    /// <summary>Плейлист «Все треки» исполнителя, если YouTube его дал.</summary>
    public string? SongsPlaylistId { get; init; }

    /// <summary>Плейлист радио исполнителя (<c>RDEM…</c>), если дал.</summary>
    public string? RadioPlaylistId { get; init; }
}

public sealed record PlaylistPage
{
    public required PlaylistItem Playlist { get; init; }
    public string? Description { get; init; }
    public string? AuthorText { get; init; }
    public string? CountText { get; init; }
    public IReadOnlyList<Track> Tracks { get; init; } = [];
    public string? Continuation { get; init; }
}

/// <summary>Очередь из «Далее» (радио или плейлист), вкладки текста и похожих.</summary>
public sealed record NextPage
{
    public IReadOnlyList<Track> Tracks { get; init; } = [];
    public string? Continuation { get; init; }
    public string? PlaylistId { get; init; }
    public string? LyricsBrowseId { get; init; }
    public string? RelatedBrowseId { get; init; }
}

public sealed record ChannelPage
{
    public required string ChannelId { get; init; }
    public required string Name { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? SubscribersText { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<Track> Videos { get; init; } = [];
    public string? Continuation { get; init; }
}
