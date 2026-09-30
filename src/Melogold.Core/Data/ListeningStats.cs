using Melogold.Core.Music;

namespace Melogold.Core.Data;

/// <summary>Период «Итогов» (tasks/0015): неделя с понедельника, месяц, год, всё время — по местному времени.</summary>
public enum StatsPeriodKind
{
    Week,
    Month,
    Year,
    All,
}

public sealed record StatsPeriod(StatsPeriodKind Kind, DateOnly Start)
{
    /// <summary>Период, в котором <paramref name="today"/>.</summary>
    public static StatsPeriod Of(StatsPeriodKind kind, DateOnly today) => kind switch
    {
        StatsPeriodKind.Week => new(kind, today.AddDays(-(((int)today.DayOfWeek + 6) % 7))),
        StatsPeriodKind.Month => new(kind, new DateOnly(today.Year, today.Month, 1)),
        StatsPeriodKind.Year => new(kind, new DateOnly(today.Year, 1, 1)),
        _ => new(kind, DateOnly.MinValue),
    };

    public DateOnly End => Kind switch
    {
        StatsPeriodKind.Week => Start.AddDays(7),
        StatsPeriodKind.Month => Start.AddMonths(1),
        StatsPeriodKind.Year => Start.AddYears(1),
        _ => DateOnly.MaxValue,
    };

    public StatsPeriod Previous() => Kind switch
    {
        StatsPeriodKind.Week => this with { Start = Start.AddDays(-7) },
        StatsPeriodKind.Month => this with { Start = Start.AddMonths(-1) },
        StatsPeriodKind.Year => this with { Start = Start.AddYears(-1) },
        _ => this,
    };

    /// <summary>
    /// Год «Итогов года», которые предлагаются сами (карточка в Библиотеке, кнопка в «Итогах»): в декабре — этот год, в
    /// январе — прошлый; в другие месяцы — null.
    /// </summary>
    public static int? RecapYear(DateOnly today) => today.Month switch
    {
        12 => today.Year,
        1 => today.Year - 1,
        _ => null,
    };

    public StatsPeriod Next() => Kind switch
    {
        StatsPeriodKind.Week => this with { Start = Start.AddDays(7) },
        StatsPeriodKind.Month => this with { Start = Start.AddMonths(1) },
        StatsPeriodKind.Year => this with { Start = Start.AddYears(1) },
        _ => this,
    };

    /// <summary>Границы в мс эпохи: от полуночи первого дня до полуночи после последнего в <paramref name="zone"/>.</summary>
    public (long From, long To) Range(TimeZoneInfo zone) => Kind == StatsPeriodKind.All
        ? (long.MinValue, long.MaxValue)
        : (Midnight(Start, zone), Midnight(End, zone));

    private static long Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Полночь, которой нет (переход на летнее время), — первый час после неё
        while (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }
}

public sealed record StatsTrack(Track Track, long PlayTimeMs, int Plays);

/// <summary>Время суток «Итогов года»: ночь 0–5, утро 6–11, день 12–17, вечер 18–23.</summary>
public enum DayPart
{
    Night,
    Morning,
    Afternoon,
    Evening,
}

/// <param name="BrowseId">страница исполнителя; null — по своему имени или без карты исполнителей</param>
public sealed record StatsArtist(string Name, string? BrowseId, string? ThumbnailUrl, long PlayTimeMs, int Plays);

/// <param name="BrowseId">страница альбома; null — альбом из своей правки названия</param>
public sealed record StatsAlbum(string Title, string? BrowseId, string? ThumbnailUrl, long PlayTimeMs, int Plays);

/// <summary>Одно прослушивание Истории.</summary>
public sealed record StatsEvent(string VideoId, long PlayedAt, long PlayTimeMs);

