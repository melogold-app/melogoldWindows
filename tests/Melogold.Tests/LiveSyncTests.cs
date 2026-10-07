using System.Diagnostics;
using System.Security.Cryptography;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.Server;
using Xunit;

namespace Melogold.Tests;

/// <summary>
/// Приёмка синхронизации (docs/PROMPT.md §6, срез 5) на живом сервере, как <c>scripts/live-check.py</c> сервера: два
/// устройства одного временного аккаунта, правки в обе стороны по SSE за секунды, одновременные правки одного плейлиста
/// без потерь; аккаунт удаляется в конце. Только с <c>MELOGOLD_LIVE=1</c>; адрес — <c>MELOGOLD_SERVER</c> или сервер по
/// умолчанию.
/// </summary>
public class LiveSyncTests(ITestOutputHelper output)
{
    private sealed class MemorySessions : ISessionStore
    {
        private StoredSession? _session;

        public StoredSession? Load() => _session;

        public void Save(StoredSession? session) => _session = session;
    }

    private sealed class TestIdentity(string name) : IDeviceIdentity
    {
        public string PlatformId { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        public string DeviceName => name;
        public string? OsVersion => "Windows 11 (test)";
        public string? Model => null;
        public string ClientVersion => "0.1.0-test";
    }

    /// <summary>Устройство: своя база в файле, свой аккаунт и синхронизация.</summary>
    private sealed class Device : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"melogold-sync-{Guid.NewGuid():N}.db");

        public Device(string name, string server, ITestOutputHelper output)
        {
            Library = new Library(new LibraryDatabase(_path));
            Account = new AccountService(new MemorySessions(), new TestIdentity(name), server);
            Name = name;
            Output = output;
            Sync = NewSync();
        }

        public string Name { get; }
        public ITestOutputHelper Output { get; }
        public Library Library { get; }
        public AccountService Account { get; }
        public LibrarySync Sync { get; private set; }

        public LibrarySync NewSync() => new(Account, new SyncStore(Library), (message, error) => Output.WriteLine($"[{Name}] {message} {error?.Message}"));

        /// <summary>«Уйти в офлайн»: синхронизация этого устройства останавливается, правки копятся.</summary>
        public void Pause()
        {
            Sync.Dispose();
            Sync = NewSync();
        }

