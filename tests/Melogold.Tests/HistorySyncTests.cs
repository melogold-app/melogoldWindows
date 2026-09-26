using System.Text.Json;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Melogold.Server;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Melogold.Tests;

/// <summary>Общая история (tasks/0002 §4): разбор ответа, идемпотентность по eventId, забывание, фильтр по устройствам, смена аккаунта.</summary>
public sealed class HistorySyncTests : IDisposable
{
    private const string Phone = "9b1e2f4a-7c3d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string Pc = "11111111-2222-4333-8444-555555555555";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"melogold-history-{Guid.NewGuid():N}.db");
    private readonly Library _library;
    private readonly SyncStore _store;

    public HistorySyncTests()
    {
        _library = new Library(new LibraryDatabase(_path));
        _store = new SyncStore(_library);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" }) File.Delete(file);
    }

    private static Track T(string id, string title) => new() { VideoId = id, Title = title, ArtistsText = "Melogold test" };

    /// <summary>Ответ сервера из примера API §4.8: прослушивание с телефона, общее время, забытое.</summary>
    private static SyncResponse Response(string plays, string stats = "", string forgets = "") => JsonSerializer.Deserialize<SyncResponse>($$"""
        {"results":[],"cursor":"a.1.1","hasMore":false,"serverTime":"2026-09-25T10:00:00.000Z",
         "tracks":[{"videoId":"a1B2c3D4e5F","title":"Song","artistsText":"Artist","artists":[],"albumId":null,"albumTitle":null,"durationMs":254000,"durationText":"4:14","thumbnailUrl":null,"explicit":false,"videoType":"ugc","metadataStub":false}],
         "playlists":[],"items":[],"likes":[],"bookmarks":[],
         "plays":[{{plays}}],"playStats":[{{stats}}],"playForgets":[{{forgets}}]}
        """, MelogoldApi.Json)!;

    private void Apply(SyncResponse response) => _library.Notify(_store.Run(tx =>
    {
        foreach (var track in response.Tracks) tx.EnsureTrack(track.VideoId, new Track { VideoId = track.VideoId, Title = track.Title!, ArtistsText = track.ArtistsText });
        LibrarySync.ApplyHistoryRows(tx, response);
        return tx.Changes;
    }));

    [Fact]
    public void PlaysFromTheServerAreInsertedOnce()
    {
        var play = $$"""{"eventId":"b71e2c3d-4e5f-4a6b-9c7d-8e9f0a1b2c3d","videoId":"a1B2c3D4e5F","playedAt":"2026-09-23T09:58:10.000Z","playTimeMs":212000,"deviceId":"{{Phone}}"}""";
        var stats = """{"videoId":"a1B2c3D4e5F","totalPlayTimeMs":1484000,"lastPlayedAt":"2026-09-23T09:58:10.000Z"}""";
        Apply(Response(play, stats));
        Apply(Response(play, stats));

        Assert.Equal(1, _library.PlayCount());
        Assert.Equal(["a1B2c3D4e5F"], _library.RecentHistory().Select(h => h.Track.VideoId));
        Assert.Equal("Song", _library.RecentHistory()[0].Track.Title);
        // Общее время — значение сервера по всем устройствам
        Assert.Equal(1_484_000, _library.MostPlayed(null)[0].PlayTimeMs);
        Assert.Equal([Phone], _library.HistoryDeviceIds());
        // С сервера — уже отправленное: здесь его никто не пошлёт снова
        Assert.Empty(_store.Run(tx => tx.UnsentPlays()));
    }

    [Fact]
    public void ForgetWithTotalBeforeResetsTheTotalUnlessFreshStatsCame()
    {
        var play = $$"""{"eventId":"b71e2c3d-4e5f-4a6b-9c7d-8e9f0a1b2c3d","videoId":"a1B2c3D4e5F","playedAt":"2026-09-23T09:58:10.000Z","playTimeMs":212000,"deviceId":"{{Phone}}"}""";
        Apply(Response(play, """{"videoId":"a1B2c3D4e5F","totalPlayTimeMs":1484000,"lastPlayedAt":"2026-09-23T09:58:10.000Z"}"""));
        Assert.Equal(1_484_000, _library.MostPlayed(null)[0].PlayTimeMs);

        // Трек убрали на Android с resetTotal: true — пропадает и из «Чаще всего» (tasks/0008)
        var forget = """{"videoId":"a1B2c3D4e5F","eventsBefore":"2026-09-24T10:00:00.000Z","totalBefore":"2026-09-24T10:00:00.000Z"}""";
        Apply(Response("", "", forget));
        Assert.Equal(0, _library.PlayCount());
        Assert.Empty(_library.MostPlayed(null));

        // Тот же забытый трек, но в ответе уже его новое общее время (слушали после) — оно и остаётся
        Apply(Response("", """{"videoId":"a1B2c3D4e5F","totalPlayTimeMs":90000,"lastPlayedAt":"2026-09-25T09:00:00.000Z"}""", forget));
        Assert.Equal(90_000, _library.MostPlayed(null).Single().PlayTimeMs);
    }

    [Fact]
    public void OwnPlayComingBackIsNotDoubled()
    {
        _library.RecordPlay(T("dQw4w9WgXcQ", "Never Gonna Give You Up"), 60_000, 1_790_000_000_000);
        var own = _store.Run(tx => tx.UnsentPlays()).Single();
        Apply(Response($$"""{"eventId":"{{own.EventId}}","videoId":"dQw4w9WgXcQ","playedAt":"2026-09-23T09:58:10.000Z","playTimeMs":60000,"deviceId":"{{Pc}}"}"""));
        Assert.Equal(1, _library.PlayCount());
        // Своё осталось «этого устройства»: device_id не подменился чужим
        Assert.Empty(_library.HistoryDeviceIds());
    }

