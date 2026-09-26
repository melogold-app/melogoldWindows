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
}
