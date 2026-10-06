using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.Server;
using Xunit;

namespace Melogold.Tests;

/// <summary>tasks/0017: отчёт о воспроизведении, пульт и команды — без сети.</summary>
public sealed class RemoteTests
{
    private const long Start = 1_790_000_000_000;

    private sealed class FakeServer : IPlaybackServer
    {
        public long Now { get; set; } = Start;

        public bool SignedIn { get; set; } = true;

        public List<PlaybackPut> Puts { get; } = [];

        public Func<PlaybackPut, PlaybackPutResult>? OnPut { get; set; }

        public List<RemoteCommand> Commands { get; } = [];

        public Func<RemoteCommand, RemoteCommandResult>? OnCommand { get; set; }

        public PlaybackState? State { get; set; }

        public Task<PlaybackPutResult> PutAsync(PlaybackPut put, CancellationToken ct)
        {
            Puts.Add(put);
            return Task.FromResult(OnPut?.Invoke(put) ?? new PlaybackPutResult(true, Now, null, null, IsoTime.Format(Now)));
        }

        public Task<PlaybackStateResponse> StateAsync(CancellationToken ct) => Task.FromResult(new PlaybackStateResponse(State, IsoTime.Format(Now)));

        public List<RemoteDevice> Devices { get; } = [];

        public Task<RemoteDeviceList> DevicesAsync(CancellationToken ct) => Task.FromResult(new RemoteDeviceList(Devices, IsoTime.Format(Now)));

        public Task<RemoteCommandResult> CommandAsync(RemoteCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(OnCommand?.Invoke(command) ?? new RemoteCommandResult(true));
        }
    }

    private static List<Track> Tracks(int count) => Enumerable.Range(0, count).Select(i => new Track { VideoId = $"video{i:000000}", Title = $"Трек {i}" }).ToList();

    private sealed class Setup
    {
        public FakeServer Server { get; } = new();

        public LocalPlayback? Playing { get; set; }

        public List<TimeSpan> Waits { get; } = [];

        public PlaybackReporter Reporter { get; }

        public Setup()
        {
            var clock = new ServerClock(() => Server.Now);
            Reporter = new PlaybackReporter(Server, () => Playing, clock, delay: wait =>
            {
                Waits.Add(wait);
                Server.Now += (long)wait.TotalMilliseconds;
                return Task.CompletedTask;
            });
        }
    }

    [Fact]
    public async Task NothingIsReportedBeforeTheFirstPlay()
    {
        var setup = new Setup { Playing = new LocalPlayback(Tracks(3), 1, 5000, 200_000, false, 70) };
        await setup.Reporter.SendAsync();
        Assert.Empty(setup.Server.Puts);
        setup.Playing = setup.Playing with { Playing = true };
        await setup.Reporter.SendAsync();
        var put = Assert.Single(setup.Server.Puts);
        Assert.Equal((1, 1, true, 70, 3), (put.QueueVersion, put.Index, put.Playing, put.Volume, put.Queue?.Count));
        Assert.Equal(setup.Reporter.SessionId, put.SessionId);
        // Пауза после этого — сообщается
        setup.Playing = setup.Playing with { Playing = false };
        await setup.Reporter.SendAsync();
        Assert.Equal(2, setup.Server.Puts.Count);
        Assert.False(setup.Server.Puts[1].Playing);
    }

    [Fact]
    public async Task NotMoreThanOncePerSecond()
    {
        var setup = new Setup { Playing = new LocalPlayback(Tracks(3), 0, 0, 200_000, true, 70) };
        setup.Reporter.Changed();
        Assert.Single(setup.Server.Puts);
        Assert.Empty(setup.Waits);
        setup.Server.Now += 200;
        setup.Playing = setup.Playing with { Index = 1, PositionMs = 0 };
        setup.Reporter.Changed();
        Assert.Equal([TimeSpan.FromMilliseconds(800)], setup.Waits);
        Assert.Equal(2, setup.Server.Puts.Count);
        Assert.Equal(Start + 1000, IsoTime.Parse(setup.Server.Puts[1].At));
    }

