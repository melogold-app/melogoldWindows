using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.Core.Music;
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
        try
        {
            await load(cts.Token);
            if (!cts.IsCancellationRequested) state.ShowContent();
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

/// <summary>Кэш страниц каталога в памяти на 10 минут: «Назад» и повторное открытие — без сети.</summary>
public sealed class CatalogCache
{
    private readonly Dictionary<string, (DateTime At, object Value)> _items = [];
    private readonly Lock _lock = new();

    public async Task<T> GetAsync<T>(string key, Func<Task<T>> load) where T : class
    {
        lock (_lock)
        {
            if (_items.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10) && hit.Value is T value) return value;
        }
        var loaded = await load();
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
}
