using System.Text.Json;
using Melogold.Core.Data;
using Melogold.Core.Lyrics;
using Melogold.Server;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Melogold.Tests;

/// <summary>Тексты через сервер (docs/LYRICS-SYNC.md §4): свой текст, разница со снимком, надгробия, разбор ответов, миграция базы.</summary>
public class LyricsSyncTests
{
    private const string Lrc = "[00:01.00]Hello\n[00:02.00]World\n";
    private const string Ttml = "<tt xmlns=\"http://www.w3.org/ns/ttml\" xml:lang=\"ru\"><body><div><p begin=\"1s\" end=\"2s\">Привет</p></div></body></tt>";

    private static StoredLyrics FileLrc(string text = Lrc, long offset = 0) => new(text, "", LyricsSources.File, null, offset);

    [Fact]
    public void OwnTextIsUserOrFileSideWithText()
    {
        Assert.True(LyricsSyncRules.IsOwn(FileLrc()));
        Assert.True(LyricsSyncRules.IsOwn(new StoredLyrics(Lrc, "Слова", LyricsSources.LrcLib, LyricsSources.User)));
        Assert.False(LyricsSyncRules.IsOwn(new StoredLyrics(Lrc, "Слова", LyricsSources.LrcLib, LyricsSources.YouTubeMusic)));
        Assert.False(LyricsSyncRules.IsOwn(new StoredLyrics(Lrc, "", LyricsSources.Melogold, null)));
        // Источник «свой», но стороны нет — не свой
        Assert.False(LyricsSyncRules.IsOwn(new StoredLyrics("", "", LyricsSources.File, LyricsSources.User)));
    }