    [Fact]
    public async Task QueueGoesOnceAndItsVersionGrowsWithEachChange()
    {
        var setup = new Setup { Playing = new LocalPlayback(Tracks(3), 0, 0, 200_000, true, 70) };
        await setup.Reporter.SendAsync();
        setup.Server.Now += 5000;
        // Та же очередь, позиция идёт сама — отчёта нет: другие считают её от at
        setup.Playing = setup.Playing with { PositionMs = 5000 };
        await setup.Reporter.SendAsync();
        Assert.Single(setup.Server.Puts);
        setup.Playing = setup.Playing with { Index = 2, PositionMs = 0 };
        await setup.Reporter.SendAsync();
        Assert.Null(setup.Server.Puts[1].Queue);
        Assert.Equal(1, setup.Server.Puts[1].QueueVersion);
        setup.Playing = setup.Playing with { Queue = Tracks(4) };
        await setup.Reporter.SendAsync();
        Assert.Equal((2, 4), (setup.Server.Puts[2].QueueVersion, setup.Server.Puts[2].Queue?.Count));
        // Громкость — тоже изменение
        setup.Playing = setup.Playing with { Volume = 30 };
        await setup.Reporter.SendAsync();
        Assert.Equal(30, setup.Server.Puts[3].Volume);
    }

    [Fact]
    public async Task ServerThatForgotTheQueueGetsItAgain()
    {
        var setup = new Setup { Playing = new LocalPlayback(Tracks(2), 0, 0, 200_000, true, 70) };
        await setup.Reporter.SendAsync();
        setup.Server.OnPut = put => put.Queue is null
            ? throw new ApiException(409, "playback_queue_required", null)
            : new PlaybackPutResult(true, 2, null, null, IsoTime.Format(setup.Server.Now));
        setup.Playing = setup.Playing with { Playing = false };
        await setup.Reporter.SendAsync();
        Assert.Equal(3, setup.Server.Puts.Count);
        Assert.Null(setup.Server.Puts[1].Queue);
        Assert.Equal(2, setup.Server.Puts[2].Queue?.Count);
    }

    [Fact]
    public async Task HandedOffPausesHere()
    {
        var setup = new Setup { Playing = new LocalPlayback(Tracks(2), 0, 0, 200_000, true, 70) };
        var handedOff = 0;
        setup.Reporter.HandedOff += () => handedOff++;
        setup.Server.OnPut = _ => new PlaybackPutResult(false, null, "handed_off", null, IsoTime.Format(setup.Server.Now));
        await setup.Reporter.SendAsync();
        Assert.Equal(1, handedOff);

        // «Слушать здесь» с этого устройства: следующий отчёт несёт handoffFrom, потом — нет
        setup.Server.OnPut = null;
        setup.Reporter.TakeOverFrom("phone", "session-1");
        var put = setup.Server.Puts[^1];
        Assert.Equal(new PlaybackHandoffInput("phone", "session-1"), put.HandoffFrom);
        setup.Playing = setup.Playing with { Playing = false };
        await setup.Reporter.SendAsync();
        Assert.Null(setup.Server.Puts[^1].HandoffFrom);
    }