/// <summary>
/// «Итоги» за период (tasks/0015): время, прослушивания, разные треки, исполнители и альбомы; сравнение с прошлым таким
/// же периодом; топы; «Когда вы слушали» (<see cref="Buckets"/> — по дням, у года по месяцам, у «всего времени» по
/// годам с <see cref="FirstBucketYear"/>) и «Время суток» (<see cref="Hours"/>, 24); открытия.
/// </summary>
public sealed record ListeningStats(
    StatsPeriod Period,
    long PlayTimeMs,
    int Plays,
    int Tracks,
    int Artists,
    int Albums,
    long? PreviousPlayTimeMs,
    IReadOnlyList<StatsTrack> TopTracks,
    IReadOnlyList<StatsArtist> TopArtists,
    IReadOnlyList<StatsAlbum> TopAlbums,
    IReadOnlyList<long> Buckets,
    int FirstBucketYear,
    IReadOnlyList<long> Hours,
    int Discoveries,
    IReadOnlyList<StatsTrack> TopDiscoveries)
{
    public bool IsEmpty => Plays == 0;

    /// <summary>Доля к прошлому периоду: 0,12 — «+12 %»; null — сравнивать не с чем.</summary>
    public double? Change => PreviousPlayTimeMs is > 0 and var previous ? (PlayTimeMs - previous) / (double)previous : null;

    /// <summary>Столбец «Когда вы слушали» (у года — месяц 0…11), когда слушали больше всего.</summary>
    public int? BestBucket => Buckets.Count == 0 || Buckets.Max() == 0 ? null : Buckets.ToList().IndexOf(Buckets.Max());

    /// <summary>Час (0…23), когда слушали больше всего.</summary>
    public int? BestHour => Hours.Max() == 0 ? null : Hours.ToList().IndexOf(Hours.Max());

    /// <summary>Любимое время суток: часть с наибольшим временем; null — прослушиваний нет.</summary>
    public DayPart? FavoriteDayPart
    {
        get
        {
            var parts = Enumerable.Range(0, 4).Select(part => Hours.Skip(part * 6).Take(6).Sum()).ToList();
            return parts.Max() == 0 ? null : (DayPart)parts.IndexOf(parts.Max());
        }
    }

    private static readonly string[] Separators = [", ", " & ", " feat. ", " ft. ", " Feat. ", " Ft. ", " FEAT. "];

    /// <summary>
    /// Исполнитель трека для топа: своя правка исполнителя; иначе первый по карте исполнителей; без карты — подпись до
    /// первого «, », « & », « feat. », « ft. ». null — не знаем.
    /// </summary>
    public static (string Name, string? BrowseId)? ArtistOf(Track original, TrackOverride? custom)
    {
        if (custom?.ArtistsText is { } own) return (FirstName(own), null);
        if (original.Artists.Count > 0) return (original.Artists[0].Name, original.Artists[0].Id);
        return original.ArtistsText is { Length: > 0 } text ? (FirstName(text), null) : null;
    }

    private static string FirstName(string text)
    {
        var end = text.Length;
        foreach (var separator in Separators)
        {
            var at = text.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0 && at < end) end = at;
        }
        return text[..end].Trim();
    }

    /// <summary>Альбом трека: своя правка альбома; иначе по карте альбома; без неё трек в топ альбомов не идёт.</summary>
    public static (string Title, string? BrowseId)? AlbumOf(Track original, TrackOverride? custom) =>
        custom?.AlbumTitle is { } own ? (own, null)
        : original.AlbumId is { } id ? (original.AlbumTitle ?? "", id)
        : null;

    /// <summary>Из базы библиотеки: <see cref="Library.Stats"/>.</summary>
    internal static ListeningStats Build(StatsPeriod period, TimeZoneInfo zone, Library library, HistoryDevice device, int top)
    {
        var input = library.StatsInput(period, zone, device);
        return Build(period, zone, input.Events, input.Tracks, library.Overrides(), input.FirstPlayed, input.Previous, top);
    }

    /// <summary>Подсчёт по событиям периода — без базы и сети (50 000 событий — доли секунды).</summary>
    /// <param name="tracks">треки событий как на YouTube</param>
    /// <param name="overrides">свои названия (tasks/0011): правка альбома кладёт трек в этот альбом, исполнителя — к нему</param>
    /// <param name="firstPlayed">первое прослушивание трека за всю историю (открытия)</param>
    public static ListeningStats Build(StatsPeriod period, TimeZoneInfo zone, IReadOnlyList<StatsEvent> events,
        IReadOnlyDictionary<string, Track> tracks, IReadOnlyDictionary<string, TrackOverride> overrides,
        IReadOnlyDictionary<string, long> firstPlayed, long? previousPlayTimeMs, int top = 50)
    {
        var (from, to) = period.Range(zone);
        long total = 0;
        var byTrack = new Dictionary<string, (long Ms, int Plays)>(StringComparer.Ordinal);
        var hours = new long[24];
        var firstYear = period.Kind == StatsPeriodKind.All && events.Count > 0
            ? events.Min(e => Local(e.PlayedAt, zone).Year)
            : period.Start.Year;
        var buckets = period.Kind switch
        {
            StatsPeriodKind.Week => new long[7],
            StatsPeriodKind.Month => new long[DateTime.DaysInMonth(period.Start.Year, period.Start.Month)],
            StatsPeriodKind.Year => new long[12],
            _ => new long[events.Count == 0 ? 0 : events.Max(e => Local(e.PlayedAt, zone).Year) - firstYear + 1],
        };
        foreach (var e in events)
        {
            if (e.PlayedAt < from || e.PlayedAt >= to) continue;
            total += e.PlayTimeMs;
            var (ms, plays) = byTrack.GetValueOrDefault(e.VideoId);
            byTrack[e.VideoId] = (ms + e.PlayTimeMs, plays + 1);
            var local = Local(e.PlayedAt, zone);
            hours[local.Hour] += e.PlayTimeMs;
            var bucket = period.Kind switch
            {
                StatsPeriodKind.Week or StatsPeriodKind.Month => local.DayNumber - period.Start.DayNumber,
                StatsPeriodKind.Year => local.Month - 1,
                _ => local.Year - firstYear,
            };
            if (bucket >= 0 && bucket < buckets.Length) buckets[bucket] += e.PlayTimeMs;
        }

        Track TrackOf(string id) => tracks.GetValueOrDefault(id) ?? new Track { VideoId = id, Title = id };
        TrackOverride? CustomOf(string id) => overrides.GetValueOrDefault(id);

        // Трек — как на YouTube (его играют и пишут в историю); свои названия подставляет показ
        var topTracks = byTrack.OrderByDescending(p => p.Value.Ms).ThenByDescending(p => p.Value.Plays).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new StatsTrack(TrackOf(p.Key), p.Value.Ms, p.Value.Plays)).ToList();

        var artists = new Dictionary<string, (string Name, string? Id, string? Thumb, long Ms, int Plays)>(StringComparer.OrdinalIgnoreCase);
        var albums = new Dictionary<string, (string Title, string? Id, string? Thumb, long Ms, int Plays)>(StringComparer.OrdinalIgnoreCase);
        // По убыванию времени: обложка исполнителя и альбома — у их самого слушаемого трека
        foreach (var entry in topTracks)
        {
            var id = entry.Track.VideoId;
            if (ArtistOf(TrackOf(id), CustomOf(id)) is var (name, artistId) && name.Length > 0)
            {
                var key = artistId ?? name;
                var known = artists.GetValueOrDefault(key, (name, artistId, entry.Track.ThumbnailUrl, 0, 0));
                artists[key] = known with { Ms = known.Ms + entry.PlayTimeMs, Plays = known.Plays + entry.Plays };
            }
            if (AlbumOf(TrackOf(id), CustomOf(id)) is var (title, albumId) && title.Length > 0)
            {
                var key = albumId ?? "custom:" + title;
                var known = albums.GetValueOrDefault(key, (title, albumId, entry.Track.ThumbnailUrl, 0, 0));
                albums[key] = known with { Ms = known.Ms + entry.PlayTimeMs, Plays = known.Plays + entry.Plays };
            }
        }

        var discovered = topTracks.Where(t => firstPlayed.TryGetValue(t.Track.VideoId, out var first) && first >= from && first < to).ToList();
        return new ListeningStats(
            period, total, byTrack.Values.Sum(v => v.Plays), byTrack.Count, artists.Count, albums.Count,
            period.Kind == StatsPeriodKind.All ? null : previousPlayTimeMs,
            topTracks.Take(top).ToList(),
            artists.Values.OrderByDescending(a => a.Ms).ThenByDescending(a => a.Plays).Take(top).Select(a => new StatsArtist(a.Name, a.Id, a.Thumb, a.Ms, a.Plays)).ToList(),
            albums.Values.OrderByDescending(a => a.Ms).ThenByDescending(a => a.Plays).Take(top).Select(a => new StatsAlbum(a.Title, a.Id, a.Thumb, a.Ms, a.Plays)).ToList(),
            buckets, firstYear, hours, discovered.Count, discovered.Take(5).ToList());
    }

    private static DateTime LocalTime(long ms, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone).DateTime;

    private static DateOnlyHour Local(long ms, TimeZoneInfo zone)
    {
        var time = LocalTime(ms, zone);
        return new DateOnlyHour(DateOnly.FromDateTime(time), time.Hour);
    }

    private readonly record struct DateOnlyHour(DateOnly Day, int Hour)
    {
        public int Year => Day.Year;
        public int Month => Day.Month;
        public int DayNumber => Day.DayNumber;
    }
}