    [Fact]
    public void ChosenTextIsOwnAndKeepsItsSource()
    {
        // Выбран в «Найти другой текст» — свой: уходит на сервер с источником lrclib (API §4.10)
        var chosen = new StoredLyrics(Lrc, "", LyricsSources.LrcLib, null, Chosen: true);
        Assert.True(LyricsSyncRules.IsOwn(chosen));
        Assert.Equal(LyricsSources.LrcLib, LyricsSyncRules.ToPayload(chosen).SyncedSource);
        // Тот же текст, найденный автоматически, — не свой: его найдёт и другое устройство
        Assert.False(LyricsSyncRules.IsOwn(chosen with { Chosen = false }));
        // Своя версия с сервера — своя и здесь
        Assert.True(LyricsSyncRules.FromPayload(LyricsSyncRules.ToPayload(chosen)).Chosen);

        var path = Path.Combine(Path.GetTempPath(), $"melogold-chosen-{Guid.NewGuid():N}.db");
        try
        {
            var library = new Library(new LibraryDatabase(path));
            library.SaveLyrics("aaaaaaaaaaa", chosen);
            library.SaveLyrics("bbbbbbbbbbb", chosen with { Chosen = false });
            Assert.True(library.GetLyrics("aaaaaaaaaaa")!.Chosen);
            Assert.Equal(["aaaaaaaaaaa"], new SyncStore(library).Run(tx => tx.OwnLyrics()).Keys);
            // «Очистить кэш» выбранный текст не трогает
            library.ClearFetchedLyrics();
            Assert.NotNull(library.GetLyrics("aaaaaaaaaaa"));
            Assert.Null(library.GetLyrics("bbbbbbbbbbb"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
        }
    }

    [Fact]
    public void PayloadSendsSidesWithTextFormatAndStart()
    {
        // Сдвиг «позже» — начало текста в треке
        var payload = LyricsSyncRules.ToPayload(new StoredLyrics(Lrc, "", LyricsSources.File, LyricsSources.YouTubeMusic, OffsetMs: -300));
        Assert.Equal(new LyricsPayload(null, null, Lrc, "lrc", LyricsSources.File, 300, null), payload);

        var ttml = LyricsSyncRules.ToPayload(new StoredLyrics(Ttml, "Слова", LyricsSources.File, LyricsSources.User, 0, "ru"));
        Assert.Equal(new LyricsPayload("Слова", LyricsSources.User, Ttml, "ttml", LyricsSources.File, null, "ru"), ttml);
    }

    /// <summary>Что не пройдёт проверку сервера, не отправляется: начало раньше нуля, источник <c>melogold</c>, длинный язык.</summary>
    [Fact]
    public void PayloadFitsServerRules()
    {
        var earlier = LyricsSyncRules.ToPayload(new StoredLyrics(Lrc, "Слова", LyricsSources.File, LyricsSources.Melogold, OffsetMs: 300, Language: new string('x', 36)));
        Assert.Equal(new LyricsPayload("Слова", null, Lrc, "lrc", LyricsSources.File, null, null), earlier);

        Assert.False(LyricsSyncRules.TooLarge(earlier));
        Assert.True(LyricsSyncRules.TooLarge(earlier with { Synced = new string('a', LyricsSyncRules.SyncedMax + 1) }));
        Assert.True(LyricsSyncRules.TooLarge(earlier with { Plain = new string('a', LyricsSyncRules.PlainMax + 1) }));
    }

    /// <summary>Эхо своей отправки не перезаписывает строку: сдвиг «раньше» и подпись общего текста остаются здесь.</summary>
    [Fact]
    public void EchoKeepsLocalDetails()
    {
        var local = new StoredLyrics(Lrc, "Слова", LyricsSources.File, LyricsSources.Melogold, OffsetMs: 300);
        var echo = LyricsSyncRules.FromPayload(LyricsSyncRules.ToPayload(local));
        Assert.True(LyricsSyncRules.SameContent(local, echo));
        Assert.False(LyricsSyncRules.SameContent(local with { Synced = Lrc + "[00:03.00]!\n" }, echo));
        Assert.False(LyricsSyncRules.SameContent(new StoredLyrics(Lrc, "Слова", LyricsSources.LrcLib, LyricsSources.LrcLib), echo));
        Assert.False(LyricsSyncRules.SameContent(null, echo));
    }

    [Fact]
    public void ServerVersionRoundTripsWithSameHash()
    {
        var payload = new LyricsPayload("Слова", LyricsSources.YouTubeMusic, Lrc, "lrc", LyricsSources.User, 1500, "ru");
        var stored = LyricsSyncRules.FromPayload(payload);
        Assert.Equal(-1500, stored.OffsetMs);
        Assert.Equal(payload, LyricsSyncRules.ToPayload(stored));
        Assert.Equal(LyricsSyncRules.Hash(payload), LyricsSyncRules.Hash(LyricsSyncRules.ToPayload(stored)));
        // Нет стороны на сервере — здесь «искали, нет», а не «ещё не искали»
        Assert.Equal("", LyricsSyncRules.FromPayload(payload with { Plain = null, PlainSource = null }).Plain);
    }

    [Fact]
    public void SendsNewChangedAndRemoved()
    {
        var unchanged = FileLrc();
        var changedBefore = FileLrc();
        var changedNow = FileLrc(offset: -100);
        var snapshot = new Dictionary<string, LyricsSnapshot>
        {
            ["aaaaaaaaaaa"] = new(3, LyricsSyncRules.Hash(LyricsSyncRules.ToPayload(unchanged))),
            ["bbbbbbbbbbb"] = new(4, LyricsSyncRules.Hash(LyricsSyncRules.ToPayload(changedBefore))),
            ["ccccccccccc"] = new(5, "removed-here"),
            ["ddddddddddd"] = new(LyricsSnapshot.Rejected, "too-big-and-removed"),
        };
        var own = new Dictionary<string, StoredLyrics>
        {
            ["aaaaaaaaaaa"] = unchanged,
            ["bbbbbbbbbbb"] = changedNow,
            ["eeeeeeeeeee"] = FileLrc(),
        };
        var sends = LyricsSyncRules.PlanSends(own, snapshot);
        Assert.Collection(sends,
            s => Assert.Equal("bbbbbbbbbbb", Assert.IsType<LyricsSend.Put>(s).VideoId),
            s => Assert.Equal("eeeeeeeeeee", Assert.IsType<LyricsSend.Put>(s).VideoId),
            s => Assert.Equal("ccccccccccc", Assert.IsType<LyricsSend.Delete>(s).VideoId),
            s => Assert.Equal("ddddddddddd", Assert.IsType<LyricsSend.Forget>(s).VideoId));
    }

    [Fact]
    public void RejectedTextIsNotSentAgainUntilChanged()
    {
        var text = FileLrc();
        var hash = LyricsSyncRules.Hash(LyricsSyncRules.ToPayload(text));
        var snapshot = new Dictionary<string, LyricsSnapshot> { ["aaaaaaaaaaa"] = new(LyricsSnapshot.Rejected, hash) };
        Assert.Empty(LyricsSyncRules.PlanSends(new Dictionary<string, StoredLyrics> { ["aaaaaaaaaaa"] = text }, snapshot));
        Assert.Single(LyricsSyncRules.PlanSends(new Dictionary<string, StoredLyrics> { ["aaaaaaaaaaa"] = FileLrc(offset: -100) }, snapshot));
    }

    [Fact]
    public void TombstoneDeletesOnlyUnchangedOwnText()
    {
        var text = FileLrc();
        var snapshot = new LyricsSnapshot(7, LyricsSyncRules.Hash(LyricsSyncRules.ToPayload(text)));
        Assert.True(LyricsSyncRules.DeleteOnTombstone(text, snapshot));
        Assert.False(LyricsSyncRules.DeleteOnTombstone(FileLrc(offset: -500), snapshot));
        Assert.False(LyricsSyncRules.DeleteOnTombstone(new StoredLyrics(Lrc, "", LyricsSources.LrcLib, null), snapshot));
        Assert.False(LyricsSyncRules.DeleteOnTombstone(text, null));
        Assert.False(LyricsSyncRules.DeleteOnTombstone(null, snapshot));
    }

    [Fact]
    public void ServerResponsesParse()
    {
        var page = JsonSerializer.Deserialize<MyLyricsPage>("""
            {"items":[
              {"id":"3f0c1d2e-4a5b-4c6d-8e7f-9a0b1c2d3e4f","videoId":"dQw4w9WgXcQ","rev":12,"deleted":false,"updatedAt":"2026-09-25T10:00:00.000Z",
               "text":{"plain":null,"plainSource":null,"synced":"[00:01.00]Hi","syncedFormat":"lrc","syncedSource":"user","startTimeMs":250,"language":"en"}},
              {"id":"7a21b3c4-d5e6-4f7a-8b9c-0d1e2f3a4b5c","videoId":"kJQP7kiw5Fk","rev":13,"deleted":true,"text":null,"updatedAt":"2026-09-25T10:00:01.000Z"}],
             "rev":13,"more":false}
            """, MelogoldApi.Json)!;
        Assert.Equal(13, page.Rev);
        Assert.False(page.More);
        Assert.Equal("user", page.Items[0].Text!.SyncedSource);
        Assert.Equal(250, page.Items[0].Text!.StartTimeMs);
        Assert.True(page.Items[1].Deleted);

        var response = JsonSerializer.Deserialize<LyricsResponse>("""
            {"mine":null,"shared":{"id":"b8e0d4c2-1a3b-4c5d-9e6f-7a8b9c0d1e2f","videoId":"dQw4w9WgXcQ","updatedAt":"2026-09-25T10:00:00.000Z",
             "text":{"plain":"Hi","plainSource":"file","synced":null,"syncedFormat":null,"syncedSource":null,"startTimeMs":null,"language":null}},
             "serverTime":"2026-09-25T10:00:02.000Z"}
            """, MelogoldApi.Json)!;
        Assert.Null(response.Mine);
        Assert.Equal("Hi", response.Shared!.Text.Plain);

        var put = JsonSerializer.Serialize(new LyricsPut { Synced = Lrc, SyncedFormat = "lrc", SyncedSource = "file" }, MelogoldApi.Json);
        Assert.DoesNotContain("plain", put);
        Assert.Contains("\"syncedFormat\":\"lrc\"", put);

        var features = JsonSerializer.Deserialize<ServerFeatures>("""{"lyrics":{"version":1}}""", MelogoldApi.Json)!;
        Assert.Equal(1, features.Lyrics!.Version);
    }

    /// <summary>База 0.1.0 (схема v2) открывается 0.1.1: источники переходят в словарь сервера, свои тексты находятся.</summary>
    [Fact]
    public void SchemaV2LyricsMigrate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"melogold-lyrics-{Guid.NewGuid():N}.db");
        try
        {
            _ = new LibraryDatabase(path);
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var command = c.CreateCommand();
                command.CommandText = """
                    DROP TABLE synced_lyrics;
                    DROP TABLE history_ops;
                    DROP TABLE downloads;
                    ALTER TABLE play_events DROP COLUMN device_id;
                    ALTER TABLE lyrics DROP COLUMN language;
                    ALTER TABLE lyrics DROP COLUMN chosen;
                    INSERT INTO lyrics (video_id, synced, plain, source, plain_source, offset_ms, fetched_at) VALUES
                      ('aaaaaaaaaaa', '[00:01.00]Hi', 'Hi', 'File', 'YouTubeMusic', 0, 0),
                      ('bbbbbbbbbbb', '[00:01.00]Yo', '', 'LrcLib', NULL, 0, 0);
                    PRAGMA user_version = 2;
                    """;
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var library = new Library(new LibraryDatabase(path));
            Assert.Equal(LyricsSources.File, library.GetLyrics("aaaaaaaaaaa")!.SyncedSource);
            Assert.Equal(LyricsSources.YouTubeMusic, library.GetLyrics("aaaaaaaaaaa")!.PlainSource);
            Assert.Equal(LyricsSources.LrcLib, library.GetLyrics("bbbbbbbbbbb")!.SyncedSource);
            var own = new SyncStore(library).Run(tx => tx.OwnLyrics());
            Assert.Equal(["aaaaaaaaaaa"], own.Keys);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
        }
    }
}
