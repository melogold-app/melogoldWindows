using Melogold.Core.Domain;

namespace Melogold.Core.Music;

/// <summary>
/// Своё название, исполнитель и альбом трека поверх метаданных YouTube (tasks/0011, API §4.8 <c>track.override.set</c>):
/// альбом, собранный из разрозненных видео (цензура удалила оригинал), выглядит одним альбомом на всех устройствах.
/// Только текст — обложки и файлы сервер не хранит. Пустое поле — как на YouTube.
/// </summary>
public sealed record TrackOverride(string? Title, string? ArtistsText, string? AlbumTitle)
{
    /// <summary>Как у сервера: поле обрезается до 500 единиц UTF-16.</summary>
    public const int FieldMax = 500;

    public static readonly TrackOverride None = new(null, null, null);

    /// <summary>Как у сервера (API §4.8): обрезка по краям и до 500 единиц UTF-16, пустое поле — без правки.</summary>
    public static TrackOverride Of(string? title, string? artistsText, string? albumTitle) => new(Field(title), Field(artistsText), Field(albumTitle));

    private static string? Field(string? value) => value?.Trim() is { Length: > 0 } text ? Utf16.Truncate(text, FieldMax) : null;

    /// <summary>Правки нет: все поля как на YouTube.</summary>
    public bool IsEmpty => Title is null && ArtistsText is null && AlbumTitle is null;

    /// <summary>Трек для показа: свои поля поверх YouTube. Исполнители-ссылки и остальное — как было.</summary>
    public Track Apply(Track track) => IsEmpty ? track : track with
    {
        Title = Title ?? track.Title,
        ArtistsText = ArtistsText ?? track.ArtistsText,
        AlbumTitle = AlbumTitle ?? track.AlbumTitle,
    };
}
