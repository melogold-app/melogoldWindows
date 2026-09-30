using System.Globalization;
using Melogold.Core.Domain;

namespace Melogold.Server;

// Вход по коду (API §4.6, tasks/0014; Android DeviceLinking.kt). Две стороны, два режима:
//   request — НОВОЕ устройство показывает код, устройство, где уже вошли, вводит его («Добавить устройство»);
//   invite  — устройство, где уже вошли, показывает код, новое вводит его.
// NewDeviceLinker — новое устройство (оба режима кончаются одним длинным опросом, который приносит сессию);
// InviteLinker — устройство, где уже вошли, которое показывает код и ждёт, пока его введут.

/// <summary>Почему привязка ни к чему не привела — в словах экранов (API §2.2).</summary>
public enum LinkFailure
{
    /// <summary>Другое устройство отказало или там выбрали неверное число.</summary>
    Denied,
    Expired,
    Cancelled,
    DeviceLimit,
    NotFound,
    AlreadyClaimed,
    WrongMode,
    Throttled,
    Network,
    Unknown,
}

public static class LinkRules
{
    public static LinkFailure FailureOf(Exception error) => error is ApiException api
        ? api.Code switch
        {
            "link_denied" or "link_verify_mismatch" => LinkFailure.Denied,
            "link_expired" => LinkFailure.Expired,
            "link_cancelled" => LinkFailure.Cancelled,
            "link_not_found" => LinkFailure.NotFound,
            "link_already_claimed" => LinkFailure.AlreadyClaimed,
            "link_wrong_mode" => LinkFailure.WrongMode,
            "device_limit_reached" => LinkFailure.DeviceLimit,
            "rate_limited" or "login_throttled" => LinkFailure.Throttled,
            _ => api.IsNetwork ? LinkFailure.Network : LinkFailure.Unknown,
        }
        : error is HttpRequestException ? LinkFailure.Network : LinkFailure.Unknown;

    /// <summary>Статус привязки, которая кончилась, как причина; null — ещё идёт или кончилась хорошо.</summary>
    public static LinkFailure? StatusFailure(string status) => status switch
    {
        "denied" => LinkFailure.Denied,
        "expired" => LinkFailure.Expired,
        "cancelled" => LinkFailure.Cancelled,
        _ => null,
    };

    /// <summary>Сколько ещё действует код: «4:32»; не ниже «0:00».</summary>
    public static string CountdownText(long millis)
    {
        var seconds = (Math.Max(0, millis) + 999) / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary><c>K7QX-M2PD</c> по знакам для экранного диктора: «K, 7, Q, X, M, 2, P, D».</summary>
    public static string SpokenCode(string code) => string.Join(", ", code.Where(c => c != '-'));

    /// <summary>Срок кода в мс эпохи; нечитаемый — 5 минут от сейчас.</summary>
    internal static long ExpiryOf(string iso) => IsoTime.TryParse(iso) ?? IsoTime.NowMs() + 5 * 60_000;
}

// ---------- Новое устройство ----------

/// <summary>Где сейчас вход по коду этого (нового) устройства.</summary>
public abstract record NewDeviceLinkState
{
    /// <summary>Ничего не просили или бросили.</summary>
    public sealed record Idle : NewDeviceLinkState;

    /// <summary>Сервер спрашивают о коде (request) или о введённом коде (invite).</summary>
    public sealed record Starting : NewDeviceLinkState;

    /// <summary>Режим request: код этого устройства, ждём, пока его введут. <paramref name="Reconnecting"/> — нет связи.</summary>
    public sealed record ShowingCode(string UserCode, long ExpiresAt, bool Reconnecting = false) : NewDeviceLinkState;

    /// <summary>Код введён на другом устройстве: число, которое нужно выбрать там, для какого аккаунта и на каком устройстве.</summary>
    public sealed record Verify(string VerifyCode, string Login, string ApproverName, string ApproverPlatform, long ExpiresAt, bool Reconnecting = false) : NewDeviceLinkState;

    /// <summary>Сессия взята — как после входа по паролю.</summary>
    public sealed record SignedIn : NewDeviceLinkState;

