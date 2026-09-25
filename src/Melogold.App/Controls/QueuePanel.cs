using System.Collections.ObjectModel;
using Melogold.App.Services;
using Melogold.App.Views;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Melogold.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Melogold.App.Controls;

/// <summary>
/// Очередь — панель справа около 320 px (docs/PROMPT.md §5.2): «Сейчас играет», «Далее» и блок автоплея «Далее —
/// похожие»; перетаскивание, удаление крестиком при наведении или клавишей Delete, «Сохранить как плейлист» и
/// «Очистить» (с «Отменить»). Клик по треку — переход к нему.
/// </summary>
public sealed partial class QueuePanel : Grid
{
    private readonly PlayerEngine _engine = App.Services.GetRequiredService<PlayerEngine>();
    private readonly ObservableCollection<QueueEntry> _entries = [];
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        IsItemClickEnabled = true,
        CanReorderItems = true,
        AllowDrop = true,
        CanDragItems = true,
        Padding = new Thickness(0, 0, 0, 12),
    };
    private readonly TextBlock _summary = new() { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
    private bool _rebuildQueued;
    private bool _dragging;

    public QueuePanel()
    {
        Width = 320;
        Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        BorderThickness = new Thickness(1, 0, 0, 0);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(this, Loc.Get("QueueTitle"));

        var header = new Grid { Padding = new Thickness(16, 12, 8, 8), RowSpacing = 2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition());
        header.RowDefinitions.Add(new RowDefinition());
        header.Children.Add(new TextBlock { Text = Loc.Get("QueueTitle"), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        SetRow(_summary, 1);
        header.Children.Add(_summary);
        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 16 }, Style = (Style)Application.Current.Resources["PlayerIconButtonStyle"], VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(more, Loc.Get("QueueActions"));
        ToolTipService.SetToolTip(more, Loc.Get("QueueActions"));
        var menu = new MenuFlyout();
        var save = new MenuFlyoutItem { Text = Loc.Get("QueueSaveAsPlaylist"), Icon = new FontIcon { Glyph = "" } };
        save.Click += async (_, _) => await SaveAsync();
        var clear = new MenuFlyoutItem { Text = Loc.Get("QueueClear"), Icon = new FontIcon { Glyph = "" } };
        clear.Click += (_, _) => Clear();
        menu.Items.Add(save);
        menu.Items.Add(clear);
        more.Flyout = menu;
        SetColumn(more, 1);
        SetRowSpan(more, 2);
        header.Children.Add(more);
        Children.Add(header);

        _list.ItemsSource = _entries;
        _list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:controls="using:Melogold.App.Controls">
                <controls:QueueRow Entry="{Binding}" />
            </DataTemplate>
            """);
        _list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is QueueEntry { Item: { } item, IsCurrent: false }) _engine.JumpTo(item.Id);
        };
        _list.KeyDown += OnListKeyDown;
        _list.DragItemsStarting += (_, e) =>
        {
            // Заголовки и текущий трек не перетаскиваются
            if (e.Items.OfType<QueueEntry>().Any(entry => entry.Item is null || entry.IsCurrent)) e.Cancel = true;
            else _dragging = true;
        };
        _list.DragItemsCompleted += (_, e) =>
        {
            _dragging = false;
            if (e.Items.OfType<QueueEntry>().FirstOrDefault() is { Item: { } moved }) Moved(moved);
        };
        _list.ContainerContentChanging += (_, e) =>
        {
            // Заголовки раздела не выбираются и не получают фокус
            if (e.ItemContainer is ListViewItem container)
            {
                var header = e.Item is QueueEntry { Item: null };
                container.IsHitTestVisible = !header;
                container.IsTabStop = !header;
            }
        };
        SetRow(_list, 1);
        Children.Add(_list);

        _engine.QueueChanged += QueueRebuild;
        _engine.TrackChanged += QueueRebuild;
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public event Action<bool>? OpenChanged;

    public void Toggle()
    {
        Visibility = IsOpen ? Visibility.Collapsed : Visibility.Visible;
        if (IsOpen) Rebuild();
        OpenChanged?.Invoke(IsOpen);
    }

    public void Close()
    {
        if (!IsOpen) return;
        Visibility = Visibility.Collapsed;
        OpenChanged?.Invoke(false);
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued || !IsOpen) return;
        _rebuildQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _rebuildQueued = false;
            if (!_dragging) Rebuild();
        });
    }

    /// <summary>Разделы в порядке проигрывания: текущий, свои дальше, автоплей.</summary>
    private void Rebuild()
    {
        var queue = _engine.Queue;
        var ordered = queue.Ordered;
        var current = queue.CurrentItem;
        var position = current is null ? -1 : ordered.ToList().FindIndex(i => i.Id == current.Id);
        var upcoming = ordered.Skip(position + 1).ToList();
        var user = upcoming.Where(i => !i.FromAutoplay).ToList();
        var autoplay = upcoming.Where(i => i.FromAutoplay).ToList();

        _entries.Clear();
        if (current is not null)
        {
            _entries.Add(new QueueEntry(Loc.Get("QueueNowPlaying"), null, false, this));
            _entries.Add(new QueueEntry(null, current, true, this));
        }
        if (user.Count > 0)
        {
            _entries.Add(new QueueEntry(Loc.Get("QueueUpNext"), null, false, this));
            foreach (var item in user) _entries.Add(new QueueEntry(null, item, false, this));
        }
        if (autoplay.Count > 0)
        {
            _entries.Add(new QueueEntry(Loc.Get("QueueSimilar"), null, false, this));
            foreach (var item in autoplay) _entries.Add(new QueueEntry(null, item, false, this));
        }
        var total = upcoming.Count + (current is null ? 0 : 1);
        var duration = upcoming.Sum(i => i.Track.DurationMs ?? 0) + (current?.Track.DurationMs ?? 0);
        _summary.Text = total == 0 ? "" : string.Join(" · ", new[] { Loc.Plural("Tracks", total), duration > 0 ? Durations.Format(duration) : null }.OfType<string>());
    }

    /// <summary>Перетащили: новое место в порядке проигрывания; перенос выше блока автоплея делает трек своим.</summary>
    private void Moved(QueueItem moved)
    {
        var index = _entries.ToList().FindIndex(e => e.Item?.Id == moved.Id);
        if (index < 0) return;
        var queue = _engine.Queue;
        var ordered = queue.Ordered.ToList();
        var currentPosition = queue.CurrentItem is { } current ? ordered.FindIndex(i => i.Id == current.Id) : -1;
        // Сколько треков очереди стоит перед ним на панели после текущего
        var before = _entries.Take(index).Count(e => e.Item is not null && !e.IsCurrent);
        queue.Move(moved.Id, currentPosition + 1 + before);
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || _list.SelectedItem is not QueueEntry { Item: { } item, IsCurrent: false }) return;
        Remove(item);
        e.Handled = true;
    }

    internal void Remove(QueueItem item)
    {
        var before = _engine.Queue.Snapshot();
        _engine.Queue.Remove(item.Id);
        App.Services.GetRequiredService<Snackbar>().Show(Loc.Get("QueueRemoved"), Loc.Get("Undo"), () => Undo(before));
    }

    private void Clear()
    {
        var before = _engine.Queue.Snapshot();
        _engine.Queue.ClearExceptCurrent();
        App.Services.GetRequiredService<Snackbar>().Show(Loc.Get("QueueCleared"), Loc.Get("Undo"), () => Undo(before));
    }

    /// <summary>«Отменить» возвращает очередь, если играет всё тот же трек.</summary>
    private void Undo(QueueSnapshot before)
    {
        var queue = _engine.Queue;
        if (before.Index >= 0 && before.Index < before.Items.Count && queue.CurrentItem?.Id == before.Items[before.Index].Id) queue.Restore(before);
    }

    private async Task SaveAsync()
    {
        var tracks = _engine.Queue.Ordered.Select(i => i.Track).ToList();
        if (tracks.Count == 0) return;
        var name = await PlaylistDialogs.AskNameAsync(XamlRoot, Loc.Get("QueueSaveAsPlaylist"), "");
        if (name is null) return;
        var library = App.Services.GetRequiredService<Library>();
        _ = Task.Run(() => library.CreatePlaylist(name, tracks));
        App.Services.GetRequiredService<Snackbar>().Show(Loc.Format("QueueSavedFormat", name));
    }
}

/// <summary>Запись панели очереди: заголовок раздела или трек.</summary>
public sealed class QueueEntry(string? header, QueueItem? item, bool isCurrent, QueuePanel owner)
{
    public string? Header { get; } = header;
    public QueueItem? Item { get; } = item;
    public bool IsCurrent { get; } = isCurrent;
    internal QueuePanel Owner { get; } = owner;
}

/// <summary>Строка панели очереди; разметка — в коде, XAML-файл регистрирует тип для шаблона списка.</summary>
public sealed partial class QueueRow : Grid
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(
        nameof(Entry), typeof(object), typeof(QueueRow), new PropertyMetadata(null, (d, _) => ((QueueRow)d).Build()));

    private Button? _remove;

    public QueueRow()
    {
        InitializeComponent();
        PointerEntered += (_, _) => ShowRemove(true);
        PointerExited += (_, _) => ShowRemove(false);
        ContextRequested += OnContextRequested;
    }

    public object? Entry
    {
        get => GetValue(EntryProperty);
        set => SetValue(EntryProperty, value);
    }

    private void Build()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        _remove = null;
        if (Entry is not QueueEntry entry) return;
        if (entry.Header is { } header)
        {
            Padding = new Thickness(0);
            Children.Add(new TextBlock { Text = header, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(4, 12, 0, 4) });
            AutomationProperties.SetName(this, header);
            return;
        }
        var item = entry.Item!;
        var track = item.Track;
        ColumnSpacing = 12;
        Padding = new Thickness(0, 4, 0, 4);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var artwork = new Grid { Width = 40, Height = 40, CornerRadius = new CornerRadius(4) };
        artwork.Children.Add(new Image
        {
            Source = Images.Row(Thumbnails.Sized(track.ThumbnailUrl ?? Thumbnails.ForVideo(track.VideoId), 120)),
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (entry.IsCurrent)
        {
            // Столбики «играет» поверх обложки текущего трека
            var overlay = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 0, 0, 0)) };
            overlay.Children.Add(new PlayingBars { Width = 18, Height = 16 });
            artwork.Children.Add(overlay);
        }
        Children.Add(artwork);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = track.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = entry.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)Application.Current.Resources[entry.IsCurrent ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"],
        });
        text.Children.Add(new TextBlock
        {
            Text = track.ArtistsText ?? "",
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        SetColumn(text, 1);
        Children.Add(text);
        if (!entry.IsCurrent)
        {
            var remove = _remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 12 },
                Style = (Style)Application.Current.Resources["RowIconButtonStyle"],
                Opacity = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(remove, Loc.Get("MenuRemoveFromQueue"));
            ToolTipService.SetToolTip(remove, Loc.Get("MenuRemoveFromQueue"));
            remove.Click += (_, _) => entry.Owner.Remove(item);
            // Крестик — при наведении или фокусе, как в «Медиаплеере»
            remove.GotFocus += (_, _) => remove.Opacity = 1;
            remove.LostFocus += (_, _) => remove.Opacity = 0;
            SetColumn(remove, 2);
            Children.Add(remove);
        }
        AutomationProperties.SetName(this, string.Join(", ", new[] { track.Title, track.ArtistsText }.Where(s => !string.IsNullOrEmpty(s))));
    }

    private void ShowRemove(bool show)
    {
        if (_remove is not null) _remove.Opacity = show ? 1 : 0;
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (Entry is not QueueEntry { Item: { } item } entry) return;
        App.Services.GetRequiredService<TrackActions>().ShowMenu(item.Track, new TrackContext.Queue(item.Id), this,
            onRemove: entry.IsCurrent ? null : () => entry.Owner.Remove(item));
        e.Handled = true;
    }
}
