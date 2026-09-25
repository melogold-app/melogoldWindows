using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Melogold.App.Services;

/// <summary>Раздел приложения: корень стека и свой <see cref="Frame"/>.</summary>
public sealed record Section(string Key, Type RootPage);

/// <summary>Экран, который умеет прокрутиться наверх (повторное нажатие на раздел в корне).</summary>
public interface IScrollToTop
{
    void ScrollToTop();
}

/// <summary>
/// Навигация по docs/PROMPT.md §5.1: у каждого раздела свой стек (детальные экраны открываются в стеке текущего
/// раздела, панель и плеер остаются на месте). «Назад»: шаг назад в стеке, в корне нестартового раздела — «Тренды».
/// Повторное нажатие на активный раздел: во вложенном экране — к корню, в корне — наверх.
/// </summary>
public sealed class Navigator
{
    public const string Start = "trends";

    private readonly Dictionary<string, Frame> _frames = [];
    private readonly Dictionary<string, Section> _sections = [];

    /// <summary>
    /// Параметры экранов: во <see cref="Frame.Navigate(Type, object)"/> уходит только строковый ключ (WinUI советует простые
    /// типы; записи C# через границу WinRT давали падение 0xC0000005 в CsWinRT), сам объект лежит здесь.
    /// </summary>
    private static readonly Dictionary<string, object> Parameters = [];
    private static long _nextParameter;

    /// <summary>Параметр экрана по ключу из <c>NavigationEventArgs.Parameter</c>.</summary>
    public static object? Resolve(object? parameter) =>
        parameter is string key && key.StartsWith("nav:", StringComparison.Ordinal) && Parameters.TryGetValue(key, out var value) ? value : parameter;

    private static object? Wrap(object? parameter)
    {
        if (parameter is null or string) return parameter;
        var key = "nav:" + Interlocked.Increment(ref _nextParameter);
        Parameters[key] = parameter;
        return key;
    }

    public string Current { get; private set; } = Start;

    /// <summary>Сменился раздел или экран: окну — обновить выделение, кнопку «Назад» и заголовок.</summary>
    public event Action? Changed;

    /// <summary>Раздел стал видимым: окну — показать его <see cref="Frame"/>.</summary>
    public event Action<string>? SectionShown;

    public void Register(Section section, Frame frame)
    {
        _sections[section.Key] = section;
        _frames[section.Key] = frame;
        frame.Navigated += (_, _) => Changed?.Invoke();
        frame.NavigationFailed += (_, e) =>
        {
            Log.Error($"Navigation to {e.SourcePageType.Name} failed", e.Exception);
            e.Handled = true;
        };
    }

    public Frame CurrentFrame => _frames[Current];

    public bool CanGoBack => CurrentFrame.CanGoBack || Current != Start;

    public void Show(string key)
    {
        if (!_frames.ContainsKey(key)) key = Start;
        Current = key;
        var frame = _frames[key];
        if (frame.Content is null) frame.Navigate(_sections[key].RootPage, null, new SuppressNavigationTransitionInfo());
        SectionShown?.Invoke(key);
        Changed?.Invoke();
    }

    /// <summary>Нажали на уже открытый раздел.</summary>
    public void Reselect()
    {
        var frame = CurrentFrame;
        if (frame.CanGoBack)
        {
            while (frame.BackStack.Count > 1) frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
            frame.GoBack(new SuppressNavigationTransitionInfo());
        }
        else if (frame.Content is IScrollToTop page) page.ScrollToTop();
    }

    /// <summary>К корню текущего раздела (после входа, регистрации, выхода).</summary>
    public void BackToRoot()
    {
        var frame = CurrentFrame;
        if (!frame.CanGoBack) return;
        while (frame.BackStack.Count > 1) frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
        frame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
    }

    /// <summary>Открыть экран в стеке текущего раздела.</summary>
    public void Open(Type page, object? parameter = null)
    {
        try
        {
            CurrentFrame.Navigate(page, Wrap(parameter), new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
        }
        catch (Exception e)
        {
            Log.Error($"Navigation to {page.Name} threw", e);
        }
    }

    public bool GoBack()
    {
        var frame = CurrentFrame;
        if (frame.CanGoBack)
        {
            frame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
            return true;
        }
        if (Current != Start)
        {
            Show(Start);
            return true;
        }
        return false;
    }
}
