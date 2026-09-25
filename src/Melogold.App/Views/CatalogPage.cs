using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.Core.Music;
using Melogold.InnerTube;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Melogold.App.Views;

public enum SearchScope
{
    All,
    Music,
    YouTube,
}

/// <summary>Выдача поиска: запрос и сегмент «Всё · Музыка · YouTube».</summary>
public sealed record SearchRequest(string Query, SearchScope Scope = SearchScope.All);

/// <summary>Страница полок YouTube Music: настроения, «Все новые релизы», «Все» полки исполнителя.</summary>
public sealed record BrowseRequest(string Title, string BrowseId, string? Params);

/// <summary>Список треков полки целиком («Все ›»).</summary>
public sealed record TrackListRequest(string Title, IReadOnlyList<Track> Tracks);

/// <summary>
/// Страница каталога: загрузка через <see cref="StateView"/> (кольцо через 300 мс, ошибки REWRITE §3.0 с «Повторить»),
/// отмена при уходе со страницы. Данные берутся из <see cref="CatalogCache"/>: «Назад» не ходит в сеть заново.
/// </summary>
public partial class CatalogPage : Page, IScrollToTop
{
    private CancellationTokenSource? _cts;

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (e.NavigationMode == NavigationMode.Back) _cts?.Cancel();
    }

    protected async Task RunAsync(StateView state, Func<CancellationToken, Task> load)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        state.ShowLoading();
        // Без сети кэш отдаёт прежние данные: экран показывает их с «Нет сети — данные от 14:02» (§5.5)
        var stale = CatalogCache.BeginReport();
        try
        {
            await load(cts.Token);
            if (cts.IsCancellationRequested) return;
            if (stale.OldestAt is { } at) state.ShowStale(at);
            else state.ShowContent();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (StateShownException)
        {
            // Экран уже показал «пусто»
        }
        catch (Exception error)
        {
            if (!cts.IsCancellationRequested) state.ShowError(error, () => _ = RunAsync(state, load));
        }
    }

    public virtual void ScrollToTop()
    {
    }
}

/// <summary>Загрузка закончилась своим состоянием (например, «Ничего не нашлось»): не ошибка и не контент.</summary>
public sealed class StateShownException : Exception;

/// <summary>
/// Кэш страниц каталога в памяти: 10 минут данные свежие — «Назад» и повторное открытие без сети; старше — загрузка
/// заново, а если сети нет, отдаются прежние, и экран говорит, от какого они времени.
/// </summary>
public sealed class CatalogCache
{
    /// <summary>Что загрузка экрана взяла из устаревшего кэша (для «Нет сети — данные от …»).</summary>
    public sealed class StaleReport
    {
        public DateTime? OldestAt { get; set; }
    }

    private static readonly AsyncLocal<StaleReport?> Report = new();
    private readonly Dictionary<string, (DateTime At, object Value)> _items = [];
    private readonly Lock _lock = new();

    /// <summary>Начать учёт устаревших данных для загрузки экрана (течёт по её async-вызовам).</summary>
    public static StaleReport BeginReport() => Report.Value = new StaleReport();

    public async Task<T> GetAsync<T>(string key, Func<Task<T>> load) where T : class
    {
        (DateTime At, T Value)? stale = null;
        lock (_lock)
        {
            if (_items.TryGetValue(key, out var hit) && hit.Value is T value)
            {
                if (DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return value;
                stale = (hit.At, value);
            }
        }
        T loaded;
        try
        {
            loaded = await load();
        }
        catch (Exception e) when (stale is { } old && e is HttpRequestException or YouTubeException { Kind: YouTubeErrorKind.Offline })
        {
            if (Report.Value is { } report) report.OldestAt = report.OldestAt is { } at && at < old.At ? at : old.At;
            return old.Value;
        }
        lock (_lock)
        {
            _items[key] = (DateTime.UtcNow, loaded);
            if (_items.Count > 100)
            {
                foreach (var old in _items.OrderBy(i => i.Value.At).Take(20).Select(i => i.Key).ToList()) _items.Remove(old);
            }
        }
        return loaded;
    }

    public void Forget(string key)
    {
        lock (_lock) _items.Remove(key);
    }

    /// <summary>«Очистить кэш» в Настройках.</summary>
    public void Clear()
    {
        lock (_lock) _items.Clear();
    }
}
