using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace Melogold.App.Services;

/// <summary>
/// Плашка над панелью плеера (§5.5): одна на окно, новая сразу заменяет старую. Разрушающее действие ждёт 5 секунд с
/// «Отменить» и выполняется, когда плашка уходит — или сразу, когда её вытесняет следующая.
/// </summary>
public sealed partial class Snackbar : ObservableObject
{
    private readonly DispatcherQueueTimer _timer;
    private Action? _action;
    private Action? _commit;

    public Snackbar()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Dismiss(commit: true);
    }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial string? ActionText { get; set; }

    /// <summary>Сообщение, при желании с действием («Отменить» очереди).</summary>
    public void Show(string message, string? actionText = null, Action? action = null, TimeSpan? duration = null)
    {
        Dismiss(commit: true);
        Message = message;
        ActionText = actionText;
        _action = action;
        _commit = null;
        IsOpen = true;
        _timer.Interval = duration ?? TimeSpan.FromSeconds(actionText is null ? 4 : 5);
        _timer.Start();
    }

    /// <summary>
    /// Отложенное действие: <paramref name="commit"/> выполнится через 5 секунд или когда появится другая плашка;
    /// «Отменить» вызывает <paramref name="undo"/> и отменяет коммит.
    /// </summary>
    public void ShowUndoable(string message, Action commit, Action? undo = null)
    {
        Dismiss(commit: true);
        Message = message;
        ActionText = Loc.Get("Undo");
        _action = undo;
        _commit = commit;
        IsOpen = true;
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Start();
    }

    public void InvokeAction()
    {
        var action = _action;
        _commit = null;
        Dismiss(commit: false);
        action?.Invoke();
    }

    public void Dismiss(bool commit)
    {
        _timer.Stop();
        var pending = _commit;
        _commit = null;
        _action = null;
        IsOpen = false;
        if (commit) pending?.Invoke();
    }
}