        public void Dispose()
        {
            Sync.Dispose();
            Account.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static Track T(string id, string title) => new() { VideoId = id, Title = title, ArtistsText = "Melogold test" };

    private static readonly Track A1 = T("dQw4w9WgXcQ", "Never Gonna Give You Up");
    private static readonly Track A2 = T("kJQP7kiw5Fk", "Despacito");
    private static readonly Track A3 = T("9bZkp7q19f0", "Gangnam Style");
    private static readonly Track A4 = T("fJ9rUzIMcZQ", "Bohemian Rhapsody");
    private static readonly Track A5 = T("hTWKbfoikeg", "Smells Like Teen Spirit");
    private static readonly Track A6 = T("YR5ApYxkU-U", "Test six");

    private async Task WaitFor(string what, Func<bool> condition, int seconds = 25)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) Assert.Fail($"Не дождались: {what}");
            await Task.Delay(200);
        }
        output.WriteLine($"✓ {what} — {watch.Elapsed.TotalSeconds:F1} с");
    }

    private static List<string> Order(Library library, string name) =>
        library.Playlists().FirstOrDefault(p => p.Name == name) is { } p ? library.PlaylistTracks(p.Id).Select(t => t.VideoId).ToList() : [];

    [Fact]
    public async Task TwoDevicesStayTheSame()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var server = Environment.GetEnvironmentVariable("MELOGOLD_SERVER") ?? AccountService.DefaultServerUrl;
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var password = "проверка связи " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

        using var a = new Device("E2E Windows A", server, output);
        using var b = new Device("E2E Windows B", server, output);
        try
        {
            // Библиотека A до входа: всё уходит на сервер при первой синхронизации
            a.Library.SetLiked(A1, true);
            a.Library.SetLiked(A2, true);
            a.Library.CreatePlaylist("Дорога", [A1, A2, A3]);
            a.Library.SetAlbumSaved(new AlbumItem { BrowseId = "MPREb_test_album", Title = "Test album", ArtistsText = "Melogold test" }, true);
            await a.Account.RegisterAsync(login, password);
            a.Sync.Start();
            await WaitFor("A отправил библиотеку", () => a.Sync.Status is SyncStatus.Idle { LastSyncAt: not null });

            await b.Account.SignInAsync(login, password);
            b.Sync.Start();
            await WaitFor("B получил лайки, плейлист и альбом", () =>
                b.Library.LikedIds().SetEquals([A1.VideoId, A2.VideoId]) && Order(b.Library, "Дорога").SequenceEqual([A1.VideoId, A2.VideoId, A3.VideoId]) &&
                b.Library.SavedAlbums().Count == 1);
            Assert.Equal("Never Gonna Give You Up", b.Library.GetTrack(A1.VideoId)?.Title);

            // Правки B приходят на A по SSE за секунды
            var road = b.Library.Playlists().Single(p => p.Name == "Дорога").Id;
            b.Library.MoveInPlaylist(road, A3.VideoId, 0);
            b.Library.RemoveFromPlaylist(road, A2.VideoId);
            b.Library.SetLiked(A4, true);
            b.Library.SetLiked(A1, false);
            await WaitFor("A видит перестановку, удаление и лайки B", () =>
                Order(a.Library, "Дорога").SequenceEqual([A3.VideoId, A1.VideoId]) && a.Library.LikedIds().SetEquals([A2.VideoId, A4.VideoId]));

            // И обратно: новый плейлист, переименование, снятая закладка
            var aRoad = a.Library.Playlists().Single(p => p.Name == "Дорога").Id;
            a.Library.RenamePlaylist(aRoad, "Дорога домой");
            a.Library.CreatePlaylist("Вечер", [A5]);
            a.Library.SetAlbumSaved(new AlbumItem { BrowseId = "MPREb_test_album", Title = "Test album" }, false);
            await WaitFor("B видит переименование, новый плейлист и снятую закладку", () =>
                Order(b.Library, "Дорога домой").Count == 2 && Order(b.Library, "Вечер").SequenceEqual([A5.VideoId]) && b.Library.SavedAlbums().Count == 0);

            // Офлайн-правка на A и одновременная правка того же плейлиста на B сливаются без потерь
            a.Pause();
            aRoad = a.Library.Playlists().Single(p => p.Name == "Дорога домой").Id;
            a.Library.AddToPlaylist(aRoad, [A5]);
            var bRoad = b.Library.Playlists().Single(p => p.Name == "Дорога домой").Id;
            b.Library.AddToPlaylist(bRoad, [A6]);
            // B отправляет свою правку через 2 с; A всё это время вне сети
            await Task.Delay(5000);
            a.Sync.Start();
            await WaitFor("оба устройства сошлись на 4 треках", () =>
            {
                var onA = Order(a.Library, "Дорога домой");
                var onB = Order(b.Library, "Дорога домой");
                return onA.Count == 4 && onA.SequenceEqual(onB) && onA.Contains(A5.VideoId) && onA.Contains(A6.VideoId);
            });
            output.WriteLine("Порядок: " + string.Join(", ", Order(a.Library, "Дорога домой")));

            // Удалённый плейлист исчезает и на другом устройстве
            var evening = b.Library.Playlists().Single(p => p.Name == "Вечер").Id;
            b.Library.DeletePlaylist(evening);
            await WaitFor("A видит удаление плейлиста", () => a.Library.Playlists().All(p => p.Name != "Вечер"));

            // «Выйти на других устройствах» с A: B получает session.invalidated
            var devices = await a.Account.DevicesAsync();
            Assert.Equal(2, devices.Devices.Count);
            await a.Account.RevokeOthersAsync(password);
            await WaitFor("B вышел по session.invalidated", () => b.Account.State is AccountState.AuthRequired);
        }
        finally
        {
            if (a.Account.Session is not null) await a.Account.DeleteAccountAsync(password);
            output.WriteLine($"Аккаунт {login} удалён");
        }
    }

    /// <summary>
    /// Вход по коду (tasks/0006 §2, API §4.6 <c>request</c>): «часы» просят вход и показывают код; Windows вводит его,
    /// видит часы и три числа, выбирает число с часов — часы получают сессию и появляются в списке устройств. Неверное
    /// число — вход отклонён.
    /// </summary>
    [Fact]
    public async Task WatchSignsInWithCode()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var server = Environment.GetEnvironmentVariable("MELOGOLD_SERVER") ?? AccountService.DefaultServerUrl;
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var password = "проверка связи " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        using var a = new Device("E2E Windows A", server, output);
        using var http = new HttpClient { BaseAddress = new Uri(server) };

        async Task<System.Text.Json.Nodes.JsonObject> Post(string path, object body)
        {
            using var response = await http.PostAsync(path, new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"));
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
            return (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(text)!;
        }

        // Часы просят вход: код и секрет опроса
        async Task<(string UserCode, string PollSecret)> WatchAsks() =>
            await Post("/auth/link/requests", new
            {
                device = new { hwid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), name = "Apple Watch", platform = "watchos", osVersion = "11.0", model = "Watch7,1" },
            }) is var created ? ((string)created["userCode"]!, (string)created["pollSecret"]!) : default;

        try
        {
            await a.Account.RegisterAsync(login, password);

            var (code, secret) = await WatchAsks();
            // Код вводят как удобно: строчными, с пробелом
            var link = await a.Account.ResolveLinkAsync(UserCode.Normalize(code.ToLowerInvariant().Replace("-", " "))!);
            Assert.Equal("claimed", link.Status);
            Assert.Equal("watchos", link.Device?.Platform);
            Assert.Equal(DeviceKind.Watch, DeviceSymbols.Kind(link.Device?.Platform));
            Assert.Equal(3, link.VerifyChoices.Count);
            var shown = (string)(await Post("/auth/link/poll", new { pollSecret = secret, knownStatus = "pending", waitSeconds = 0 }))["verifyCode"]!;
            Assert.Contains(shown, link.VerifyChoices);

            Assert.Equal("approved", (await a.Account.ApproveLinkAsync(link.LinkId, shown)).Status);
            var done = await Post("/auth/link/poll", new { pollSecret = secret, knownStatus = "claimed", waitSeconds = 0 });
            Assert.Equal("completed", (string)done["status"]!);
            Assert.NotNull(done["session"]);
            Assert.Contains((await a.Account.DevicesAsync()).Devices, d => d.Platform == "watchos" && d.Name == "Apple Watch");
            output.WriteLine("✓ часы вошли по коду");

            // Неверное число — отказ
            var (code2, _) = await WatchAsks();
            var link2 = await a.Account.ResolveLinkAsync(UserCode.Normalize(code2)!);
            var wrong = Enumerable.Range(10, 90).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).First(n => !link2.VerifyChoices.Contains(n));
            var error = await Assert.ThrowsAsync<ApiException>(() => a.Account.ApproveLinkAsync(link2.LinkId, wrong));
            Assert.Equal("link_verify_mismatch", error.Code);
            output.WriteLine("✓ неверное число — вход отклонён");

            // Несуществующий код
            Assert.Equal("link_not_found", (await Assert.ThrowsAsync<ApiException>(() => a.Account.ResolveLinkAsync("ZZZZ-ZZZZ"))).Code);
        }
        finally
        {
            if (a.Account.Session is not null) await a.Account.DeleteAccountAsync(password);
            output.WriteLine($"Аккаунт {login} удалён");
        }
    }

    /// <summary>
    /// Для скриншота списка устройств (tasks/0006 §3): временный аккаунт с iPhone, iPad, Mac, Vision Pro и часами.
    /// Логин и пароль — в файл <c>MELOGOLD_SCREENSHOT_ACCOUNT</c>; второй запуск с тем же файлом удаляет аккаунт.
    /// </summary>
    [Fact]
    public async Task AppleDevicesAccountForScreenshot()
    {
        var file = Environment.GetEnvironmentVariable("MELOGOLD_SCREENSHOT_ACCOUNT");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1" && !string.IsNullOrEmpty(file), "MELOGOLD_LIVE=1 и MELOGOLD_SCREENSHOT_ACCOUNT");
        var server = Environment.GetEnvironmentVariable("MELOGOLD_SERVER") ?? AccountService.DefaultServerUrl;
        using var a = new Device("E2E Windows", server, output);
        if (File.Exists(file))
        {
            var saved = File.ReadAllLines(file!);
            await a.Account.SignInAsync(saved[0], saved[1]);
            await a.Account.DeleteAccountAsync(saved[1]);
            File.Delete(file!);
            output.WriteLine($"Аккаунт {saved[0]} удалён");
            return;
        }
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        // Латиница: пароль вводит в окно tools/shot.ps1, а аргументы Windows PowerShell портят кириллицу
        var password = "check-devices-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        await a.Account.RegisterAsync(login, password);
        using var http = new HttpClient { BaseAddress = new Uri(server) };
        foreach (var (name, platform, model) in new[] { ("iPhone", "ios", "iPhone17,1"), ("iPad", "ipados", "iPad16,3"), ("MacBook Air", "macos", "Mac15,12"), ("Apple Vision Pro", "visionos", "RealityDevice14,1"), ("Apple Watch", "watchos", "Watch7,1") })
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new { login, password, device = new { hwid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), name, platform, model } });
            using var response = await http.PostAsync("/auth/login", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
        File.WriteAllLines(file!, [login, password]);
        output.WriteLine($"Аккаунт {login} с устройствами Apple готов");
    }

    /// <summary>
    /// Общая история (tasks/0002 §4): прослушивание на A — в Истории B с устройством A; накопленное до входа время —
    /// через <c>play.baseline</c>; «Убрать из истории» и «Очистить историю» — на обоих; «Это устройство» — только свои.
    /// </summary>
    [Fact]
    public async Task HistoryIsSharedBetweenDevices()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var server = Environment.GetEnvironmentVariable("MELOGOLD_SERVER") ?? AccountService.DefaultServerUrl;
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var password = "проверка связи " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

        using var a = new Device("E2E Windows A", server, output);
        using var b = new Device("E2E Windows B", server, output);
        try
        {
            // До входа: прослушивание на A — уйдёт play.add, его время — ещё и baseline
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            a.Library.RecordPlay(A1, 90_000, now - 60_000);
            await a.Account.RegisterAsync(login, password);
            a.Sync.Start();
            await WaitFor("A отправил историю", () => a.Sync.Status is SyncStatus.Idle { LastSyncAt: not null } && a.Library.PlayCount() == 1);

            await b.Account.SignInAsync(login, password);
            b.Sync.Start();
            var aDevice = ((AccountState.SignedIn)a.Account.State).DeviceId;
            var bDevice = ((AccountState.SignedIn)b.Account.State).DeviceId;
            await WaitFor("B видит прослушивание A с устройством A", () =>
                b.Library.RecentHistory().Select(h => h.Track.VideoId).SequenceEqual([A1.VideoId]) && b.Library.HistoryDeviceIds().SequenceEqual([aDevice]));
            Assert.Equal(90_000, b.Library.MostPlayed(null).Single().PlayTimeMs);

            // Прослушивание на B — на A по SSE; «Это устройство» на B — только своё
            b.Library.RecordPlay(A2, 45_000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await WaitFor("A видит прослушивание B", () => a.Library.RecentHistory().Count == 2 && a.Library.HistoryDeviceIds().SequenceEqual([bDevice]));
            Assert.Equal([A2.VideoId], b.Library.RecentHistory(device: HistoryDevice.This(bDevice)).Select(h => h.Track.VideoId));
            Assert.Equal([A1.VideoId], b.Library.RecentHistory(device: HistoryDevice.Other(aDevice)).Select(h => h.Track.VideoId));

            // «Убрать из истории» на B — на A тоже, и из «Чаще всего · Всё время» (resetTotal: true, tasks/0008)
            b.Library.RemoveFromHistory(A1.VideoId);
            Assert.DoesNotContain(A1.VideoId, b.Library.MostPlayed(null).Select(m => m.Track.VideoId));
            await WaitFor("A убрал трек из истории и из «Чаще всего»", () =>
                a.Library.RecentHistory().Select(h => h.Track.VideoId).SequenceEqual([A2.VideoId])
                && !a.Library.MostPlayed(null).Any(m => m.Track.VideoId == A1.VideoId));

            // «Очистить историю» на A — на B тоже
            a.Library.ClearHistory();
            await WaitFor("B очистил историю", () => b.Library.PlayCount() == 0);
            Assert.Empty(a.Library.RecentHistory());
        }
        finally
        {
            if (a.Account.Session is not null) await a.Account.DeleteAccountAsync(password);
            output.WriteLine($"Аккаунт {login} удалён");
        }
    }

    /// <summary>
    /// Тексты через сервер (docs/LYRICS-SYNC.md §4): свой текст с A появляется на B за секунды и обратно, удаление на
    /// одном устройстве убирает его на другом, второй аккаунт видит его общим. Трек — случайный id, чтобы общий текст был
    /// именно наш. Оба аккаунта удаляются в конце.
    /// </summary>
    [Fact]
    public async Task OwnLyricsReachOtherDevices()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var server = Environment.GetEnvironmentVariable("MELOGOLD_SERVER") ?? AccountService.DefaultServerUrl;
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var other = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var password = "проверка связи " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var video = "e2e" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        const string Lrc = "[00:01.00]Первая строка\n[00:04.50]Вторая строка\n";

        using var a = new Device("E2E Windows A", server, output);
        using var b = new Device("E2E Windows B", server, output);
        using var c = new Device("E2E Windows C", server, output);
        try
        {
            await a.Account.RegisterAsync(login, password);
            a.Sync.Start();
            await b.Account.SignInAsync(login, password);
            b.Sync.Start();
            await WaitFor("оба устройства синхронизировались", () =>
                a.Sync.Status is SyncStatus.Idle { LastSyncAt: not null } && b.Sync.Status is SyncStatus.Idle { LastSyncAt: not null });

            // Импорт .lrc на A — свой текст, уходит через 2 с и приходит на B по lyrics.changed
            a.Library.SaveLyrics(video, new StoredLyrics(Lrc, "", LyricsSources.File, null, 0, "ru"));
            await WaitFor("B получил текст A", () => b.Library.GetLyrics(video) is { Synced: Lrc, SyncedSource: LyricsSources.File, Language: "ru" });

            // Сдвиг «позже» на B виден на A
            b.Library.SaveLyrics(video, b.Library.GetLyrics(video)! with { OffsetMs = -1500 });
            await WaitFor("A получил сдвиг B", () => a.Library.GetLyrics(video)?.OffsetMs == -1500);

            // Другой аккаунт видит его общим, с подписью сообщества
            await c.Account.RegisterAsync(other, password);
            var shared = await c.Sync.LookupLyricsAsync(video, CancellationToken.None);
            Assert.NotNull(shared);
            Assert.False(shared.Value.Mine);
            Assert.Equal(Lrc, shared.Value.Payload.Synced);
            Assert.Equal(1500, shared.Value.Payload.StartTimeMs);
            output.WriteLine("✓ C видит общий текст");

            // B заменил свой текст найденным в LRCLIB: свой исчез — на сервере надгробие, и A его удаляет
            b.Library.SaveLyrics(video, new StoredLyrics("[00:02.00]Из LRCLIB\n", "", LyricsSources.LrcLib, null));
            await WaitFor("A удалил текст по надгробию", () => a.Library.GetLyrics(video) is null);
            Assert.Null(await c.Sync.LookupLyricsAsync(video, CancellationToken.None));
            Assert.Equal(LyricsSources.LrcLib, b.Library.GetLyrics(video)?.SyncedSource);

            // Текст, выбранный в «Найти другой текст», — свой: приходит на A с источником lrclib, искать его там не нужно
            const string Chosen = "[00:03.00]Выбран в LRCLIB\n";
            b.Library.SaveLyrics(video, new StoredLyrics(Chosen, "", LyricsSources.LrcLib, null, Chosen: true));
            await WaitFor("A получил выбранный на B текст", () => a.Library.GetLyrics(video) is { Synced: Chosen, SyncedSource: LyricsSources.LrcLib, Chosen: true });
        }
        finally
        {
            if (a.Account.Session is not null) await a.Account.DeleteAccountAsync(password);
            if (c.Account.Session is not null) await c.Account.DeleteAccountAsync(password);
            output.WriteLine($"Аккаунты {login} и {other} удалены");
        }
    }
    /// <summary>
    /// tasks/0011: своё название, исполнитель и альбом с одного устройства — на другом; лайк с метаданными YouTube правку не
    /// сбрасывает; «Как на YouTube» снимает её везде. Аккаунт удаляется в конце.
    /// </summary>
    [Fact]
    public async Task OverridesReachOtherDevices()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var server = Environment.GetEnvironmentVariable("MELOGOLD_SERVER") ?? AccountService.DefaultServerUrl;
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var password = "проверка связи " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var track = new Track
        {
            VideoId = "e2e" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)),
            Title = "Artist — Song (live 2014, fan upload)",
            ArtistsText = "Some Channel",
            VideoType = "ugc",
        };

        using var a = new Device("E2E Windows A", server, output);
        using var b = new Device("E2E Windows B", server, output);
        try
        {
            await a.Account.RegisterAsync(login, password);
            a.Sync.Start();
            await b.Account.SignInAsync(login, password);
            b.Sync.Start();
            await WaitFor("оба устройства синхронизировались", () =>
                a.Sync.Status is SyncStatus.Idle { LastSyncAt: not null } && b.Sync.Status is SyncStatus.Idle { LastSyncAt: not null });

            a.Library.SetOverride(track, TrackOverride.Of("Песня", "Исполнитель", "Альбом"));
            await WaitFor("B получил правку A", () => b.Library.Override(track.VideoId) is { Title: "Песня", ArtistsText: "Исполнитель", AlbumTitle: "Альбом" });

            // Лайк на B несёт метаданные YouTube — правка на A остаётся
            b.Library.SetLiked(track, true);
            await WaitFor("A получил лайк B", () => a.Library.IsLiked(track.VideoId));
            Assert.Equal("Альбом", a.Library.Override(track.VideoId)?.AlbumTitle);

            // «Как на YouTube» на B снимает правку и на A
            b.Library.SetOverride(track, TrackOverride.None);
            await WaitFor("A снял правку", () => a.Library.Override(track.VideoId) is null);
        }
        finally
        {
            if (a.Account.Session is not null) await a.Account.DeleteAccountAsync(password);
            output.WriteLine($"Аккаунт {login} удалён");
        }
    }
    /// <summary>tasks/0012: закрепление найденного текста с одного устройства — на другом; сдвиг «позже» тоже.</summary>
    [Fact]
    public async Task LyricsPinsReachOtherDevices()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MELOGOLD_LIVE") == "1", "MELOGOLD_LIVE=1");
        var server = Environment.GetEnvironmentVariable("MELOGOLD_SERVER") ?? AccountService.DefaultServerUrl;
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var password = "проверка связи " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var video = "e2e" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

        using var a = new Device("E2E Windows A", server, output);
        using var b = new Device("E2E Windows B", server, output);
        try
        {
            await a.Account.RegisterAsync(login, password);
            a.Sync.Start();
            await b.Account.SignInAsync(login, password);
            b.Sync.Start();
            await WaitFor("оба устройства синхронизировались", () =>
                a.Sync.Status is SyncStatus.Idle { LastSyncAt: not null } && b.Sync.Status is SyncStatus.Idle { LastSyncAt: not null });

            // На A найден текст LrcLib с id — прослушан 30 с, закреплён
            a.Library.SaveLyrics(video, new StoredLyrics("[00:01.00]Строка", "Строка", LyricsSources.LrcLib, LyricsSources.LrcLib, SyncedRef: "33476831", PlainRef: "33476831"));
            Assert.True(a.Library.PinPlayedLyrics(video));
            await WaitFor("B получил закрепление A", () => b.Library.LyricsPinOf(video) is { Source: "lrclib", Ref: "33476831" });

            // Сдвиг «позже» на A — в закреплении и на B
            a.Library.UpdatePin(video, new Melogold.Core.Lyrics.LyricsPin("lrclib", "33476831", 1500));
            await WaitFor("B получил сдвиг", () => b.Library.LyricsPinOf(video)?.StartTimeMs == 1500);
        }
        finally
        {
            if (a.Account.Session is not null) await a.Account.DeleteAccountAsync(password);
            output.WriteLine($"Аккаунт {login} удалён");
        }
    }
    /// <summary>
    /// tasks/0014 против ЛОКАЛЬНОГО сервера (<c>MELOGOLD_LOCAL_SERVER=http://127.0.0.1:18080</c>): живой вход на рабочем
    /// сервере задание запрещает. Режим request и режим invite до сессии, отказ, неверное число, отмена кода.
    /// </summary>
    [Fact]
    public async Task SignInByCodeBothModes()
    {
        var server = Environment.GetEnvironmentVariable("MELOGOLD_LOCAL_SERVER");
        Assert.SkipUnless(server is not null, "MELOGOLD_LOCAL_SERVER");
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var password = "проверка связи " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        using var laptop = new Device("MacBook Air", server, output);
        using var phone = new Device("Pixel 8", server, output);
        using var tablet = new Device("Galaxy Tab", server, output);
        await laptop.Account.RegisterAsync(login, password);

        // request: телефон показывает код, ноутбук вводит его и выбирает число, которое видно на телефоне
        var newPhone = new NewDeviceLinker(new AccountLinkPort(phone.Account), TimeSpan.FromMilliseconds(200));
        _ = newPhone.ShowCodeAsync();
        await WaitFor("телефон показал код", () => newPhone.State is NewDeviceLinkState.ShowingCode);
        var code = ((NewDeviceLinkState.ShowingCode)newPhone.State).UserCode;
        var details = await laptop.Account.ResolveLinkAsync(code);
        Assert.Equal("claimed", details.Status);
        Assert.Equal(3, details.VerifyChoices.Count);
        await WaitFor("телефон показал число", () => newPhone.State is NewDeviceLinkState.Verify { Login: var who } && who == login);
        var verify = (NewDeviceLinkState.Verify)newPhone.State;
        Assert.Equal("MacBook Air", verify.ApproverName);
        Assert.Contains(verify.VerifyCode, details.VerifyChoices);
        await laptop.Account.ApproveLinkAsync(details.LinkId, verify.VerifyCode);
        await WaitFor("телефон вошёл", () => newPhone.State is NewDeviceLinkState.SignedIn && phone.Account.Session is not null);
        Assert.Equal(login, phone.Account.Session!.Login);
        output.WriteLine("✓ request");

        // invite: ноутбук показывает код, планшет вводит его и показывает число; ноутбук его выбирает
        var invite = new InviteLinker(new AccountLinkPort(laptop.Account), TimeSpan.FromMilliseconds(300));
        _ = invite.StartAsync();
        await WaitFor("ноутбук показал код", () => invite.State is InviteState.Waiting);
        var inviteCode = ((InviteState.Waiting)invite.State).UserCode;
        var newTablet = new NewDeviceLinker(new AccountLinkPort(tablet.Account), TimeSpan.FromMilliseconds(200));
        _ = newTablet.ClaimAsync(inviteCode);
        await WaitFor("планшет показал число", () => newTablet.State is NewDeviceLinkState.Verify);
        await WaitFor("ноутбук увидел планшет", () => invite.State is InviteState.Claimed);
        var claimed = ((InviteState.Claimed)invite.State).Link;
        Assert.Equal("Galaxy Tab", claimed.Device?.Name);
        await laptop.Account.ApproveLinkAsync(claimed.LinkId, ((NewDeviceLinkState.Verify)newTablet.State).VerifyCode);
        invite.Release();
        await WaitFor("планшет вошёл", () => newTablet.State is NewDeviceLinkState.SignedIn && tablet.Account.Session is not null);
        output.WriteLine("✓ invite");

        // Отказ и неверное число — у нового устройства «Вход отклонён на другом устройстве»
        using var other = new Device("Другой телефон", server, output);
        var denied = new NewDeviceLinker(new AccountLinkPort(other.Account), TimeSpan.FromMilliseconds(200));
        _ = denied.ShowCodeAsync();
        await WaitFor("код для отказа", () => denied.State is NewDeviceLinkState.ShowingCode);
        var deny = await laptop.Account.ResolveLinkAsync(((NewDeviceLinkState.ShowingCode)denied.State).UserCode);
        await laptop.Account.DenyLinkAsync(deny.LinkId);
        await WaitFor("отказ дошёл", () => denied.State is NewDeviceLinkState.Failed { Failure: LinkFailure.Denied, Started: true });

        var wrong = new NewDeviceLinker(new AccountLinkPort(other.Account), TimeSpan.FromMilliseconds(200));
        _ = wrong.ShowCodeAsync();
        await WaitFor("код для неверного числа", () => wrong.State is NewDeviceLinkState.ShowingCode);
        var mismatch = await laptop.Account.ResolveLinkAsync(((NewDeviceLinkState.ShowingCode)wrong.State).UserCode);
        await WaitFor("число на экране", () => wrong.State is NewDeviceLinkState.Verify);
        var right = ((NewDeviceLinkState.Verify)wrong.State).VerifyCode;
        var error = await Assert.ThrowsAsync<ApiException>(() => laptop.Account.ApproveLinkAsync(mismatch.LinkId, mismatch.VerifyChoices.First(c => c != right)));
        Assert.Equal("link_verify_mismatch", error.Code);
        await WaitFor("неверное число — отказ", () => wrong.State is NewDeviceLinkState.Failed { Failure: LinkFailure.Denied });
        output.WriteLine("✓ отказ и неверное число");

        // Отмена кода новым устройством: ввести его уже нельзя (сервер отвечает link_expired и для отменённой)
        var cancelled = new NewDeviceLinker(new AccountLinkPort(other.Account));
        _ = cancelled.ShowCodeAsync();
        await WaitFor("код для отмены", () => cancelled.State is NewDeviceLinkState.ShowingCode);
        var cancelledCode = ((NewDeviceLinkState.ShowingCode)cancelled.State).UserCode;
        cancelled.Cancel();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        var gone = await Assert.ThrowsAsync<ApiException>(() => laptop.Account.ResolveLinkAsync(cancelledCode));
        Assert.Equal("link_expired", gone.Code);
        output.WriteLine("✓ отмена");

        await laptop.Account.DeleteAccountAsync(password);
    }

    /// <summary>
    /// tasks/0016 против ЛОКАЛЬНОГО сервера: поделиться своим плейлистом → ссылка открывается без входа на другом
    /// устройстве → «Мои ссылки» → удалить → «Ссылка удалена или неверна» (404). Без входа — первые 50 на YouTube.
    /// </summary>
    [Fact]
    public async Task SharedPlaylistOpensWithoutSignIn()
    {
        var server = Environment.GetEnvironmentVariable("MELOGOLD_LOCAL_SERVER");
        Assert.SkipUnless(server is not null, "MELOGOLD_LOCAL_SERVER");
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var password = "проверка связи " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        using var laptop = new Device("MacBook Air", server, output);
        using var friend = new Device("Pixel 8", server, output);
        var tracks = new List<Track>
        {
            new() { VideoId = "dQw4w9WgXcQ", Title = "Never Gonna Give You Up", ArtistsText = "Rick Astley", AlbumTitle = "Своё название альбома" },
            new() { VideoId = "fJ9rUzIMcZQ", Title = "Bohemian Rhapsody", ArtistsText = "Queen" },
        };

        // Без входа — список YouTube
        var offline = await new PlaylistSharing(laptop.Account).ShareAsync("Дорога", tracks);
        Assert.Equal(new PlaylistShare.OnYouTube("https://www.youtube.com/watch_videos?video_ids=dQw4w9WgXcQ,fJ9rUzIMcZQ", 2, 2), offline);

        await laptop.Account.RegisterAsync(login, password);
        try
        {
            Assert.True(await laptop.Account.SharesAvailableAsync());
            var shared = Assert.IsType<PlaylistShare.OnServer>(await new PlaylistSharing(laptop.Account).ShareAsync(" Дорога ", tracks));
            var reference = ShareLinkParser.Parse(shared.Url);
            Assert.NotNull(reference);
            Assert.Equal(shared.ShareId, reference.ShareId);
            output.WriteLine($"✓ ссылка {shared.Url}");

            // Друг не входил: снимок по ссылке и через melogold://share
            var snapshot = await friend.Account.OpenShareAsync(reference);
            Assert.Equal("Дорога", snapshot.Name);
            Assert.Equal(["dQw4w9WgXcQ", "fJ9rUzIMcZQ"], snapshot.Tracks.Select(t => t.VideoId));
            Assert.Equal("Своё название альбома", snapshot.Tracks[0].AlbumTitle);
            var deep = ShareLinkParser.Parse(ShareLinks.MelogoldShare(reference.ServerUrl, reference.ShareId));
            Assert.Equal(reference, deep);
            Assert.Equal(snapshot.Tracks.Count, (await friend.Account.OpenShareAsync(deep!)).Tracks.Count);
            output.WriteLine("✓ открыта без входа");

            var mine = await laptop.Account.SharesAsync();
            Assert.Equal(shared.ShareId, Assert.Single(mine.Shares).ShareId);
            await laptop.Account.DeleteShareAsync(shared.ShareId);
            Assert.Empty((await laptop.Account.SharesAsync()).Shares);
            var gone = await Assert.ThrowsAsync<ApiException>(() => friend.Account.OpenShareAsync(reference));
            Assert.Equal((404, "share_not_found"), (gone.Status, gone.Code));
            output.WriteLine("✓ удалена — «Ссылка удалена или неверна»");
        }
        finally
        {
            if (laptop.Account.Session is not null) await laptop.Account.DeleteAccountAsync(password);
        }
    }

    private sealed class RecordingPlayer : IRemotePlayer
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Done { get; } = new();

        public void Play() => Done.Enqueue("play");

        public void Pause() => Done.Enqueue("pause");

        public void Toggle() => Done.Enqueue("toggle");

        public void Next() => Done.Enqueue("next");

        public void Previous() => Done.Enqueue("previous");

        public void Seek(long positionMs) => Done.Enqueue($"seek {positionMs}");

        public void SetVolume(int volume) => Done.Enqueue($"volume {volume}");

        public void PlayQueue(IReadOnlyList<Track> tracks, int index, long startMs = 0) => Done.Enqueue($"play_queue {tracks[index].VideoId}");

        public void Stop() => Done.Enqueue("stop");
    }

    /// <summary>
    /// tasks/0017 против ЛОКАЛЬНОГО сервера: компьютер сообщает, что играет, телефон видит его в списке устройств и
    /// управляет им — пауза, громкость, трек из списка; выключенное управление; «Слушать здесь» на телефоне ставит
    /// компьютер на паузу.
    /// </summary>
    [Fact]
    public async Task RemoteControlBetweenTwoDevices()
    {
        var server = Environment.GetEnvironmentVariable("MELOGOLD_LOCAL_SERVER");
        Assert.SkipUnless(server is not null, "MELOGOLD_LOCAL_SERVER");
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var password = "проверка связи " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        using var pc = new Device("Windows PC", server, output);
        using var phone = new Device("Pixel 7 Pro", server, output);
        await pc.Account.RegisterAsync(login, password);
        try
        {
            await phone.Account.SignInAsync(login, password);
            Assert.True(await pc.Account.RemoteAvailableAsync());
            var queue = new List<Track>
            {
                new() { VideoId = "dQw4w9WgXcQ", Title = "Never Gonna Give You Up", ArtistsText = "Rick Astley", DurationMs = 213_000 },
                new() { VideoId = "fJ9rUzIMcZQ", Title = "Bohemian Rhapsody", ArtistsText = "Queen", DurationMs = 354_000 },
            };
            var playing = new LocalPlayback(queue, 0, 30_000, 213_000, true, 70);
            var pcClock = new ServerClock();
            var reporter = new PlaybackReporter(new AccountPlaybackServer(pc.Account), () => playing, pcClock);
            var player = new RecordingPlayer();
            var notices = new List<string>();
            var handler = new RemoteCommandHandler(player, notices.Add);
            pc.Sync.PlaybackCommand += command => handler.Execute(command);
            PlaybackUpdatedPayload? pcSaw = null;
            pc.Sync.PlaybackUpdated += update => pcSaw = update;
            pc.Sync.Start();
            phone.Sync.Start();
            await reporter.SendAsync();

            var remote = new RemoteController(new AccountPlaybackServer(phone.Account), new ServerClock());
            phone.Sync.PlaybackUpdated += remote.OnUpdated;
            RemoteDevice? target = null;
            await WaitFor("компьютер в сети и управляем", () =>
            {
                target = remote.DevicesAsync().GetAwaiter().GetResult().FirstOrDefault(d => d.Name == "Windows PC" && d.Online && d.Controllable);
                return target is not null;
            });
            Assert.Equal(("dQw4w9WgXcQ", 70), (target!.Playing?.Track?.VideoId, target.Volume));
            await remote.ConnectAsync(target);
            Assert.True(remote.State?.Playing);
            Assert.InRange(remote.Position, 30_000, 40_000);
            output.WriteLine("✓ телефон видит, что играет на компьютере");

            var started = Stopwatch.StartNew();
            Assert.True(await remote.PauseAsync());
            await WaitFor("пауза дошла", () => player.Done.Contains("pause"));
            output.WriteLine($"✓ пауза за {started.ElapsedMilliseconds} мс");
            Assert.Equal(["Pixel 7 Pro"], notices);
            Assert.True(await remote.SetVolumeAsync(30));
            Assert.True(await remote.SeekAsync(60_000));
            Assert.True(await remote.PlayQueueAsync(queue, 1));
            await WaitFor("громкость, перемотка, трек", () => player.Done.Contains("volume 30") && player.Done.Contains("seek 60000") && player.Done.Contains("play_queue fJ9rUzIMcZQ"));

            // Компьютер сообщил итог — телефон видит паузу и громкость
            playing = playing with { Playing = false, Volume = 30, PositionMs = 60_000 };
            await reporter.SendAsync();
            await WaitFor("телефон увидел итог", () => remote.State is { Playing: false, Volume: 30 });
            output.WriteLine("✓ команды выполнены, итог виден пульту");

            // Управление выключено: поток без remote=1
            pc.Sync.RemoteAllowed = false;
            await WaitFor("управление выключено", () => remote.DevicesAsync().GetAwaiter().GetResult().Any(d => d.Name == "Windows PC" && d.Online && !d.Controllable));
            RemoteFailure? failure = null;
            remote.Failed += (why, _) => failure = why;
            Assert.False(await remote.NextAsync());
            Assert.Equal(RemoteFailure.Disabled, failure);
            Assert.Null(remote.Target);
            output.WriteLine("✓ выключенное управление");

            // «Слушать здесь» на телефоне: компьютер видит handoffFrom на себя и ставит паузу
            pc.Sync.RemoteAllowed = true;
            playing = playing with { Playing = true };
            await reporter.SendAsync();
            var phoneReporter = new PlaybackReporter(new AccountPlaybackServer(phone.Account), () => new LocalPlayback(queue, 0, 5000, 213_000, true, 50), new ServerClock());
            phoneReporter.TakeOverFrom(pc.Account.Session!.DeviceId, reporter.SessionId);
            await WaitFor("компьютер узнал, что воспроизведение забрали", () => reporter.IsTakenFromHere(pcSaw?.State, pc.Account.Session!.DeviceId));
            output.WriteLine("✓ «Слушать здесь»");
        }
        finally
        {
            if (pc.Account.Session is not null) await pc.Account.DeleteAccountAsync(password);
        }
    }

    /// <summary>
    /// tasks/0018 против ЛОКАЛЬНОГО сервера: второе устройство послушало трек, его отозвали — после devices.updated оно
    /// пропадает из фильтра устройств, а прослушивание остаётся во «Все устройства».
    /// </summary>
    [Fact]
    public async Task RevokedDeviceLeavesTheFilter()
    {
        var server = Environment.GetEnvironmentVariable("MELOGOLD_LOCAL_SERVER");
        Assert.SkipUnless(server is not null, "MELOGOLD_LOCAL_SERVER");
        var login = "e2ewin" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var password = "проверка связи " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        using var pc = new Device("Windows PC", server, output);
        using var phone = new Device("Pixel 8", server, output);
        await pc.Account.RegisterAsync(login, password);
        try
        {
            pc.Sync.Start();
            await phone.Account.SignInAsync(login, password);
            phone.Sync.Start();
            var phoneId = ((AccountState.SignedIn)phone.Account.State).DeviceId;
            var pcId = ((AccountState.SignedIn)pc.Account.State).DeviceId;
            var known = new KnownDevices(pc.Account, pc.Library, pc.Sync);
            var changed = 0;
            known.Changed += () => Interlocked.Increment(ref changed);

            phone.Library.RecordPlay(A1, 60_000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await WaitFor("компьютер видит прослушивание телефона", () => pc.Library.HistoryDeviceIds().SequenceEqual([phoneId]));
            Assert.Equal([phoneId], KnownDevices.Others(pc.Library.HistoryDeviceIds(), pcId, await known.ListAsync()).Select(d => d.Id));

            await pc.Account.RevokeAsync(phoneId, password);
            await WaitFor("devices.updated дошёл", () => Volatile.Read(ref changed) > 0);
            Assert.Empty(KnownDevices.Others(pc.Library.HistoryDeviceIds(), pcId, await known.ListAsync()));
            Assert.Single(pc.Library.RecentHistory());
            output.WriteLine("✓ отозванное устройство пропало из фильтра, прослушивание — во «Все устройства»");
        }
        finally
        {
            if (pc.Account.Session is not null) await pc.Account.DeleteAccountAsync(password);
        }
    }
}
