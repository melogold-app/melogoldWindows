using System.Diagnostics;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Xunit;

namespace Melogold.Tests;

/// <summary>tasks/0015: «Итоги» — периоды в местном времени, топы, столбцы, открытия.</summary>
public sealed class ListeningStatsTests : IDisposable
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-stats-{Guid.NewGuid():N}");

    public ListeningStatsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    private static long At(TimeZoneInfo zone, int year, int month, int day, int hour = 12) =>
        new DateTimeOffset(new DateTime(year, month, day, hour, 0, 0), zone.GetUtcOffset(new DateTime(year, month, day, hour, 0, 0))).ToUnixTimeMilliseconds();

    private static Track T(string id, string title, string? artists = null, IReadOnlyList<ArtistRef>? refs = null, string? albumId = null, string? album = null) =>
        new() { VideoId = id, Title = title, ArtistsText = artists, Artists = refs ?? [], AlbumId = albumId, AlbumTitle = album };

    [Fact]
    public void PeriodsStartOnMondayFirstDayAndNewYear()
    {
        var wednesday = new DateOnly(2026, 9, 30);
        Assert.Equal(new DateOnly(2026, 9, 28), StatsPeriod.Of(StatsPeriodKind.Week, wednesday).Start);
        Assert.Equal(new DateOnly(2026, 9, 28), StatsPeriod.Of(StatsPeriodKind.Week, new DateOnly(2026, 9, 28)).Start);
        Assert.Equal(new DateOnly(2026, 9, 21), StatsPeriod.Of(StatsPeriodKind.Week, new DateOnly(2026, 9, 27)).Start);
        Assert.Equal(new DateOnly(2026, 9, 1), StatsPeriod.Of(StatsPeriodKind.Month, wednesday).Start);
        Assert.Equal(new DateOnly(2026, 8, 1), StatsPeriod.Of(StatsPeriodKind.Month, wednesday).Previous().Start);
        Assert.Equal(new DateOnly(2026, 1, 1), StatsPeriod.Of(StatsPeriodKind.Year, wednesday).Start);

        // Полночь по Москве — 21:00 UTC накануне
        var (from, to) = StatsPeriod.Of(StatsPeriodKind.Month, wednesday).Range(Moscow);
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T21:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), from);
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T21:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), to);

        // Март в Берлине — 31 день, из них один на 23 часа (переход на летнее время)
        var (march, april) = new StatsPeriod(StatsPeriodKind.Month, new DateOnly(2026, 3, 1)).Range(Berlin);
        Assert.Equal(31 * 24 - 1, (april - march) / 3_600_000);
    }

    [Fact]
    public void CountsTopsBucketsAndHours()
    {
        var month = new StatsPeriod(StatsPeriodKind.Month, new DateOnly(2026, 9, 1));
        var events = new List<StatsEvent>
        {
            new("aaaaaaaaaaa", At(Moscow, 2026, 9, 1, 8), 200_000),
            new("aaaaaaaaaaa", At(Moscow, 2026, 9, 2, 8), 200_000),
            new("bbbbbbbbbbb", At(Moscow, 2026, 9, 2, 23), 100_000),
            // 31 августа 23:30 по Москве — в сентябрь не входит
            new("bbbbbbbbbbb", At(Moscow, 2026, 8, 31, 23) + 30 * 60_000, 500_000),
        };
        var tracks = new Dictionary<string, Track>
        {
            ["aaaaaaaaaaa"] = T("aaaaaaaaaaa", "Песня", "Кино", [new ArtistRef("UCkino", "Кино")], "MPREb_1", "Группа крови"),
            ["bbbbbbbbbbb"] = T("bbbbbbbbbbb", "Видео", "Some Channel feat. Guest"),
        };
        var stats = ListeningStats.Build(month, Moscow, events, tracks, new Dictionary<string, TrackOverride>(), new Dictionary<string, long>(), 250_000);
        Assert.Equal(500_000, stats.PlayTimeMs);
        Assert.Equal(3, stats.Plays);
        Assert.Equal(2, stats.Tracks);
        Assert.Equal(2, stats.Artists);
        Assert.Equal(1, stats.Albums);
        Assert.Equal(1.0, stats.Change);
        Assert.Equal("aaaaaaaaaaa", stats.TopTracks[0].Track.VideoId);
        Assert.Equal(2, stats.TopTracks[0].Plays);
        Assert.Equal(("Кино", "UCkino"), (stats.TopArtists[0].Name, stats.TopArtists[0].BrowseId));
        // Без карты исполнителей — подпись до « feat. »
        Assert.Equal("Some Channel", stats.TopArtists[1].Name);
        Assert.Equal(30, stats.Buckets.Count);
        Assert.Equal(200_000, stats.Buckets[0]);
        Assert.Equal(300_000, stats.Buckets[1]);
        Assert.Equal(400_000, stats.Hours[8]);
        Assert.Equal(100_000, stats.Hours[23]);
    }

    [Theory]
    [InlineData("A, B", "A")]
    [InlineData("A & B", "A")]
    [InlineData("A feat. B", "A")]
    [InlineData("A ft. B", "A")]
    [InlineData("Simon & Garfunkel", "Simon")]
    [InlineData("Одно имя", "Одно имя")]
    public void ArtistWithoutMapIsTheFirstName(string text, string expected) =>
        Assert.Equal(expected, ListeningStats.ArtistOf(T("aaaaaaaaaaa", "x", text), null)?.Name);

    [Fact]
    public void OverridePutsTheTrackIntoItsAlbumAndArtist()
    {
        var year = new StatsPeriod(StatsPeriodKind.Year, new DateOnly(2026, 1, 1));
        var events = new List<StatsEvent> { new("aaaaaaaaaaa", At(Moscow, 2026, 3, 1), 60_000), new("bbbbbbbbbbb", At(Moscow, 2026, 7, 1), 90_000) };
        var tracks = new Dictionary<string, Track>
        {
            ["aaaaaaaaaaa"] = T("aaaaaaaaaaa", "Artist — Song (fan upload)", "Channel 1"),
            ["bbbbbbbbbbb"] = T("bbbbbbbbbbb", "Artist - Other (live)", "Channel 2"),
        };
        var overrides = new Dictionary<string, TrackOverride>
        {
            ["aaaaaaaaaaa"] = TrackOverride.Of("Песня", "Исполнитель", "Альбом"),
            ["bbbbbbbbbbb"] = TrackOverride.Of("Другая", "Исполнитель", "Альбом"),
        };
        var stats = ListeningStats.Build(year, Moscow, events, tracks, overrides, new Dictionary<string, long>(), null);
        Assert.Equal("Альбом", Assert.Single(stats.TopAlbums).Title);
        Assert.Equal(150_000, stats.TopAlbums[0].PlayTimeMs);
        Assert.Equal("Исполнитель", Assert.Single(stats.TopArtists).Name);
        Assert.Equal("bbbbbbbbbbb", stats.TopTracks[0].Track.VideoId);
        Assert.Equal(12, stats.Buckets.Count);
        Assert.Equal(6, stats.BestBucket);
    }

    [Fact]
    public void DiscoveriesAreFirstPlaysInThePeriod()
    {
        var week = new StatsPeriod(StatsPeriodKind.Week, new DateOnly(2026, 9, 28));
        var events = new List<StatsEvent> { new("aaaaaaaaaaa", At(Moscow, 2026, 9, 29), 60_000), new("bbbbbbbbbbb", At(Moscow, 2026, 9, 30), 60_000) };
        var first = new Dictionary<string, long> { ["aaaaaaaaaaa"] = At(Moscow, 2026, 9, 29), ["bbbbbbbbbbb"] = At(Moscow, 2025, 1, 1) };
        var stats = ListeningStats.Build(week, Moscow, events, new Dictionary<string, Track>(), new Dictionary<string, TrackOverride>(), first, null);
        Assert.Equal(1, stats.Discoveries);
        Assert.Equal("aaaaaaaaaaa", Assert.Single(stats.TopDiscoveries).Track.VideoId);
        Assert.Equal(7, stats.Buckets.Count);
    }

    [Fact]
    public void AllTimeBucketsAreYears()
    {
        var all = StatsPeriod.Of(StatsPeriodKind.All, new DateOnly(2026, 9, 30));
        var events = new List<StatsEvent> { new("aaaaaaaaaaa", At(Moscow, 2024, 5, 1), 10), new("aaaaaaaaaaa", At(Moscow, 2026, 5, 1), 20) };
        var stats = ListeningStats.Build(all, Moscow, events, new Dictionary<string, Track>(), new Dictionary<string, TrackOverride>(), new Dictionary<string, long>(), 5);
        Assert.Equal(2024, stats.FirstBucketYear);
        Assert.Equal([10L, 0L, 20L], stats.Buckets);
        Assert.Null(stats.Change);
    }

    [Fact]
    public void LibraryFiltersByDevice()
    {
        var library = new Library(new LibraryDatabase(Path.Combine(_directory, "library.db")));
        var track = T("aaaaaaaaaaa", "Песня", "Кино");
        library.SaveTracks([track]);
        var september = At(Moscow, 2026, 9, 10);
        new SyncStore(library).Run(tx =>
        {
            tx.InsertPlay("e1", "aaaaaaaaaaa", september, 60_000, null);
            tx.InsertPlay("e2", "aaaaaaaaaaa", september + 1000, 30_000, "phone");
        });
        var month = new StatsPeriod(StatsPeriodKind.Month, new DateOnly(2026, 9, 1));
        Assert.Equal(90_000, library.Stats(month, Moscow).PlayTimeMs);
        Assert.Equal(30_000, library.Stats(month, Moscow, HistoryDevice.Other("phone")).PlayTimeMs);
        Assert.True(library.Stats(new StatsPeriod(StatsPeriodKind.Month, new DateOnly(2026, 8, 1)), Moscow).IsEmpty);
    }

    [Fact]
    public void RecapIsOfferedInDecemberAndJanuary()
    {
        Assert.Equal(2026, StatsPeriod.RecapYear(new DateOnly(2026, 12, 1)));
        Assert.Equal(2026, StatsPeriod.RecapYear(new DateOnly(2027, 1, 31)));
        Assert.Null(StatsPeriod.RecapYear(new DateOnly(2027, 2, 1)));
        Assert.Null(StatsPeriod.RecapYear(new DateOnly(2026, 11, 30)));
    }

    [Fact]
    public void FavoriteDayPartSumsItsHours()
    {
        var year = new StatsPeriod(StatsPeriodKind.Year, new DateOnly(2026, 1, 1));
        // Один долгий час ночью меньше, чем три вечерних
        var events = new List<StatsEvent>
        {
            new("aaaaaaaaaaa", At(Moscow, 2026, 3, 1, 2), 100_000),
            new("aaaaaaaaaaa", At(Moscow, 2026, 3, 1, 18), 40_000),
            new("aaaaaaaaaaa", At(Moscow, 2026, 3, 1, 20), 40_000),
            new("aaaaaaaaaaa", At(Moscow, 2026, 3, 1, 23), 40_000),
        };
        var stats = ListeningStats.Build(year, Moscow, events, new Dictionary<string, Track>(), new Dictionary<string, TrackOverride>(), new Dictionary<string, long>(), null);
        Assert.Equal(2, stats.BestHour);
        Assert.Equal(DayPart.Evening, stats.FavoriteDayPart);
        Assert.Null(ListeningStats.Build(year, Moscow, [], new Dictionary<string, Track>(), new Dictionary<string, TrackOverride>(), new Dictionary<string, long>(), null).FavoriteDayPart);
    }

    [Fact]
    public void FirstPlayAndHasPlays()
    {
        var library = new Library(new LibraryDatabase(Path.Combine(_directory, "library.db")));
        Assert.Null(library.FirstPlayAt());
        var july = At(Moscow, 2025, 7, 3);
        new SyncStore(library).Run(tx => tx.InsertPlay("e1", "aaaaaaaaaaa", july, 60_000, "phone"));
        Assert.Equal(july, library.FirstPlayAt());
        Assert.Null(library.FirstPlayAt(HistoryDevice.This("this")));
        Assert.True(library.HasPlays(new StatsPeriod(StatsPeriodKind.Year, new DateOnly(2025, 1, 1)), Moscow));
        Assert.False(library.HasPlays(new StatsPeriod(StatsPeriodKind.Year, new DateOnly(2026, 1, 1)), Moscow));
    }

    [Fact]
    public void FiftyThousandEventsAreFast()
    {
        var year = new StatsPeriod(StatsPeriodKind.Year, new DateOnly(2026, 1, 1));
        var random = new Random(7);
        var start = At(Moscow, 2026, 1, 1, 0);
        var events = Enumerable.Range(0, 50_000).Select(i => new StatsEvent($"v{random.Next(3000):0000000000}", start + random.NextInt64(360L * 86_400_000), 180_000)).ToList();
        var tracks = events.Select(e => e.VideoId).Distinct().ToDictionary(id => id, id => T(id, id, $"Artist {id[^2..]}", albumId: "MPREb_" + id[^1..], album: "A" + id[^1..]));
        var first = tracks.Keys.ToDictionary(id => id, _ => start);
        var watch = Stopwatch.StartNew();
        var stats = ListeningStats.Build(year, Moscow, events, tracks, new Dictionary<string, TrackOverride>(), first, null);
        watch.Stop();
        Assert.Equal(50_000, stats.Plays);
        Assert.True(watch.ElapsedMilliseconds < 300, $"{watch.ElapsedMilliseconds} ms");
    }
}