    /// <summary>Повторы после сбоя идут цепочкой асинхронных вызовов: ждём, пока цепочка дойдёт до нужного числа отчётов.</summary>
    private static async Task WaitForPuts(Setup setup, int count)
    {
        for (var i = 0; i < 400 && setup.Server.Puts.Count < count; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ServerFailuresBackOffTwoFourEightSeconds()
    {
        var setup = new Setup { Playing = new LocalPlayback(Tracks(2), 0, 0, 200_000, true, 70) };
        var failures = 3;
        setup.Server.OnPut = _ => failures-- > 0
            ? throw new ApiException(503, "server_busy", null)
            : new PlaybackPutResult(true, 1, null, null, IsoTime.Format(setup.Server.Now));
        setup.Reporter.Changed();
        await WaitForPuts(setup, 4);
        // Первая попытка сразу, повторы — через 2, 4 и 8 с, четвёртая прошла
        Assert.Equal([2000.0, 4000.0, 8000.0], setup.Waits.Select(w => w.TotalMilliseconds));
        Assert.Equal(4, setup.Server.Puts.Count);
        // После удачи — снова не чаще раза в секунду
        setup.Playing = setup.Playing with { Playing = false };
        setup.Server.Now += 100;
        setup.Reporter.Changed();
        await WaitForPuts(setup, 5);
        Assert.Equal(900, setup.Waits[^1].TotalMilliseconds);
    }

    [Fact]
    public void WindowKeepsTheCurrentTrackInside()
    {
        var queue = Tracks(500);
        var (window, index) = PlaybackReporter.Window(queue, 300);
        Assert.Equal(200, window.Count);
        Assert.Equal("video000300", window[index].VideoId);
        Assert.Equal(50, index);
        Assert.Equal(("video000000", 10), (PlaybackReporter.Window(queue, 10).Window[0].VideoId, PlaybackReporter.Window(queue, 10).Index));
        Assert.Equal("video000499", PlaybackReporter.Window(queue, 499).Window[^1].VideoId);
    }

    private static PlaybackSummary Summary(string device, bool playing, long positionMs, long at, long? durationMs = 200_000) =>
        new(1, device, "MacBook Air", "s1", 1, 0, 3, new TrackDto("video000000", "Трек", "Кино", [], null, null, durationMs, null, null, false, null, false),
            positionMs, durationMs, playing, IsoTime.Format(at), IsoTime.Format(at), null, 50);

    [Fact]
    public void RemotePositionCountsFromAt()
    {
        Assert.Equal(15_000, RemoteController.PositionAt(Summary("mac", true, 10_000, Start), Start + 5000));
        Assert.Equal(10_000, RemoteController.PositionAt(Summary("mac", false, 10_000, Start), Start + 5000));
        Assert.Equal(200_000, RemoteController.PositionAt(Summary("mac", true, 199_000, Start), Start + 60_000));
    }

    [Fact]
    public void RemoteDurationFallsBackToTheTrack()
    {
        var bare = Summary("phone", true, 10_000, Start, durationMs: null);
        // Телефон длительность не сообщил, а трек её знает — из трека; на него же опирается и позиция
        var withTrack = bare with { Track = bare.Track! with { DurationMs = 261_000 } };
        Assert.Equal(261_000, RemoteController.DurationOf(withTrack));
        Assert.Equal(261_000, RemoteController.PositionAt(withTrack with { PositionMs = 259_000 }, Start + 60_000));
        // Только «4:21» — из текста
        Assert.Equal(261_000, RemoteController.DurationOf(bare with { DurationMs = 0, Track = bare.Track! with { DurationText = "4:21" } }));
        // Нигде нет — неизвестна: позиция не обрезается
        Assert.Null(RemoteController.DurationOf(bare));
        Assert.Null(RemoteController.DurationOf(null));
        Assert.Equal(70_000, RemoteController.PositionAt(bare, Start + 60_000));
        // Сообщённая длительность важнее
        Assert.Equal(200_000, RemoteController.DurationOf(Summary("phone", true, 0, Start)));
    }

    private static RemoteDevice Mac(PlaybackSummary? playing = null) => new("mac", "MacBook Air", "macos", true, true, playing, 50);

    [Fact]
    public async Task RemoteShowsTheResultAtOnceAndFollowsUpdates()
    {
        var server = new FakeServer();
        var remote = new RemoteController(server, new ServerClock(() => server.Now));
        await remote.ConnectAsync(Mac(Summary("mac", true, 10_000, Start)));
        // Состояния на сервере нет — там ничего не играет
        Assert.Null(remote.State);
        server.State = new PlaybackState(2, "mac", "MacBook Air", "s1", 1, 0, 10_000, 200_000, true, IsoTime.Format(Start), IsoTime.Format(Start),
            [new TrackDto("video000000", "Трек", null, [], null, null, 200_000, null, null, false, null, false)], null, 40);
        await remote.RefreshAsync();
        Assert.Equal((true, 40), (remote.State?.Playing, remote.State?.Volume));

        server.Now += 2000;
        Assert.True(await remote.ToggleAsync());
        Assert.False(remote.State?.Playing);
        Assert.Equal(12_000, remote.Position);
        Assert.Equal(("toggle", "mac"), (server.Commands[^1].Action, server.Commands[^1].TargetDeviceId));
        await remote.SetVolumeAsync(120);
        Assert.Equal(100, server.Commands[^1].Volume);
        Assert.Equal(100, remote.State?.Volume);

        remote.OnUpdated(new PlaybackUpdatedPayload(3, false, Summary("mac", true, 30_000, server.Now)));
        Assert.Equal(30_000, remote.Position);
        remote.OnUpdated(new PlaybackUpdatedPayload(4, false, Summary("phone", true, 0, server.Now)));
        Assert.Null(remote.State);
    }

    [Theory]
    [InlineData("device_offline", RemoteFailure.Offline)]
    [InlineData("remote_control_disabled", RemoteFailure.Disabled)]
    public async Task OfflineOrDisabledTargetTurnsTheRemoteOff(string code, RemoteFailure failure)
    {
        var server = new FakeServer { OnCommand = _ => throw new ApiException(409, code, null) };
        var remote = new RemoteController(server, new ServerClock(() => server.Now));
        (RemoteFailure, string)? failed = null;
        remote.Failed += (why, name) => failed = (why, name);
        await remote.ConnectAsync(Mac());
        Assert.False(await remote.NextAsync());
        Assert.Null(remote.Target);
        Assert.Equal((failure, "MacBook Air"), failed);
    }

    [Fact]
    public async Task WatchIsNotARemoteTarget()
    {
        var server = new FakeServer();
        server.Devices.Add(Mac());
        server.Devices.Add(new RemoteDevice("watch", "Apple Watch", "watchos", false, false, null, null));
        var remote = new RemoteController(server, new ServerClock(() => server.Now));
        Assert.Equal(["MacBook Air"], (await remote.DevicesAsync()).Select(d => d.Name));
    }

    [Fact]
    public async Task PlayQueueSendsTheListFromTheTrack()
    {
        var server = new FakeServer();
        var remote = new RemoteController(server, new ServerClock(() => server.Now));
        await remote.ConnectAsync(Mac());
        await remote.PlayQueueAsync(Tracks(250), 120);
        var command = server.Commands[^1];
        Assert.Equal("play_queue", command.Action);
        Assert.Equal(200, command.Queue?.Count);
        Assert.Equal("video000120", command.Queue![command.Index!.Value].VideoId);
    }

    private sealed class FakePlayer : IRemotePlayer
    {
        public List<string> Done { get; } = [];

        public void Play() => Done.Add("play");

        public void Pause() => Done.Add("pause");

        public void Toggle() => Done.Add("toggle");

        public void Next() => Done.Add("next");

        public void Previous() => Done.Add("previous");

        public void Seek(long positionMs) => Done.Add($"seek {positionMs}");

        public void SetVolume(int volume) => Done.Add($"volume {volume}");

        public void PlayQueue(IReadOnlyList<Track> tracks, int index) => Done.Add($"play_queue {tracks.Count} {tracks[index].VideoId}");

        public void Stop() => Done.Add("stop");
    }

    private static PlaybackCommandPayload Command(string action, long? position = null, int? volume = null, IReadOnlyList<TrackDto>? queue = null, int? index = null) =>
        new(Guid.NewGuid().ToString(), "phone", "Pixel 7 Pro", action, position, volume, queue, index);

    [Fact]
    public void EveryCommandReachesThePlayerAndTheNoticeIsRare()
    {
        var player = new FakePlayer();
        var now = Start;
        var notices = new List<string>();
        var handler = new RemoteCommandHandler(player, notices.Add, () => now);
        var queue = new List<TrackDto>
        {
            new("video000000", "Раз", null, [], null, null, null, null, null, false, null, false),
            new("video000001", "", null, [], null, null, null, null, null, false, null, true),
        };
        Assert.True(handler.Execute(Command("play")));
        Assert.True(handler.Execute(Command("pause")));
        Assert.True(handler.Execute(Command("toggle")));
        Assert.True(handler.Execute(Command("next")));
        Assert.True(handler.Execute(Command("previous")));
        Assert.True(handler.Execute(Command("seek", position: 42_000)));
        Assert.True(handler.Execute(Command("volume", volume: 150)));
        Assert.True(handler.Execute(Command("play_queue", queue: queue, index: 1)));
        Assert.True(handler.Execute(Command("stop")));
        // Без своих полей и незнакомое — ничего
        Assert.False(handler.Execute(Command("seek")));
        Assert.False(handler.Execute(Command("volume")));
        Assert.False(handler.Execute(Command("dance")));
        Assert.Equal(["play", "pause", "toggle", "next", "previous", "seek 42000", "volume 100", "play_queue 2 video000001", "stop"], player.Done);
        Assert.Equal(["Pixel 7 Pro"], notices);
        now += 30_000;
        handler.Execute(Command("next"));
        Assert.Equal(2, notices.Count);
    }

    [Fact]
    public void TakenFromHereOnlyByThisSessionAndRecently()
    {
        var server = new FakeServer();
        var reporter = new PlaybackReporter(server, () => null, new ServerClock(() => server.Now));
        PlaybackSummary Taken(string device, string session, long at) => Summary("phone", true, 0, server.Now) with { HandoffFrom = new PlaybackHandoff(device, session, IsoTime.Format(at)) };
        Assert.True(reporter.IsTakenFromHere(Taken("pc", reporter.SessionId, server.Now - 1000), "pc"));
        Assert.False(reporter.IsTakenFromHere(Taken("pc", "other-session", server.Now), "pc"));
        Assert.False(reporter.IsTakenFromHere(Taken("mac", reporter.SessionId, server.Now), "pc"));
        Assert.False(reporter.IsTakenFromHere(Taken("pc", reporter.SessionId, server.Now - 6 * 60_000), "pc"));
        Assert.False(reporter.IsTakenFromHere(Summary("phone", true, 0, server.Now), "pc"));
    }
}
