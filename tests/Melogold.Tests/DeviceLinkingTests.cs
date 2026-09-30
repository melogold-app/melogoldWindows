using System.Collections.Concurrent;
using Melogold.Server;
using Xunit;

namespace Melogold.Tests;

/// <summary>tasks/0014: вход по коду — логика без окна на подделке сервера.</summary>
public sealed class DeviceLinkingTests
{
    private static readonly string Future = DateTimeOffset.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Сервер-подделка: ответы опроса по очереди (ответ или исключение).</summary>
    private sealed class FakeServer : INewDeviceLinkPort, IInvitePort
    {
        public readonly ConcurrentQueue<Func<LinkPollResponse>> Polls = new();
        public readonly ConcurrentQueue<Func<LinkDetails>> Details = new();
        public Func<LinkCreated>? Request;
        public Func<LinkClaimed>? Claim;
        public int Cancels;
        public int Gets;

        public Task<LinkCreated> RequestAsync(CancellationToken ct) => Task.FromResult(Request!());

        public Task<LinkClaimed> ClaimAsync(string userCode, CancellationToken ct) => Task.FromResult(Claim!());

        public async Task<LinkPollResponse> PollAsync(string pollSecret, string knownStatus, CancellationToken ct)
        {
            Func<LinkPollResponse>? next;
            while (!Polls.TryDequeue(out next)) await Task.Delay(5, ct);
            return next();
        }

        Task INewDeviceLinkPort.CancelAsync(string pollSecret)
        {
            Interlocked.Increment(ref Cancels);
            return Task.CompletedTask;
        }

        public Task<LinkCreated> CreateAsync(CancellationToken ct) => Task.FromResult(Created("invite", null));

        public Task<LinkDetails> GetAsync(string linkId, CancellationToken ct)
        {
            Interlocked.Increment(ref Gets);
            return Task.FromResult(Details.TryDequeue(out var next) ? next() : Link("pending", []));
        }

        Task IInvitePort.CancelAsync(string linkId)
        {
            Interlocked.Increment(ref Cancels);
            return Task.CompletedTask;
        }
    }

    private static LinkCreated Created(string mode, string? secret) => new("link-1", mode, "server", "token", "K7QX-M2PD", secret, Future, 25);

    private static LinkPollResponse Poll(string status, string? verify = null) =>
        new("link-1", status, Future, verify is null ? null : new LinkAccount("maxim"), verify is null ? null : new LinkApprover("MacBook Air", "macos"), verify, null);

    private static LinkDetails Link(string status, IReadOnlyList<string> choices) =>
        new("link-1", "invite", status, Future, Future, choices.Count > 0 ? new LinkDeviceInfo("Pixel 8", "android", "16", null, null, false) : null, true, choices);

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public async Task RequestModeShowsCodeThenNumberThenSignsIn()
    {
        var server = new FakeServer { Request = () => Created("request", "mgps_secret") };
        var linker = new NewDeviceLinker(server, TimeSpan.FromMilliseconds(10));
        var states = new ConcurrentQueue<NewDeviceLinkState>();
        linker.StateChanged += states.Enqueue;
        var run = linker.ShowCodeAsync();
        await Until(() => linker.State is NewDeviceLinkState.ShowingCode { UserCode: "K7QX-M2PD" });

        // Сеть пропала: код остаётся на экране с «пробуем снова»
        server.Polls.Enqueue(() => throw new ApiException(0, "network", "offline"));
        await Until(() => linker.State is NewDeviceLinkState.ShowingCode { Reconnecting: true });
        server.Polls.Enqueue(() => Poll("pending"));
        await Until(() => linker.State is NewDeviceLinkState.ShowingCode { Reconnecting: false });

        server.Polls.Enqueue(() => Poll("claimed", "47"));
        await Until(() => linker.State is NewDeviceLinkState.Verify { VerifyCode: "47", Login: "maxim", ApproverName: "MacBook Air" });
        server.Polls.Enqueue(() => Poll("completed"));
        await run;
        Assert.IsType<NewDeviceLinkState.SignedIn>(linker.State);
        // Сессия взята — отменять нечего
        linker.Cancel();
        Assert.Equal(0, server.Cancels);
    }

    [Fact]
    public async Task InviteModeShowsNumberAtOnce()
    {
        var server = new FakeServer
        {
            Claim = () => new LinkClaimed("link-1", "claimed", "mgps_secret", new LinkAccount("maxim"), new LinkApprover("Ноутбук", "windows"), "85", Future, 25),
        };
        var linker = new NewDeviceLinker(server, TimeSpan.FromMilliseconds(10));
        var run = linker.ClaimAsync("K7QX-M2PD");
        await Until(() => linker.State is NewDeviceLinkState.Verify { VerifyCode: "85" });
        server.Polls.Enqueue(() => Poll("completed"));
        await run;
        Assert.IsType<NewDeviceLinkState.SignedIn>(linker.State);
    }

