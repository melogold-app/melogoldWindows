using Melogold.Core.Data;
using Melogold.Core.Music;
using Xunit;

namespace Melogold.Tests;

/// <summary>tasks/0011: своё название, исполнитель и альбом трека — показ, хранение, копия библиотеки.</summary>
public sealed class TrackOverrideTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"melogold-overrides-{Guid.NewGuid():N}");

    public TrackOverrideTests() => Directory.CreateDirectory(_directory);

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

    private static readonly Track FanUpload = new()
    {
        VideoId = "aaaaaaaaaaa",
        Title = "Artist — Song (live 2014, fan upload)",
        ArtistsText = "Some Channel",
        VideoType = "ugc",
    };

    [Fact]
    public void FieldsAreNormalizedLikeTheServer()
    {
        var value = TrackOverride.Of("  Песня  ", "", null);
        Assert.Equal("Песня", value.Title);
        Assert.Null(value.ArtistsText);
        Assert.Null(value.AlbumTitle);
        Assert.True(TrackOverride.Of(" ", "\t", null).IsEmpty);
        // 500 единиц UTF-16, суррогатная пара не рвётся
        var longTitle = new string('a', 499) + "😀";
        Assert.Equal(499, TrackOverride.Of(longTitle, null, null).Title!.Length);
    }

    [Fact]
    public void EmptyFieldShowsYouTube()
    {
        var shown = TrackOverride.Of("Песня", null, "Альбом").Apply(FanUpload);
        Assert.Equal("Песня", shown.Title);
        Assert.Equal("Some Channel", shown.ArtistsText);
        Assert.Equal("Альбом", shown.AlbumTitle);
        Assert.Same(FanUpload, TrackOverride.None.Apply(FanUpload));
    }

    [Fact]
    public void LibraryShowsTheOverrideAndKeepsTheOriginal()
    {
        var library = NewLibrary();
        var changes = new List<LibraryChange>();
        library.Changed += changes.Add;
        library.SetOverride(FanUpload, TrackOverride.Of("Песня", "Исполнитель", "Альбом"));
        Assert.Contains(LibraryChange.Overrides, changes);
        Assert.Equal("Песня", library.Display(FanUpload).Title);
        // В базе трек — как на YouTube: лайк и плейлисты берут исходный
        Assert.Equal(FanUpload.Title, library.GetTrack(FanUpload.VideoId)?.Title);

        // Поиск по библиотеке — и по своему, и по оригинальному названию
        library.SetLiked(FanUpload, true);
        Assert.Single(library.SearchLibrary("Альбом"));
        Assert.Single(library.SearchLibrary("fan upload"));

        // «Как на YouTube»: правка снята, строка остаётся со временем снятия (для встречи с правкой другого устройства)
        library.SetOverride(FanUpload, TrackOverride.None);
        Assert.Null(library.Override(FanUpload.VideoId));
        Assert.Equal(FanUpload.Title, library.Display(FanUpload).Title);
        Assert.Equal(1L, (long)library.Database.Read(c => LibraryDatabase.Scalar(c, "SELECT COUNT(*) FROM track_overrides"))!);
    }

    [Fact]
    public void LaterEditWinsOverTheServerRow()
    {
        var library = NewLibrary();
        library.SetOverride(FanUpload, TrackOverride.Of(null, null, "Здесь"));
        var local = new SyncStore(library).Run(tx => tx.TrackOverrides()[FanUpload.VideoId].UpdatedAt);

        // Как синк: транзакция, затем уведомление — кэш правок библиотеки перечитывается
        void Apply(TrackOverride value, long at)
        {
            new SyncStore(library).Run(tx => tx.ApplyOverride(FanUpload.VideoId, value, at));
            library.Notify(LibraryChange.Overrides);
        }

        // Строка сервера старше правки здесь: снимок обновляется, правка остаётся
        Apply(TrackOverride.Of(null, null, "С сервера"), local - 1000);
        Assert.Equal("Здесь", library.Override(FanUpload.VideoId)?.AlbumTitle);
        Assert.Equal("С сервера", new SyncStore(library).Run(tx => tx.SyncedOverrides()[FanUpload.VideoId].AlbumTitle));

        // Новее — побеждает; снятие с сервера снимает и здесь
        Apply(TrackOverride.Of(null, null, "С сервера"), local + 1000);
        Assert.Equal("С сервера", library.Override(FanUpload.VideoId)?.AlbumTitle);
        Apply(TrackOverride.None, local + 2000);
        Assert.Null(library.Override(FanUpload.VideoId));
        Assert.Empty(new SyncStore(library).Run(tx => tx.SyncedOverrides()));
    }

    [Fact]
    public void BackupCarriesOverrides()
    {
        var here = NewLibrary();
        here.SetLiked(FanUpload, true);
        here.SetOverride(FanUpload, TrackOverride.Of("Песня", null, "Альбом"));
        var other = FanUpload with { VideoId = "bbbbbbbbbbb" };
        here.SetOverride(other, TrackOverride.Of(null, null, "Снята"));
        here.SetOverride(other, TrackOverride.None);
        var copy = Path.Combine(_directory, "copy.backup");
        LibraryBackup.Export(here.Database, copy, "0.1.12");

        var there = NewLibrary();
        LibraryImport.Import(there, copy);
        Assert.Equal(TrackOverride.Of("Песня", null, "Альбом"), there.Override(FanUpload.VideoId));
        // Снятая правка не переносится
        Assert.Null(there.Override(other.VideoId));

        // Правка здесь новее — копия её не перетирает
        var newer = NewLibrary();
        LibraryImport.Import(newer, copy);
        newer.SetOverride(FanUpload, TrackOverride.Of(null, null, "Новее"));
        LibraryImport.Import(newer, copy);
        Assert.Equal("Новее", newer.Override(FanUpload.VideoId)?.AlbumTitle);
    }
}