    /// <summary>Привязка кончилась. <paramref name="Started"/> false — не началась (кода нет, введённый не приняли).</summary>
    public sealed record Failed(LinkFailure Failure, bool Started) : NewDeviceLinkState;
}

/// <summary>Что нужно <see cref="NewDeviceLinker"/> от сервера: <see cref="AccountLinkPort"/> в приложении, подделка в тестах.</summary>
public interface INewDeviceLinkPort
{
    Task<LinkCreated> RequestAsync(CancellationToken ct);

    Task<LinkClaimed> ClaimAsync(string userCode, CancellationToken ct);

    /// <summary>На <c>completed</c> сессия уже взята.</summary>
    Task<LinkPollResponse> PollAsync(string pollSecret, string knownStatus, CancellationToken ct);

    Task CancelAsync(string pollSecret);
}

/// <summary>Что нужно <see cref="InviteLinker"/> от сервера.</summary>
public interface IInvitePort
{
    Task<LinkCreated> CreateAsync(CancellationToken ct);

    Task<LinkDetails> GetAsync(string linkId, CancellationToken ct);

    Task CancelAsync(string linkId);
}

public sealed class AccountLinkPort(AccountService account) : INewDeviceLinkPort, IInvitePort
{
    public Task<LinkCreated> RequestAsync(CancellationToken ct) => account.RequestLinkAsync(ct);

    public Task<LinkClaimed> ClaimAsync(string userCode, CancellationToken ct) => account.ClaimLinkAsync(userCode, ct);

    public Task<LinkPollResponse> PollAsync(string pollSecret, string knownStatus, CancellationToken ct) => account.PollLinkAsync(pollSecret, knownStatus, ct);

    Task INewDeviceLinkPort.CancelAsync(string pollSecret) => account.CancelLinkRequestAsync(pollSecret);

    public Task<LinkCreated> CreateAsync(CancellationToken ct) => account.CreateInviteAsync(ct);

    public Task<LinkDetails> GetAsync(string linkId, CancellationToken ct) => account.GetLinkAsync(linkId, ct);

    Task IInvitePort.CancelAsync(string linkId) => account.CancelInviteAsync(linkId);
}

/// <summary>
/// Вход по коду нового устройства: <see cref="ShowCodeAsync"/> просит код и ждёт, пока его одобрят (режим request);
/// <see cref="ClaimAsync"/> вводит код, который показывает устройство, где уже вошли (invite). Дальше — число, которое
/// нужно выбрать там (<see cref="NewDeviceLinkState.Verify"/>), и сессия (<see cref="NewDeviceLinkState.SignedIn"/>).
/// Сеть, 429 и 5xx в опросе — не ошибка: код остаётся, повтор через 3 с. <see cref="Cancel"/> бросает и на сервере —
/// один раз. <see cref="StateChanged"/> приходит из фонового потока.
/// </summary>
public sealed class NewDeviceLinker(INewDeviceLinkPort port, TimeSpan? retryDelay = null)
{
    private readonly TimeSpan _retry = retryDelay ?? TimeSpan.FromSeconds(3);
    private readonly Lock _lock = new();
    private CancellationTokenSource? _job;
    private string? _pollSecret;

    public NewDeviceLinkState State { get; private set; } = new NewDeviceLinkState.Idle();

    public event Action<NewDeviceLinkState>? StateChanged;

    /// <summary>Режим request: код этого устройства.</summary>
    public Task ShowCodeAsync() => Start(async ct =>
    {
        LinkCreated created;
        try
        {
            created = await port.RequestAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Set(new NewDeviceLinkState.Failed(LinkRules.FailureOf(e), Started: false));
            return;
        }
        if (created.PollSecret is not { } secret)
        {
            Set(new NewDeviceLinkState.Failed(LinkFailure.Unknown, Started: false));
            return;
        }
        lock (_lock) _pollSecret = secret;
        Set(new NewDeviceLinkState.ShowingCode(created.UserCode, LinkRules.ExpiryOf(created.ExpiresAt)));
        await FollowAsync(secret, "pending", ct).ConfigureAwait(false);
    });

