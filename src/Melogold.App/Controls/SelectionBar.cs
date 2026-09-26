using Melogold.App.Services;
using Melogold.Core.Music;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>
/// Действия с выделенными треками (пользователь, 2026-09-26: «выделить все или конкретные и скачать, в Избранное,
/// создать с ними плейлист» — так собираются альбомы, которые после цензуры лежат на YouTube разными видео). Выделяют,
/// как везде в Windows: Ctrl+щелчок, Shift+щелчок, Ctrl+A. Появляется, когда выделено два трека и больше; поверх
/// содержимого над плеером, стандартной <see cref="CommandBar"/>: в узком окне кнопки сами уходят в «…».
/// </summary>
public sealed partial class SelectionBar : Grid
{
    private readonly CommandBar _bar = new() { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
    private IReadOnlyList<Track> _tracks = [];
    private MusicListView? _owner;

    public SelectionBar()
    {
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        MaxWidth = 960;
        Margin = new Thickness(12, 0, 12, 12);
        Padding = new Thickness(4);
        CornerRadius = (CornerRadius)Application.Current.Resources["OverlayCornerRadius"];
        Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorTertiaryBrush"];
        BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"];
        BorderThickness = new Thickness(1);
        _bar.VerticalContentAlignment = VerticalAlignment.Center;
        _bar.Content = _count;
        Add("SelectionPlay", "", () => Actions.PlayAll(_tracks));
        Add("SelectionQueue", "", () => Actions.QueueAll(_tracks));
        Add("SelectionLike", "", () => Actions.LikeAll(_tracks));
        Add("SelectionAddToPlaylist", "", () => Actions.AddAllToPlaylist(_tracks));
        Add("SelectionNewPlaylist", "", () => _ = Actions.NewPlaylistAsync(_tracks, XamlRoot));
        Add("SelectionDownload", "", () => Actions.DownloadAll(_tracks));
        _bar.PrimaryCommands.Add(new AppBarSeparator());
        Add("SelectionSelectAll", "", () => _owner?.SelectAllTracks(), "Ctrl+A");
        Add("SelectionClear", "", () => _owner?.ClearSelection(), "Esc");
        Children.Add(_bar);
    }

    private static TrackActions Actions => App.Services.GetRequiredService<TrackActions>();

    private void Add(string key, string glyph, Action action, string? keys = null)
    {
        var button = new AppBarButton { Label = Loc.Get(key), Icon = new FontIcon { Glyph = glyph } };
        if (keys is not null) button.KeyboardAcceleratorTextOverride = keys;
        ToolTipService.SetToolTip(button, keys is null ? Loc.Get(key) : $"{Loc.Get(key)} ({keys})");
        button.Click += (_, _) => action();
        _bar.PrimaryCommands.Add(button);
    }

    /// <summary>Выделение списка изменилось: два трека и больше — панель, иначе — спрятать (если она этого списка).</summary>
    public void Update(MusicListView owner, IReadOnlyList<Track> tracks)
    {
        if (tracks.Count < 2)
        {
            if (ReferenceEquals(_owner, owner)) Hide();
            return;
        }
        _owner = owner;
        _tracks = tracks;
        _count.Text = Loc.Format("SelectionCountFormat", tracks.Count);
        Visibility = Visibility.Visible;
    }

    /// <summary>Список ушёл с экрана — панель его выделения тоже.</summary>
    public void Forget(MusicListView owner)
    {
        if (ReferenceEquals(_owner, owner)) Hide();
    }

    public bool IsShown => Visibility == Visibility.Visible;

    private void Hide()
    {
        _owner = null;
        _tracks = [];
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Esc: снять выделение, если панель на экране.</summary>
    public bool TryClear()
    {
        if (!IsShown || _owner is null) return false;
        _owner.ClearSelection();
        return true;
    }
}
