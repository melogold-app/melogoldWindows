using Melogold.Core.Data;
using Melogold.Core.Lyrics;
using Xunit;

namespace Melogold.Tests;

/// <summary>tasks/0012: закреплённый текст — правила (как Linux <c>pins.rs</c> и Android <c>LyricsPins.kt</c>) и хранение.</summary>
public sealed class LyricsPinTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-pins-{Guid.NewGuid():N}");

    public LyricsPinTests() => Directory.CreateDirectory(_directory);

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

    private Library NewLibrary() => new(new LibraryDatabase(Path.Combine(_directory, $"library-{Guid.NewGuid():N}.db")));

    private static StoredLyrics Found(string source, string reference) => new(
        "[00:01.00]Строка", "Строка", source, LyricsSources.YouTubeMusic, SyncedRef: reference, PlainRef: "MPLYt_plain");

    [Fact]
    public void PinsTheSideOnScreen()
    {
        Assert.Equal(new LyricsPin("lrclib", "33476831", null), LyricsPins.PinOf(Found(LyricsSources.LrcLib, "33476831")));
        // Синхронного нет — обычный
        Assert.Equal(new LyricsPin("youtube_music", "MPLYt_plain", null), LyricsPins.PinOf(Found(LyricsSources.LrcLib, "1") with { Synced = "" }));
        // Сдвиг «позже» (наш OffsetMs отрицательный) — в startTimeMs; «раньше» — нет
        Assert.Equal(1500, LyricsPins.PinOf(Found(LyricsSources.KuGou, "12:ab") with { OffsetMs = -1500 })?.StartTimeMs);
        Assert.Null(LyricsPins.PinOf(Found(LyricsSources.KuGou, "12:ab") with { OffsetMs = 700 })?.StartTimeMs);
    }

    [Fact]
    public void OwnSharedAndUnreferencedAreNotPinned()
    {
        Assert.Null(LyricsPins.PinOf(Found(LyricsSources.LrcLib, "1") with { Chosen = true }));
        Assert.Null(LyricsPins.PinOf(Found(LyricsSources.User, "1")));
        Assert.Null(LyricsPins.PinOf(Found(LyricsSources.Melogold, "1")));
        Assert.Null(LyricsPins.PinOf(Found(LyricsSources.LrcLib, "1") with { SyncedRef = null }));
        Assert.Null(LyricsPins.PinOf(null));
    }

    [Fact]
    public void OrderOwnThenPinnedThenSearch()
    {
        var pin = new LyricsPin("lrclib", "7", 800);
        Assert.True(LyricsPins.NeedsPinned(null, pin));
        // Найденный здесь другой текст уступает закреплённому
        Assert.True(LyricsPins.NeedsPinned(Found(LyricsSources.LrcLib, "9"), pin));
        // Уже тот же — не доставать
        Assert.False(LyricsPins.NeedsPinned(Found(LyricsSources.LrcLib, "7"), pin));
        // Свой важнее закрепления
        Assert.False(LyricsPins.NeedsPinned(Found(LyricsSources.LrcLib, "9") with { Chosen = true }, pin));
        Assert.False(LyricsPins.NeedsPinned(null, null));

        var placed = LyricsPins.FromPinned(Found(LyricsSources.LrcLib, "7"), pin, new StoredLyrics(null, null, null, null, Language: "ru"));
        Assert.Equal(-800, placed.OffsetMs);
        Assert.Equal("ru", placed.Language);
        Assert.False(placed.Chosen);
    }

    [Fact]
    public void ShiftLaterMovesIntoThePin()
    {
        var pin = new LyricsPin("lrclib", "7", null);
        Assert.Equal(1200, LyricsPins.Shifted(pin, Found(LyricsSources.LrcLib, "7") with { OffsetMs = -1200 })?.StartTimeMs);
        // «Раньше» остаётся на устройстве, чужой текст закрепление не трогает
        Assert.Null(LyricsPins.Shifted(pin, Found(LyricsSources.LrcLib, "7") with { OffsetMs = 500 }));
        Assert.Null(LyricsPins.Shifted(pin, Found(LyricsSources.LrcLib, "8") with { OffsetMs = -1200 }));
    }

    [Fact]
    public void ServerValuesAreNormalized()
    {
        Assert.Null(LyricsPins.Of("genius", "1", null));
        Assert.Null(LyricsPins.Of("lrclib", "  ", null));
        Assert.Equal(new LyricsPin("lrclib", "5", null), LyricsPins.Of("lrclib", " 5 ", -3));
        Assert.Equal(200, LyricsPins.Of("kugou", new string('1', 300), 10)?.Ref.Length);
    }

    [Fact]
    public void FirstPinIsShared()
    {
        var library = NewLibrary();
        // Текста нет — закреплять нечего
        Assert.False(library.PinPlayedLyrics("aaaaaaaaaaa"));
        library.SaveLyrics("aaaaaaaaaaa", Found(LyricsSources.LrcLib, "7"));
        Assert.True(library.PinPlayedLyrics("aaaaaaaaaaa"));
        Assert.Equal(new LyricsPin("lrclib", "7", null), library.LyricsPinOf("aaaaaaaaaaa"));
        // Другой найденный текст первое закрепление не перезакрепляет
        library.SaveLyrics("aaaaaaaaaaa", Found(LyricsSources.LrcLib, "9"));
        Assert.False(library.PinPlayedLyrics("aaaaaaaaaaa"));
        Assert.Equal("7", library.LyricsPinOf("aaaaaaaaaaa")?.Ref);
        // Ссылка у текста сохраняется в базе
        Assert.Equal("9", library.GetLyrics("aaaaaaaaaaa")?.SyncedRef);
    }

    [Fact]
    public void LaterPinWinsOverTheServerRow()
    {
        var library = NewLibrary();
        library.SaveLyrics("aaaaaaaaaaa", Found(LyricsSources.LrcLib, "7"));
        library.PinPlayedLyrics("aaaaaaaaaaa");
        var local = new SyncStore(library).Run(tx => tx.LyricsPins()["aaaaaaaaaaa"].UpdatedAt);

        new SyncStore(library).Run(tx => tx.ApplyLyricsPin("aaaaaaaaaaa", new LyricsPin("kugou", "1:x", null), local - 1000));
        Assert.Equal("7", library.LyricsPinOf("aaaaaaaaaaa")?.Ref);
        Assert.Equal("1:x", new SyncStore(library).Run(tx => tx.SyncedLyricsPins()["aaaaaaaaaaa"].Ref));

        new SyncStore(library).Run(tx => tx.ApplyLyricsPin("aaaaaaaaaaa", new LyricsPin("kugou", "1:x", 300), local + 1000));
        Assert.Equal(new LyricsPin("kugou", "1:x", 300), library.LyricsPinOf("aaaaaaaaaaa"));
        new SyncStore(library).Run(tx => tx.ApplyLyricsPin("aaaaaaaaaaa", null, local + 2000));
        Assert.Null(library.LyricsPinOf("aaaaaaaaaaa"));
    }
}