    /// <summary>Режим invite: <paramref name="userCode"/> (нормализованный, <c>K7QX-M2PD</c>) показывает устройство, где уже вошли.</summary>
    public Task ClaimAsync(string userCode) => Start(async ct =>
    {
        LinkClaimed claimed;
        try
        {
            claimed = await port.ClaimAsync(userCode, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Set(new NewDeviceLinkState.Failed(LinkRules.FailureOf(e), Started: false));
            return;
        }
        lock (_lock) _pollSecret = claimed.PollSecret;
        Set(new NewDeviceLinkState.Verify(claimed.VerifyCode, claimed.Account.Login, claimed.ApproverDevice.Name, claimed.ApproverDevice.Platform,
            LinkRules.ExpiryOf(claimed.ExpiresAt)));
        await FollowAsync(claimed.PollSecret, "claimed", ct).ConfigureAwait(false);
    });

    /// <summary>Бросить привязку здесь и на сервере; назад к <see cref="NewDeviceLinkState.Idle"/>, если сессия ещё не взята.</summary>
    public void Cancel()
    {
        string? secret;
        lock (_lock)
        {
            _job?.Cancel();
            _job = null;
            secret = _pollSecret;
            _pollSecret = null;
        }
        if (State is not NewDeviceLinkState.SignedIn) Set(new NewDeviceLinkState.Idle());
        // Ответа ждать не нужно (204, в том числе если всё уже кончено)
        if (secret is not null) _ = Task.Run(async () =>
        {
            try
            {
                await port.CancelAsync(secret).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or OperationCanceledException)
            {
            }
        });
    }

    private Task Start(Func<CancellationToken, Task> work)
    {
        Cancel();
        var job = new CancellationTokenSource();
        lock (_lock) _job = job;
        Set(new NewDeviceLinkState.Starting());
        return Task.Run(async () =>
        {
            try
            {
                await work(job.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.IsCancellationRequested)
            {
            }
        });
    }

    /// <summary>Длинный опрос: ответ сразу, если статус сдвинулся, иначе через 25 с.</summary>
    private async Task FollowAsync(string secret, string knownStatus, CancellationToken ct)
    {
        var known = knownStatus;
        while (!ct.IsCancellationRequested)
        {
            LinkPollResponse answer;
            try
            {
                answer = await port.PollAsync(secret, known, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (e is ApiException { IsTransient: true } or HttpRequestException)
                {
                    Reconnecting(true);
                    await Task.Delay(_retry, ct).ConfigureAwait(false);
                    continue;
                }
                // Отказ, истёк, отменили, лимит устройств: привязки больше нет, отменять нечего
                lock (_lock) _pollSecret = null;
                Set(new NewDeviceLinkState.Failed(LinkRules.FailureOf(e), Started: true));
                return;
            }
            switch (answer.Status)
            {
                case "completed":
                    lock (_lock) _pollSecret = null;
                    Set(new NewDeviceLinkState.SignedIn());
                    return;
                case "claimed":
                    known = "claimed";
                    if (answer is { VerifyCode: { } code, Account: { } account, ApproverDevice: { } approver })
                        Set(new NewDeviceLinkState.Verify(code, account.Login, approver.Name, approver.Platform, LinkRules.ExpiryOf(answer.ExpiresAt)));
                    else
                    {
                        lock (_lock) _pollSecret = null;
                        Set(new NewDeviceLinkState.Failed(LinkFailure.Unknown, Started: true));
                        return;
                    }
                    break;
                default:
                    Reconnecting(false);
                    break;
            }
        }
    }

    /// <summary>Код (или число) на экране: «нет связи» или снова свежий.</summary>
    private void Reconnecting(bool value)
    {
        switch (State)
        {
            case NewDeviceLinkState.ShowingCode code when code.Reconnecting != value:
                Set(code with { Reconnecting = value });
                break;
            case NewDeviceLinkState.Verify verify when verify.Reconnecting != value:
                Set(verify with { Reconnecting = value });
                break;
        }
    }

    private void Set(NewDeviceLinkState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}

// ---------- Устройство, где уже вошли, показывает код ----------

public abstract record InviteState
{
    public sealed record Idle : InviteState;

    public sealed record Starting : InviteState;

    /// <summary>Код, который вводят на новом устройстве.</summary>
    public sealed record Waiting(string UserCode, long ExpiresAt) : InviteState;

    /// <summary>Новое устройство ввело код: его карточка и три числа (одобрение).</summary>
    public sealed record Claimed(LinkDetails Link) : InviteState;

    /// <summary>Приглашение кончилось. <paramref name="Started"/> false — кода не получилось.</summary>
    public sealed record Failed(LinkFailure Failure, bool Started) : InviteState;
}

/// <summary>
/// «Показать код для нового устройства» (режим invite): создаёт приглашение и следит за ним, пока его не заберёт новое
/// устройство — по живому событию <c>link.updated</c> (<see cref="Nudge"/>) сразу, и в любом случае раз в 3 с.
/// Следит и после <c>claimed</c>: если новое устройство бросило вход или код истёк, скажет. <see cref="Release"/> —
/// перестать без отмены (после решения), <see cref="Cancel"/> — отменить на сервере.
/// </summary>
public sealed class InviteLinker(IInvitePort port, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(3);
    private readonly Lock _lock = new();
    private CancellationTokenSource? _job;
    private string? _linkId;
    private TaskCompletionSource _nudge = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InviteState State { get; private set; } = new InviteState.Idle();

    public event Action<InviteState>? StateChanged;

    public Task StartAsync()
    {
        Cancel();
        var job = new CancellationTokenSource();
        lock (_lock) _job = job;
        Set(new InviteState.Starting());
        return Task.Run(async () =>
        {
            try
            {
                await FollowAsync(job.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.IsCancellationRequested)
            {
            }
        });
    }

    /// <summary>Живое событие про привязку с этим id: прочитать её сейчас.</summary>
    public void Nudge(string linkId)
    {
        lock (_lock)
        {
            if (linkId != _linkId) return;
            _nudge.TrySetResult();
        }
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        LinkCreated created;
        try
        {
            created = await port.CreateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Set(new InviteState.Failed(LinkRules.FailureOf(e), Started: false));
            return;
        }
        lock (_lock) _linkId = created.LinkId;
        Set(new InviteState.Waiting(created.UserCode, LinkRules.ExpiryOf(created.ExpiresAt)));
        while (true)
        {
            Task nudge;
            lock (_lock) nudge = _nudge.Task;
            await Task.WhenAny(Task.Delay(_poll, ct), nudge).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_nudge.Task.IsCompleted) _nudge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (await RefreshAsync(created.LinkId, ct).ConfigureAwait(false)) return;
        }
    }

    /// <summary>Прочитать приглашение; true — следить больше не за чем.</summary>
    private async Task<bool> RefreshAsync(string linkId, CancellationToken ct)
    {
        LinkDetails details;
        try
        {
            details = await port.GetAsync(linkId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (e is ApiException { IsTransient: true } or HttpRequestException) return false;
            lock (_lock) _linkId = null;
            Set(new InviteState.Failed(LinkRules.FailureOf(e), Started: true));
            return true;
        }
        if (LinkRules.StatusFailure(details.Status) is { } failure)
        {
            lock (_lock) _linkId = null;
            Set(new InviteState.Failed(failure, Started: true));
            return true;
        }
        switch (details.Status)
        {
            case "claimed" when details.VerifyChoices.Count > 0:
                if (State is not InviteState.Claimed) Set(new InviteState.Claimed(details));
                break;
            // Решено здесь и завершено там: экран своё сказал
            case "approved" or "completed":
                return true;
        }
        return false;
    }

    /// <summary>Перестать следить без отмены: привязка решена (одобрена или отклонена).</summary>
    public void Release()
    {
        lock (_lock)
        {
            _job?.Cancel();
            _job = null;
            _linkId = null;
        }
        Set(new InviteState.Idle());
    }

    /// <summary>Отменить приглашение здесь и на сервере.</summary>
    public void Cancel()
    {
        string? id;
        lock (_lock) id = _linkId;
        Release();
        if (id is not null) _ = Task.Run(async () =>
        {
            try
            {
                await port.CancelAsync(id).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or OperationCanceledException)
            {
            }
        });
    }

    private void Set(InviteState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
