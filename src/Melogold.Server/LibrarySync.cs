using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Lyrics;
using Melogold.Core.Music;

namespace Melogold.Server;

/// <summary>Что делает синхронизация — для Настроек.</summary>
public abstract record SyncStatus
{
    /// <summary>Нет аккаунта.</summary>
    public sealed record Off : SyncStatus;

    public sealed record Idle(long? LastSyncAt) : SyncStatus;

    public sealed record Syncing : SyncStatus;

    public sealed record Failed(bool Offline, long? LastSyncAt) : SyncStatus;
}

/// <summary>
/// Держит библиотеку этого устройства и аккаунта на сервере одинаковыми (API §4.8): Избранное, плейлисты с порядком,
/// сохранённые альбомы, исполнители и каналы.
/// <para>
/// <b>Вариант со снимком</b> (REWRITE §4.12a Android, <c>sync/SyncEngine.kt</c>): вместо журнала правок библиотека
/// сравнивается с тем, что было на сервере после прошлой синхронизации (таблицы <c>synced_*</c>), разница уходит ops,
/// ответ сервера обновляет и библиотеку, и снимок. Каждая op несёт <c>base</c> — курсор снимка: она выигрывает у того,
/// что устройство видело, и сравнивается по времени с тем, чего не видело. Сервер отвечает текущими строками всех
/// ключей, которых коснулись ops, поэтому проигравшая правка сразу приходит победителем.
/// </para>
/// Синхронизация одна за раз: при входе и запуске, через 2 с после правки библиотеки, по SSE <c>sync.changed</c> и
/// <c>system.connected</c>, когда возвращается сеть, по «Синхронизировать сейчас» и перед выходом.
/// </summary>
public sealed class LibrarySync : IDisposable
{
    private const int MaxOps = 500;
    private const int NameMax = 200;
    private static readonly TimeSpan LocalChangeDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private const string KeyBinding = "binding";
    private const string KeyCursor = "cursor";
    private const string KeyMerge = "needsMerge";
    private const string KeyLastSync = "lastSyncAt";
    private const string KeyLyricsRev = "lyricsRev";
    private const string KeyHistoryMerge = "historyMerge";
    private const string KeyHistoryRetryAt = "historyRetryAt";

    /// <summary>Сколько последних прослушиваний отправить при первой синхронизации, если сервер не сказал (<c>limits.history</c>).</summary>
    private const int DefaultMergeUploadMax = 20_000;

    /// <summary>Самое долгое прослушивание, которое примет сервер (<c>play.add</c>).</summary>
    private const long MaxPlayTimeMs = 86_400_000;
    private const int BaselineChunk = 500;
    private static readonly TimeSpan FeaturesMaxAge = TimeSpan.FromMinutes(30);

    private readonly AccountService _account;
    private readonly SyncStore _store;
    private readonly Action<string, Exception?> _log;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Lock _lock = new();
    private CancellationTokenSource? _session;
    private CancellationTokenSource? _debounce;
    private bool _pendingLocal;

    /// <summary>Курсор, от которого считаются строящиеся ops (их <c>base</c>).</summary>
    private string? _base;

    /// <summary>Когда последний раз спрашивали <c>/server/info</c> про модуль текстов.</summary>
    private DateTime _featuresAt;

    public LibrarySync(AccountService account, SyncStore store, Action<string, Exception?> log)
    {
        _account = account;
        _store = store;
        _log = log;
        Status = new SyncStatus.Off();
    }

    public SyncStatus Status { get; private set; }

    /// <summary>Сменился статус (из любого потока).</summary>
    public event Action<SyncStatus>? StatusChanged;

    /// <summary>Список устройств изменился на сервере (<c>devices.updated</c>): экраны со списком перечитывают его.</summary>
    public event Action? DevicesChanged;

    /// <summary>Свой текст сервер не принял как слишком большой (413): он остаётся только на этом устройстве.</summary>
    public event Action<string>? LyricsRejected;

    public void Start()
    {
        _account.StateChanged += OnAccountChanged;
        _store.Library.Changed += OnLibraryChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        OnAccountChanged(_account.State);
    }