    [Fact]
    public void ForgetsRemoveEventsUpToTheirTime()
    {
        Apply(Response(string.Join(',',
            $$"""{"eventId":"00000000-0000-4000-8000-000000000001","videoId":"a1B2c3D4e5F","playedAt":"2026-09-23T09:00:00.000Z","playTimeMs":60000,"deviceId":"{{Phone}}"}""",
            $$"""{"eventId":"00000000-0000-4000-8000-000000000002","videoId":"a1B2c3D4e5F","playedAt":"2026-09-23T11:00:00.000Z","playTimeMs":60000,"deviceId":"{{Phone}}"}""",
            $$"""{"eventId":"00000000-0000-4000-8000-000000000003","videoId":"kJQP7kiw5Fk","playedAt":"2026-09-23T09:30:00.000Z","playTimeMs":60000,"deviceId":"{{Phone}}"}""")));
        Assert.Equal(3, _library.PlayCount());

        // Трек до 10:00 включительно: позднее событие остаётся
        Apply(Response("", forgets: """{"videoId":"a1B2c3D4e5F","eventsBefore":"2026-09-23T10:00:00.000Z","totalBefore":null}"""));
        Assert.Equal(2, _library.PlayCount());
        // «*» — все события до времени
        Apply(Response("", forgets: """{"videoId":"*","eventsBefore":"2026-09-23T12:00:00.000Z","totalBefore":null}"""));
        Assert.Equal(0, _library.PlayCount());
    }

    [Fact]
    public void FilterShowsPlaysOfTheChosenDevice()
    {
        _library.RecordPlay(T("dQw4w9WgXcQ", "Here"), 120_000, 1_790_000_000_000);
        Apply(Response(string.Join(',',
            $$"""{"eventId":"00000000-0000-4000-8000-000000000001","videoId":"a1B2c3D4e5F","playedAt":"2026-09-23T09:00:00.000Z","playTimeMs":300000,"deviceId":"{{Phone}}"}""",
            // Этого устройства, но пришло с сервера (например, после переустановки)
            $$"""{"eventId":"00000000-0000-4000-8000-000000000002","videoId":"kJQP7kiw5Fk","playedAt":"2026-09-23T09:30:00.000Z","playTimeMs":60000,"deviceId":"{{Pc}}"}""")));

        Assert.Equal(3, _library.RecentHistory().Count);
        Assert.Equal(["dQw4w9WgXcQ", "kJQP7kiw5Fk"], _library.RecentHistory(device: HistoryDevice.This(Pc)).Select(h => h.Track.VideoId).Order());
        Assert.Equal(["a1B2c3D4e5F"], _library.RecentHistory(device: HistoryDevice.Other(Phone)).Select(h => h.Track.VideoId));
        // «Чаще всего» за всё время у устройства — сумма его событий
        Assert.Equal(300_000, _library.MostPlayed(null, device: HistoryDevice.Other(Phone)).Single().PlayTimeMs);
    }

    [Fact]
    public void RemoveResetsTheTotalAndClearKeepsIt()
    {
        _library.RecordPlay(T("dQw4w9WgXcQ", "Here"), 120_000, IsoTimeNow() - 1000);
        _library.RecordPlay(T("kJQP7kiw5Fk", "Stays"), 60_000, IsoTimeNow() - 1000);
        _library.RemoveFromHistory("dQw4w9WgXcQ");
        Assert.Equal(1, _library.PlayCount());
        // resetTotal: true — трек пропадает и из «Чаще всего», как на Android и Apple (tasks/0008)
        Assert.Equal(["kJQP7kiw5Fk"], _library.MostPlayed(null).Select(m => m.Track.VideoId));
        _library.ClearHistory();
        // «Очистить историю» общее время не трогает
        Assert.Equal(60_000, _library.MostPlayed(null).Single().PlayTimeMs);
        var ops = _store.Run(tx => tx.HistoryOps());
        Assert.Equal(["history.forget", "history.clear"], ops.Select(o => o.Kind));
        Assert.Equal("dQw4w9WgXcQ", ops[0].VideoId);
    }

    [Fact]
    public void AnotherAccountTakesOwnPlaysAndDropsForeignOnes()
    {
        _library.RecordPlay(T("dQw4w9WgXcQ", "Here"), 120_000, 1_790_000_000_000);
        var own = _store.Run(tx => tx.UnsentPlays()).Single();
        _store.Run(tx => tx.MarkPlaySent(own.EventId));
        Apply(Response($$"""{"eventId":"00000000-0000-4000-8000-000000000001","videoId":"a1B2c3D4e5F","playedAt":"2026-09-23T09:00:00.000Z","playTimeMs":300000,"deviceId":"{{Phone}}"}"""));
        _library.RemoveFromHistory("zzzzzzzzzzz");

        _store.Run(tx => tx.ForgetBinding());
        Assert.Equal([own.EventId], _store.Run(tx => tx.UnsentPlays()).Select(p => p.EventId));
        Assert.Equal(1, _library.PlayCount());
        Assert.Empty(_store.Run(tx => tx.HistoryOps()));
    }

    private static long IsoTimeNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