    [Theory]
    [InlineData(403, "link_denied", LinkFailure.Denied)]
    [InlineData(410, "link_expired", LinkFailure.Expired)]
    [InlineData(410, "link_cancelled", LinkFailure.Cancelled)]
    [InlineData(409, "device_limit_reached", LinkFailure.DeviceLimit)]
    public async Task PollFailuresEndTheLink(int status, string code, LinkFailure failure)
    {
        var server = new FakeServer { Request = () => Created("request", "mgps_secret") };
        var linker = new NewDeviceLinker(server, TimeSpan.FromMilliseconds(10));
        var run = linker.ShowCodeAsync();
        server.Polls.Enqueue(() => throw new ApiException(status, code, code));
        await run;
        Assert.Equal(new NewDeviceLinkState.Failed(failure, Started: true), linker.State);
        // Привязки уже нет — на сервер ничего не идёт
        linker.Cancel();
        Assert.Equal(0, server.Cancels);
    }

    [Theory]
    [InlineData(404, "link_not_found", LinkFailure.NotFound)]
    [InlineData(409, "link_already_claimed", LinkFailure.AlreadyClaimed)]
    [InlineData(409, "link_wrong_mode", LinkFailure.WrongMode)]
    [InlineData(410, "link_expired", LinkFailure.Expired)]
    [InlineData(429, "rate_limited", LinkFailure.Throttled)]
    [InlineData(0, "network", LinkFailure.Network)]
    [InlineData(500, "internal", LinkFailure.Unknown)]
    public async Task ClaimFailuresSayWhy(int status, string code, LinkFailure failure)
    {
        var server = new FakeServer { Claim = () => throw new ApiException(status, code, code) };
        var linker = new NewDeviceLinker(server);
        await linker.ClaimAsync("K7QX-M2PD");
        Assert.Equal(new NewDeviceLinkState.Failed(failure, Started: false), linker.State);
    }

    [Fact]
    public async Task CancelReachesTheServerOnce()
    {
        var server = new FakeServer { Request = () => Created("request", "mgps_secret") };
        var linker = new NewDeviceLinker(server);
        _ = linker.ShowCodeAsync();
        await Until(() => linker.State is NewDeviceLinkState.ShowingCode);
        linker.Cancel();
        linker.Cancel();
        await Until(() => server.Cancels == 1);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, server.Cancels);
        Assert.IsType<NewDeviceLinkState.Idle>(linker.State);
    }

    [Fact]
    public async Task InviteWaitsForTheNewDeviceAndNudgeReadsAtOnce()
    {
        var server = new FakeServer();
        // Опрос раз в час: читать сразу заставляет только живое событие
        var linker = new InviteLinker(server, TimeSpan.FromHours(1));
        _ = linker.StartAsync();
        await Until(() => linker.State is InviteState.Waiting { UserCode: "K7QX-M2PD" });
        server.Details.Enqueue(() => Link("claimed", ["12", "47", "85"]));
        linker.Nudge("другая-привязка");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, server.Gets);
        linker.Nudge("link-1");
        await Until(() => linker.State is InviteState.Claimed { Link.VerifyChoices.Count: 3 });

        // Решено здесь — не отменять
        linker.Release();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, server.Cancels);
    }

    [Fact]
    public async Task InviteEndsWhenTheCodeExpires()
    {
        var server = new FakeServer();
        var linker = new InviteLinker(server, TimeSpan.FromMilliseconds(10));
        server.Details.Enqueue(() => Link("expired", []));
        _ = linker.StartAsync();
        await Until(() => linker.State is InviteState.Failed { Failure: LinkFailure.Expired, Started: true });
    }

    [Fact]
    public async Task InviteCancelReachesTheServer()
    {
        var server = new FakeServer();
        var linker = new InviteLinker(server, TimeSpan.FromHours(1));
        _ = linker.StartAsync();
        await Until(() => linker.State is InviteState.Waiting);
        linker.Cancel();
        await Until(() => server.Cancels == 1);
    }

    [Theory]
    [InlineData(272_000, "4:32")]
    [InlineData(271_500, "4:32")]
    [InlineData(1, "0:01")]
    [InlineData(0, "0:00")]
    [InlineData(-5_000, "0:00")]
    public void Countdown(long millis, string text) => Assert.Equal(text, LinkRules.CountdownText(millis));

    [Fact]
    public void CodeIsSpokenCharacterByCharacter() => Assert.Equal("K, 7, Q, X, M, 2, P, D", LinkRules.SpokenCode("K7QX-M2PD"));
}