    private void OnAccountChanged(AccountState state)
    {
        lock (_lock)
        {
            _session?.Cancel();
            _session = null;
            if (state is not AccountState.SignedIn)
            {
                SetStatus(new SyncStatus.Off());
                return;
            }
            _session = new CancellationTokenSource();
        }
        var token = _session.Token;
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            // Без сети — ничего не пробовать; когда она вернётся, всё начнётся заново
            SetStatus(new SyncStatus.Failed(true, LastSyncAt()));
            return;
        }
        SetStatus(new SyncStatus.Idle(LastSyncAt()));
        _ = SyncAsync(true, token);
        _ = FollowLiveEventsAsync(token);
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (_account.State is not AccountState.SignedIn) return;
        if (e.IsAvailable) OnAccountChanged(_account.State);
        else
        {
            lock (_lock)
            {
                _session?.Cancel();
                _session = null;
            }
            SetStatus(new SyncStatus.Failed(true, LastSyncAt()));
        }
    }

    /// <summary>Правка Избранного, плейлистов или закладок уходит через 2 с.</summary>
    private void OnLibraryChanged(LibraryChange change)
    {
        if ((change & (LibraryChange.Likes | LibraryChange.Playlists | LibraryChange.Bookmarks | LibraryChange.Lyrics | LibraryChange.History)) == 0) return;
        if (_account.State is not AccountState.SignedIn) return;
        CancellationTokenSource debounce;
        lock (_lock)
        {
            _pendingLocal = true;
            _debounce?.Cancel();
            _debounce = debounce = new CancellationTokenSource();
        }
        _ = Task.Delay(LocalChangeDelay, debounce.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ = SyncAsync(false);
        }, TaskScheduler.Default);
    }

    /// <summary>Перед выходом: ещё не отправленные правки — одной синхронизацией, не дольше <paramref name="timeout"/>.</summary>
    public void Flush(TimeSpan timeout)
    {
        if (!_pendingLocal || _account.Session is null) return;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            Task.Run(() => SyncAsync(false, cts.Token)).Wait(timeout);
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>Синхронизировать сейчас; <paramref name="force"/> — спросить сервер, даже если здесь ничего не менялось.</summary>
    public Task<bool> SyncAsync(bool force = true, CancellationToken ct = default) => RunAsync(force, false, ct);

    /// <param name="lyricsOnly">только тексты (<c>lyrics.changed</c>): библиотека — лишь если здесь есть неотправленные правки</param>
    private async Task<bool> RunAsync(bool force, bool lyricsOnly, CancellationToken ct)
    {
        if (_account.Session is null) return true;
        try
        {
            await _mutex.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        var last = LastSyncAt();
        try
        {
            SetStatus(new SyncStatus.Syncing());
            _pendingLocal = false;
            await SyncOnceAsync(force, lyricsOnly, ct).ConfigureAwait(false);
            SetStatus(new SyncStatus.Idle(LastSyncAt()));
            return true;
        }
        catch (Exception e)
        {
            if (e is not OperationCanceledException) _log("Sync failed", e);
            SetStatus(_account.Session is null
                ? new SyncStatus.Off()
                : e is OperationCanceledException ? new SyncStatus.Idle(last) : new SyncStatus.Failed(e is ApiException { IsNetwork: true }, last));
            return false;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task SyncOnceAsync(bool force, bool lyricsOnly, CancellationToken ct)
    {
        var session = _account.Session;
        if (session is null) return;
        var binding = $"{session.ServerId}:{session.UserId}";
        _store.Run(tx =>
        {
            if (tx.State(KeyBinding) == binding) return;
            // Другой аккаунт или сервер: здесь с ним ничего не синхронизировано, уходит (и сливается) всё
            tx.ForgetBinding();
            tx.SetState(KeyBinding, binding);
            tx.SetState(KeyMerge, "1");
            tx.SetState(KeyHistoryMerge, "1");
        });
        if (_store.State(KeyMerge) == "1") await PlanMergeAsync(ct).ConfigureAwait(false);

        var ops = _store.Run(BuildOps);
        if (ops.Count > 0 || (force && !lyricsOnly)) await SyncLibraryAsync(ops, ct).ConfigureAwait(false);
        // После всех прослушиваний первой синхронизации — накопленное время (play.baseline): раньше его нельзя, иначе
        // отложенные лимитом play.add прибавились бы к нему ещё раз
        if (ops.Count > 0 && _store.State(KeyHistoryMerge) == "1" && _store.Run(BuildOps) is { Count: > 0 } more)
            await SyncLibraryAsync(more, ct).ConfigureAwait(false);
        // Тексты — после библиотеки, в том же цикле (docs/LYRICS-SYNC.md §3.3)
        await SyncLyricsAsync(force, ct).ConfigureAwait(false);
    }

    private async Task SyncLibraryAsync(List<Op> ops, CancellationToken ct)
    {
        var cursor = _store.State(KeyCursor) ?? "";
        var restarted = false;
        while (true)
        {
            var batch = ops.Take(MaxOps).ToList();
            SyncResponse response;
            try
            {
                var request = new SyncRequest { Cursor = cursor, Ops = batch.Select(o => o.Json).ToList(), Streams = ["library", "history"] };
                response = await _account.AuthorizedAsync((api, token) => api.SyncAsync(token, request, ct), ct).ConfigureAwait(false);
            }
            catch (ApiException e) when (e.Status == 410 && !restarted)
            {
                // Сервер восстановлен из копии или забыл курсор: прочитать всё заново (API §4.8, 410)
                restarted = true;
                cursor = "";
                continue;
            }
            ops = ops.Skip(batch.Count).ToList();
            var (changes, retryAfter) = _store.Run(tx =>
            {
                var retry = ApplyResults(tx, batch, response.Results);
                ApplyRows(tx, response);
                tx.SetState(KeyCursor, response.Cursor);
                return (tx.Changes, retry);
            });
            if (retryAfter is { } delay)
            {
                // Больше 2000 прослушиваний в час (op_rate_limited): остальные — после паузы, которую назвал сервер
                ops.RemoveAll(o => o.Kind == "play.add");
                _ = Task.Delay(delay, CancellationToken.None).ContinueWith(_ => SyncAsync(false), TaskScheduler.Default);
            }
            _store.Library.Notify(changes);
            cursor = response.Cursor;
            if (!response.HasMore && ops.Count == 0) break;
        }
        _store.Run(tx =>
        {
            tx.SetState(KeyLastSync, IsoTime.NowMs().ToString(CultureInfo.InvariantCulture));
            tx.SetState(KeyMerge, "0");
        });
    }

    /// <summary>Первая синхронизация с аккаунтом: свои плейлисты занимают серверных двойников, а не задваивают их.</summary>
    private async Task PlanMergeAsync(CancellationToken ct)
    {
        var locals = _store.Run(tx => tx.Playlists());
        if (locals.Count == 0) return;
        var request = new MergePlanRequest(locals.Select(p => new MergePlanInput(p.Id.ToString(CultureInfo.InvariantCulture), PlaylistName(p.Name), BrowseId: p.BrowseId)).ToList());
        var plan = await _account.AuthorizedAsync((api, token) => api.MergePlanAsync(token, request, ct), ct).ConfigureAwait(false);
        _store.Run(tx =>
        {
            foreach (var entry in plan.Plan)
            {
                if (!long.TryParse(entry.LocalKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                if (entry.Action is "merge" or "create") tx.SetPlaylistSyncId(id, entry.PlaylistId);
            }
        });
    }

    /// <summary>Одна op <c>POST /sync</c> и её ключ (для результата).</summary>
    private sealed record Op(string Kind, string Key, JsonObject Json);

    /// <summary>Что изменилось здесь с прошлой синхронизации — ops. В транзакции: новые плейлисты получают <c>sync_id</c>.</summary>
    private List<Op> BuildOps(SyncTx tx)
    {
        var ops = new List<Op>();
        var now = IsoTime.NowMs();
        _base = tx.State(KeyCursor) is { Length: > 0 } cursor ? cursor : null;

        // Избранное
        var liked = tx.Likes();
        var likedIds = liked.Select(l => l.Track.VideoId).ToHashSet(StringComparer.Ordinal);
        var syncedLikes = tx.SyncedLikes();
        foreach (var like in liked.Where(l => !syncedLikes.Contains(l.Track.VideoId)))
        {
            ops.Add(MakeOp("like.set", "like:" + like.Track.VideoId, like.LikedAt, o =>
            {
                o["videoId"] = like.Track.VideoId;
                o["liked"] = true;
                o["likedAt"] = IsoTime.Format(like.LikedAt);
                PutTracks(o, [like.Track]);
            }));
        }
        foreach (var videoId in syncedLikes.Where(id => !likedIds.Contains(id)))
        {
            ops.Add(MakeOp("like.set", "like:" + videoId, now, o =>
            {
                o["videoId"] = videoId;
                o["liked"] = false;
            }));
        }

        // Плейлисты
        var synced = tx.SyncedPlaylists();
        var playlists = tx.Playlists();
        foreach (var playlist in playlists)
        {
            var songs = tx.PlaylistVideoIds(playlist.Id);
            if (playlist.SyncId is null)
            {
                var newId = Guid.NewGuid().ToString();
                tx.SetPlaylistSyncId(playlist.Id, newId);
                ops.Add(PlaylistOp(tx, "playlist.create", newId, now, playlist, songs));
            }
            else if (!synced.TryGetValue(playlist.SyncId, out var previous))
            {
                // Занят по плану слияния или создан и ещё не подтверждён: import сливает
                ops.Add(PlaylistOp(tx, "playlist.import", playlist.SyncId, now, playlist, songs));
            }
            else
            {
                var syncId = playlist.SyncId;
                if (previous.Name != playlist.Name || previous.ThumbnailUrl != playlist.ThumbnailUrl)
                {
                    ops.Add(MakeOp("playlist.update", "pl:" + syncId, now, o =>
                    {
                        o["playlistId"] = syncId;
                        o["name"] = PlaylistName(playlist.Name);
                        if (playlist.ThumbnailUrl is not null) o["thumbnailUrl"] = playlist.ThumbnailUrl;
                    }));
                }
                if (!previous.VideoIds.SequenceEqual(songs, StringComparer.Ordinal))
                    ops.AddRange(ItemOps(tx, syncId, PlaylistDiff.Changes(previous.VideoIds, songs), now));
            }
        }
        var present = playlists.Select(p => p.SyncId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var syncId in synced.Keys.Where(id => !present.Contains(id)))
            ops.Add(MakeOp("playlist.delete", "pl:" + syncId, now, o => o["playlistId"] = syncId));

        // Сохранённые альбомы, исполнители и каналы
        var bookmarks = tx.Bookmarks();
        var bookmarkKeys = bookmarks.Select(b => (b.Type, b.BrowseId)).ToHashSet();
        var syncedBookmarks = tx.SyncedBookmarks();
        foreach (var bookmark in bookmarks.Where(b => !syncedBookmarks.Contains((b.Type, b.BrowseId))))
        {
            ops.Add(MakeOp("bookmark.set", $"bm:{bookmark.Type}:{bookmark.BrowseId}", now, o =>
            {
                o["type"] = bookmark.Type;
                o["browseId"] = bookmark.BrowseId;
                o["bookmarked"] = true;
                o["bookmarkedAt"] = IsoTime.Format(bookmark.BookmarkedAt);
                if (bookmark.Title is not null) o["title"] = bookmark.Title;
                if (bookmark.Subtitle is not null) o["subtitle"] = bookmark.Subtitle;
                if (bookmark.ThumbnailUrl is not null) o["thumbnailUrl"] = bookmark.ThumbnailUrl;
                if (bookmark.Year is not null) o["year"] = bookmark.Year;
            }));
        }
        foreach (var (type, browseId) in syncedBookmarks.Where(k => !bookmarkKeys.Contains(k)))
        {
            ops.Add(MakeOp("bookmark.set", $"bm:{type}:{browseId}", now, o =>
            {
                o["type"] = type;
                o["browseId"] = browseId;
                o["bookmarked"] = false;
            }));
        }
        ops.AddRange(HistoryOps(tx, now));
        return ops;
    }

    /// <summary>
    /// История (tasks/0002 §3.2): свои неотправленные прослушивания — <c>play.add</c> (opId = eventId), «Убрать из
    /// истории» и «Очистить историю» — <c>history.forget</c> и <c>history.clear</c>. При первой синхронизации с аккаунтом —
    /// не больше <c>mergeUploadMax</c> самых свежих прослушиваний, а когда все они на сервере — накопленное время треков
    /// <c>play.baseline atLeast</c>.
    /// </summary>
    private List<Op> HistoryOps(SyncTx tx, long now)
    {
        var ops = new List<Op>();
        var merge = tx.State(KeyHistoryMerge) == "1";
        var retryAt = long.TryParse(tx.State(KeyHistoryRetryAt), NumberStyles.Integer, CultureInfo.InvariantCulture, out var saved) ? saved : 0;
        var plays = tx.UnsentPlays();
        if (retryAt <= now)
        {
            var limit = MergeUploadMax();
            if (merge && plays.Count > limit)
            {
                // Старше последних mergeUploadMax — не отправляются: сервер их всё равно не примет
                foreach (var old in plays.Take(plays.Count - limit)) tx.MarkPlaySent(old.EventId);
                plays = plays.Skip(plays.Count - limit).ToList();
            }
            foreach (var play in plays)
            {
                var track = tx.Track(play.VideoId);
                ops.Add(MakeOp("play.add", "play:" + play.EventId, play.PlayedAt, o =>
                {
                    o["videoId"] = play.VideoId;
                    o["playedAt"] = IsoTime.Format(play.PlayedAt);
                    o["playTimeMs"] = Math.Clamp(play.PlayTimeMs, 1, MaxPlayTimeMs);
                    o["history"] = true;
                    o["playtime"] = true;
                    if (track is not null) PutTracks(o, [track]);
                }, opId: play.EventId));
            }
        }
        foreach (var op in tx.HistoryOps())
        {
            ops.Add(MakeOp(op.Kind, "hop:" + op.OpId, op.EventsBefore, o =>
            {
                if (op.Kind == "history.forget")
                {
                    o["videoId"] = op.VideoId;
                    // Общее время трека обнуляется на всех устройствах, как у Android и Apple (tasks/0008)
                    o["resetTotal"] = true;
                }
                o["eventsBefore"] = IsoTime.Format(op.EventsBefore);
            }, opId: op.OpId));
        }
        if (merge && plays.Count == 0)
        {
            var totals = tx.PlayTotals();
            if (totals.Count == 0) tx.SetState(KeyHistoryMerge, "0");
            foreach (var chunk in totals.Chunk(BaselineChunk))
            {
                ops.Add(MakeOp("play.baseline", "stat:batch", now, o =>
                {
                    o["mode"] = "atLeast";
                    o["entries"] = new JsonArray([.. chunk.Select(e => (JsonNode)new JsonObject { ["videoId"] = e.VideoId, ["totalMs"] = e.TotalMs })]);
                }));
            }
        }
        return ops;
    }

    /// <summary><c>limits.history.mergeUploadMax</c> из <c>/server/info</c>.</summary>
    private int MergeUploadMax()
    {
        try
        {
            return _account.ServerInfo?.Limits?["history"]?["mergeUploadMax"]?.GetValue<int>() is > 0 and var max ? max : DefaultMergeUploadMax;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return DefaultMergeUploadMax;
        }
    }

    /// <summary>Ops треков одного плейлиста; добавляемые треки несут метаданные.</summary>
    private IEnumerable<Op> ItemOps(SyncTx tx, string syncId, List<ItemChange> changes, long now) => changes.Select(change => change switch
    {
        ItemChange.Remove remove => MakeOp("playlist.item.remove", "pl:" + syncId, now, o =>
        {
            o["playlistId"] = syncId;
            o["videoId"] = remove.VideoId;
        }),
        ItemChange.Add add => MakeOp("playlist.items.add", "pl:" + syncId, now, o =>
        {
            o["playlistId"] = syncId;
            o["videoIds"] = new JsonArray([.. add.VideoIds.Select(id => (JsonNode)id)]);
            if (add.After is not null) o["after"] = add.After;
            if (add.Before is not null) o["before"] = add.Before;
            PutTracks(o, add.VideoIds.Select(tx.Track).OfType<Track>());
        }),
        ItemChange.Move move => MakeOp("playlist.item.move", "pl:" + syncId, now, o =>
        {
            o["playlistId"] = syncId;
            o["videoId"] = move.VideoId;
            if (move.After is not null) o["after"] = move.After;
            if (move.Before is not null) o["before"] = move.Before;
        }),
        _ => throw new InvalidOperationException(change.GetType().Name),
    }).ToList();

    private Op PlaylistOp(SyncTx tx, string kind, string syncId, long now, PlaylistRecord playlist, List<string> songs) =>
        MakeOp(kind, "pl:" + syncId, now, o =>
        {
            o["playlistId"] = syncId;
            o["name"] = PlaylistName(playlist.Name);
            if (playlist.BrowseId is not null) o["browseId"] = playlist.BrowseId;
            if (playlist.ThumbnailUrl is not null) o["thumbnailUrl"] = playlist.ThumbnailUrl;
            o["videoIds"] = new JsonArray([.. songs.Select(id => (JsonNode)id)]);
            PutTracks(o, songs.Select(tx.Track).OfType<Track>());
        });

    private static string PlaylistName(string name) => Utf16.Truncate(name, NameMax) is { Length: > 0 } trimmed && trimmed.Trim().Length > 0 ? trimmed : "—";

    private Op MakeOp(string kind, string key, long at, Action<JsonObject> fields, string? opId = null)
    {
        var json = new JsonObject
        {
            ["opId"] = opId ?? Guid.NewGuid().ToString(),
            ["kind"] = kind,
            ["at"] = IsoTime.Format(at),
        };
        if (_base is not null) json["base"] = _base;
        fields(json);
        return new Op(kind, key, json);
    }

    /// <summary>Метаданные треков, о которых говорит op (API §4.8 <c>tracks</c>): другие устройства смогут их показать.</summary>
    private static void PutTracks(JsonObject o, IEnumerable<Track> tracks)
    {
        var array = new JsonArray();
        foreach (var track in tracks)
        {
            var input = new TrackInput
            {
                VideoId = track.VideoId,
                // Заглушка (название = videoId) — без названия: сервер оставит своё
                Title = track.Title == track.VideoId ? null : track.Title,
                ArtistsText = track.ArtistsText,
                Artists = track.Artists.Count > 0 ? track.Artists.Select(a => new ArtistRefDto(a.Id, a.Name)).ToList() : null,
                AlbumId = track.AlbumId,
                AlbumTitle = track.AlbumTitle,
                DurationMs = track.DurationMs,
                DurationText = track.DurationText,
                ThumbnailUrl = track.ThumbnailUrl,
                Explicit = track.Explicit ? true : null,
                VideoType = track.VideoType,
            };
            array.Add(JsonSerializer.SerializeToNode(input, MelogoldApi.Json));
        }
        if (array.Count > 0) o["tracks"] = array;
    }

    /// <summary>
    /// Результаты ops: плейлист, который сервер перенёс в копию восстановления (<c>redirected</c>), переходит туда и здесь;
    /// прослушивания и действия с историей, которые сервер принял или отверг, больше не отправляются. Возвращает паузу,
    /// если сервер отложил прослушивания (<c>deferred</c>, лимит в час).
    /// </summary>
    private static TimeSpan? ApplyResults(SyncTx tx, List<Op> batch, IReadOnlyList<OpResult> results)
    {
        TimeSpan? retry = null;
        bool? baselineDone = null;
        foreach (var (op, result) in batch.Zip(results))
        {
            var deferred = result.Status == "deferred";
            switch (op.Kind)
            {
                case "play.add":
                    // applied (и replayed), superseded, rejected — больше не слать; deferred — после паузы
                    if (!deferred) tx.MarkPlaySent(op.Key["play:".Length..]);
                    else retry ??= TimeSpan.FromSeconds(Math.Max(1, result.RetryAfterSeconds ?? 3600));
                    continue;
                case "history.forget" or "history.clear":
                    if (!deferred) tx.DeleteHistoryOp(op.Key["hop:".Length..]);
                    continue;
                case "play.baseline":
                    baselineDone = (baselineDone ?? true) && !deferred;
                    continue;
            }
            if (result.Status != "redirected" || !op.Key.StartsWith("pl:", StringComparison.Ordinal) || result.PlaylistId is not { } newId) continue;
            var old = op.Key[3..];
            if (tx.PlaylistBySyncId(old) is { } local)
            {
                tx.SetPlaylistSyncId(local.Id, newId);
                // В копии только то, что несла op: остальные треки уйдут туда как новые
                tx.ClearSortKeys(local.Id);
            }
            tx.DeleteSyncedPlaylist(old);
        }
        if (baselineDone is { } done) tx.SetState(KeyHistoryMerge, done ? "0" : "1");
        if (retry is { } pause) tx.SetState(KeyHistoryRetryAt, (IsoTime.NowMs() + (long)pause.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        return retry;
    }

    /// <summary>
    /// Ответ сервера в порядке API §4.8 (треки, плейлисты по <c>createdAt</c>, их треки, лайки, закладки): библиотека и
    /// снимок становятся тем, что на сервере.
    /// </summary>
    private static void ApplyRows(SyncTx tx, SyncResponse response)
    {
        foreach (var track in response.Tracks) tx.EnsureTrack(track.VideoId, ToTrack(track));

        var touched = new HashSet<long>();
        var synced = tx.SyncedPlaylists();
        foreach (var row in response.Playlists.OrderBy(p => IsoTime.TryParse(p.CreatedAt) ?? 0))
        {
            var local = tx.PlaylistBySyncId(row.Id);
            if (row.Deleted)
            {
                if (local is not null) tx.DeletePlaylist(local.Id);
                tx.DeleteSyncedPlaylist(row.Id);
            }
            else if (local is null)
            {
                var id = tx.InsertPlaylist(row.Name, row.BrowseId, row.ThumbnailUrl, row.Id, IsoTime.TryParse(row.CreatedAt) ?? IsoTime.NowMs());
                tx.UpsertSyncedPlaylist(new SyncedPlaylist(row.Id, row.Name, row.ThumbnailUrl, []));
                touched.Add(id);
            }
            else
            {
                if (local.Name != row.Name || local.ThumbnailUrl != row.ThumbnailUrl) tx.UpdatePlaylist(local.Id, row.Name, row.ThumbnailUrl);
                var previous = synced.TryGetValue(row.Id, out var known) ? known.VideoIds : [];
                tx.UpsertSyncedPlaylist(new SyncedPlaylist(row.Id, row.Name, row.ThumbnailUrl, previous));
            }
        }

        foreach (var group in response.Items.GroupBy(i => i.PlaylistId))
        {
            if (tx.PlaylistBySyncId(group.Key) is not { } playlist) continue;
            foreach (var item in group)
            {
                if (item.Present)
                {
                    tx.EnsureTrack(item.VideoId, null);
                    tx.UpsertItem(playlist.Id, item.VideoId, item.SortKey, IsoTime.TryParse(item.AddedAt) ?? IsoTime.NowMs());
                }
                else tx.DeleteItem(playlist.Id, item.VideoId);
            }
            touched.Add(playlist.Id);
        }
        foreach (var id in touched) tx.Reorder(id);

        foreach (var row in response.Likes)
        {
            tx.EnsureTrack(row.VideoId, null);
            tx.SetLike(row.VideoId, row.Liked ? IsoTime.TryParse(row.LikedAt) ?? IsoTime.NowMs() : null);
        }

        foreach (var row in response.Bookmarks)
        {
            if (row.Type is not ("album" or "artist")) continue;
            tx.SetBookmark(row.Type, row.BrowseId, row.Bookmarked ? IsoTime.TryParse(row.BookmarkedAt) ?? IsoTime.NowMs() : null,
                row.Title, row.Subtitle, row.ThumbnailUrl, row.Year);
        }

        ApplyHistoryRows(tx, response);
    }

    /// <summary>
    /// История с сервера (tasks/0002 §3.3), в порядке API §4.8: общее время трека (уже по всем устройствам),
    /// прослушивания любого устройства (своё вернувшееся не задваивается), забытые события. Забытое с
    /// <c>totalBefore</c> (другой клиент убрал трек с <c>resetTotal: true</c>) обнуляет общее время трека, если в том же
    /// ответе нет его свежего <c>playStats</c> (tasks/0008).
    /// </summary>
    internal static void ApplyHistoryRows(SyncTx tx, SyncResponse response)
    {
        var withStats = response.PlayStats.Select(r => r.VideoId).ToHashSet(StringComparer.Ordinal);
        foreach (var row in response.PlayStats)
        {
            tx.EnsureTrack(row.VideoId, null);
            tx.SetPlayTotal(row.VideoId, row.TotalPlayTimeMs);
        }
        foreach (var row in response.Plays)
        {
            if (IsoTime.TryParse(row.PlayedAt) is not { } playedAt) continue;
            tx.EnsureTrack(row.VideoId, null);
            tx.InsertPlay(row.EventId, row.VideoId, playedAt, row.PlayTimeMs, row.DeviceId);
        }
        foreach (var row in response.PlayForgets)
        {
            if (IsoTime.TryParse(row.EventsBefore) is { } before) tx.ForgetPlays(row.VideoId, before);
            if (row.TotalBefore is not null && row.VideoId != "*" && !withStats.Contains(row.VideoId)) tx.SetPlayTotal(row.VideoId, 0);
        }
    }

    /// <summary>Метаданные трека с сервера; у заглушки их нет.</summary>
    private static Track? ToTrack(TrackDto dto) => dto.MetadataStub || string.IsNullOrWhiteSpace(dto.Title) ? null : new Track
    {
        VideoId = dto.VideoId,
        Title = dto.Title,
        ArtistsText = dto.ArtistsText,
        Artists = dto.Artists?.Select(a => new ArtistRef(a.Id, a.Name)).ToList() ?? [],
        AlbumId = dto.AlbumId,
        AlbumTitle = dto.AlbumTitle,
        DurationMs = dto.DurationMs,
        DurationText = dto.DurationText,
        ThumbnailUrl = dto.ThumbnailUrl,
        Explicit = dto.Explicit,
        VideoType = dto.VideoType,
    };

    /// <summary>Живые события (API §6): синхронизация на <c>sync.changed</c>, выход на <c>session.invalidated</c>.</summary>
    private async Task FollowLiveEventsAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.Zero;
        while (!ct.IsCancellationRequested && _account.Session is not null)
        {
            var started = DateTime.UtcNow;
            try
            {
                await _account.AuthorizedAsync(async (api, token) =>
                {
                    await foreach (var e in api.EventsAsync(token, ct).ConfigureAwait(false)) OnLiveEvent(e, ct);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log("Live stream closed: " + e.Message, null);
            }
            if (_account.Session is null) return;
            // Долгий поток — не сбой: сервер закрывает его, когда истекает токен
            backoff = DateTime.UtcNow - started > MaxBackoff ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Clamp(backoff.TotalMilliseconds * 2, 1000, MaxBackoff.TotalMilliseconds));
            try
            {
                await Task.Delay(backoff + TimeSpan.FromMilliseconds(Random.Shared.NextInt64((long)backoff.TotalMilliseconds / 4 + 1)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnLiveEvent(LiveEvent e, CancellationToken ct)
    {
        switch (e.Type)
        {
            case "system.connected" or "sync.changed":
                _ = SyncAsync(true, ct);
                break;
            case "devices.updated":
                DevicesChanged?.Invoke();
                break;
            case "session.invalidated":
                _account.EndSession();
                break;
            case "lyrics.changed":
                _ = RunAsync(true, true, ct);
                break;
        }
    }

    // ---------- Тексты песен (API §4.10, docs/LYRICS-SYNC.md) ----------

    /// <summary>
    /// Модуль текстов есть на сервере (<c>features.lyrics</c>): без него маршруты текстов не трогаются. Ответ
    /// <c>/server/info</c> перечитывается раз в 30 минут — модуль может появиться, пока приложение открыто.
    /// </summary>
    public async Task<bool> LyricsAvailableAsync(CancellationToken ct)
    {
        if (_account.ServerInfo is null || DateTime.UtcNow - _featuresAt > FeaturesMaxAge)
        {
            try
            {
                await _account.CheckAsync(_account.ServerUrl, ct).ConfigureAwait(false);
                _featuresAt = DateTime.UtcNow;
            }
            catch (ApiException e)
            {
                _log("Server info unavailable", e);
            }
        }
        return _account.ServerInfo?.Features?.Lyrics is { Version: >= 1 };
    }

    /// <summary>
    /// Цикл текстов (§3.3): сначала свои тексты, изменившиеся со снимка, — <c>PUT</c> и <c>DELETE</c>; затем свои версии с
    /// сервера после <c>lyricsRev</c>. Без своих правок и без <paramref name="force"/> сеть не трогается.
    /// </summary>
    private async Task SyncLyricsAsync(bool force, CancellationToken ct)
    {
        var sends = _store.Run(tx => LyricsSyncRules.PlanSends(tx.OwnLyrics(), tx.SyncedLyrics()));
        if (sends.Count == 0 && !force) return;
        if (!await LyricsAvailableAsync(ct).ConfigureAwait(false)) return;

        foreach (var send in sends)
        {
            switch (send)
            {
                case LyricsSend.Put put when LyricsSyncRules.TooLarge(put.Payload):
                    _store.Run(tx => tx.SetSyncedLyrics(put.VideoId, LyricsSnapshot.Rejected, put.Hash));
                    LyricsRejected?.Invoke(put.VideoId);
                    break;
                case LyricsSend.Put put:
                    try
                    {
                        var body = ToPut(put.Payload);
                        var mine = await _account.AuthorizedAsync((api, token) => api.PutLyricsAsync(token, put.VideoId, body, ct), ct).ConfigureAwait(false);
                        _store.Run(tx => tx.SetSyncedLyrics(put.VideoId, mine.Rev, put.Hash));
                    }
                    catch (ApiException e) when (e.Status is 413 or 400)
                    {
                        // Не отправлять снова, пока текст не изменится; 413 — сказать человеку
                        _log($"Lyrics {put.VideoId} rejected: {e.Code}", e);
                        _store.Run(tx => tx.SetSyncedLyrics(put.VideoId, LyricsSnapshot.Rejected, put.Hash));
                        if (e.Status == 413) LyricsRejected?.Invoke(put.VideoId);
                    }
                    break;
                case LyricsSend.Delete delete:
                    try
                    {
                        await _account.AuthorizedAsync((api, token) => api.DeleteLyricsAsync(token, delete.VideoId, ct), ct).ConfigureAwait(false);
                    }
                    catch (ApiException e) when (e.Status == 404)
                    {
                        // На сервере уже нет — снимок всё равно забыть
                    }
                    _store.Run(tx => tx.ForgetSyncedLyrics(delete.VideoId));
                    break;
                case LyricsSend.Forget forget:
                    _store.Run(tx => tx.ForgetSyncedLyrics(forget.VideoId));
                    break;
            }
        }

        var after = long.TryParse(_store.State(KeyLyricsRev), NumberStyles.Integer, CultureInfo.InvariantCulture, out var saved) ? saved : 0;
        while (true)
        {
            var request = new LyricsChangesRequest(after);
            var page = await _account.AuthorizedAsync((api, token) => api.LyricsChangesAsync(token, request, ct), ct).ConfigureAwait(false);
            var changes = _store.Run(tx =>
            {
                foreach (var item in page.Items) ApplyLyrics(tx, item);
                tx.SetState(KeyLyricsRev, page.Rev.ToString(CultureInfo.InvariantCulture));
                return tx.Changes;
            });
            _store.Library.Notify(changes);
            if (!page.More || page.Rev <= after) break;
            after = page.Rev;
        }
    }

    /// <summary>
    /// Своя версия с сервера: записать с источниками как есть и обновить снимок. Надгробие удаляет свой текст, только
    /// если он не менялся с прошлого синка; изменённый уйдёт на сервер следующей отправкой.
    /// </summary>
    private static void ApplyLyrics(SyncTx tx, MyLyrics item)
    {
        if (item.Deleted || item.Text is null)
        {
            var snapshot = tx.SyncedLyrics().GetValueOrDefault(item.VideoId);
            if (LyricsSyncRules.DeleteOnTombstone(tx.Lyrics(item.VideoId), snapshot)) tx.DeleteLyrics(item.VideoId);
            tx.ForgetSyncedLyrics(item.VideoId);
            return;
        }
        var stored = LyricsSyncRules.FromPayload(FromText(item.Text));
        // Тот же текст, найденный здесь автоматически, становится своим: дальше его правки уходят на сервер
        var local = tx.Lyrics(item.VideoId);
        if (!LyricsSyncRules.SameContent(local, stored)) tx.SaveLyrics(item.VideoId, stored);
        else if (local is { Chosen: false }) tx.SaveLyrics(item.VideoId, local with { Chosen = true });
        tx.SetSyncedLyrics(item.VideoId, item.Rev, LyricsSyncRules.Hash(LyricsSyncRules.ToPayload(stored)));
    }

    /// <summary>
    /// Текст с сервера для цепочки поиска (§3.5), когда провайдеры не нашли синхронный: своя версия (ещё не пришла синком)
    /// или общая версия другого пользователя. null — модуля нет, текста нет или нет связи.
    /// </summary>
    public async Task<(LyricsPayload Payload, bool Mine)?> LookupLyricsAsync(string videoId, CancellationToken ct)
    {
        if (_account.State is not AccountState.SignedIn || !await LyricsAvailableAsync(ct).ConfigureAwait(false)) return null;
        LyricsResponse response;
        try
        {
            response = await _account.AuthorizedAsync((api, token) => api.LyricsAsync(token, videoId, ct), ct).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.IsNetwork)
        {
            // Цепочка поиска считает это сетевой неудачей: «нет текста» не кэшируется
            throw new HttpRequestException(e.Message, e);
        }
        catch (ApiException e)
        {
            _log($"Lyrics {videoId} lookup failed: {e.Code}", e);
            return null;
        }
        if (response.Mine is { Deleted: false, Text: { } mine }) return (FromText(mine), true);
        if (response.Shared is { Text: { } shared }) return (FromText(shared), false);
        return null;
    }

    private static LyricsPut ToPut(LyricsPayload p) => new()
    {
        Plain = p.Plain,
        PlainSource = p.PlainSource,
        Synced = p.Synced,
        SyncedFormat = p.SyncedFormat,
        SyncedSource = p.SyncedSource,
        StartTimeMs = p.StartTimeMs,
        Language = p.Language,
    };

    private static LyricsPayload FromText(LyricsText t) => new(t.Plain, t.PlainSource, t.Synced, t.SyncedFormat, t.SyncedSource, t.StartTimeMs, t.Language);

    private long? LastSyncAt() => long.TryParse(_store.State(KeyLastSync), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private void SetStatus(SyncStatus status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _account.StateChanged -= OnAccountChanged;
        _store.Library.Changed -= OnLibraryChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        lock (_lock)
        {
            _session?.Cancel();
            _debounce?.Cancel();
        }
    }
}
