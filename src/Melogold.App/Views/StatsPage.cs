using System.Globalization;
using Melogold.App.Controls;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Melogold.App.Views;

/// <summary>
/// «Итоги» (tasks/0015): статистика прослушиваний за неделю, месяц, год и всё время — числа и сравнение с прошлым таким
/// же периодом, лучшие треки, исполнители и альбомы, «Когда вы слушали», «Время суток», открытия. Считается по Истории на
/// устройстве (<see cref="Library.Stats"/>), без сети; фильтр устройств — как в Истории. По умолчанию — текущий месяц.
/// </summary>
public sealed partial class StatsPage : Page, IScrollToTop
{
    private const int Shown = 10;
    private const double WideWidth = 860;
    private const double ChartHeight = 120;

    private readonly Library _library = App.Services.GetRequiredService<Library>();
    private readonly ScrollViewer _scroller = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(36, 24, 36, 36), Spacing = 8 };
    private readonly SelectorBar _kind = new();
    private readonly HistoryDeviceFilter _device = new();
    private readonly Button _recap = new() { VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly Button _previous = ArrowButton("", "StatsPrevious");
    private readonly Button _next = ArrowButton("", "StatsNext");
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, Style = StatsUi.Style("SubtitleTextBlockStyle") };
    private readonly StackPanel _navigation = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StateView _empty = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _body = new();
    private readonly TextBlock _note = StatsUi.Caption(Loc.Get("StatsAllTimeNote"));
    private readonly HashSet<string> _expanded = [];
    private StatsPeriod _period = StatsPeriod.Of(StatsPeriodKind.Month, StatsTexts.Today);
    private ListeningStats? _stats;
    private long? _firstPlay;
    private int _version;
    private bool _dirty = true;
    private bool _wide = true;

    public StatsPage()
    {
        InitializeComponent();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = Loc.Get("Stats"), Style = StatsUi.Style("PageTitleStyle") });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _device.FilterChanged += Reload;
        actions.Children.Add(_device);
        _recap.Content = Loc.Get("StatsRecap");
        _recap.Click += (_, _) =>
        {
            if (RecapYear() is { } year) StatsUi.Navigator.Open(typeof(YearRecapPage), year.ToString(CultureInfo.InvariantCulture));
        };
        actions.Children.Add(_recap);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        _content.Children.Add(header);

        foreach (var (kind, key) in new[] { (StatsPeriodKind.Week, "StatsWeek"), (StatsPeriodKind.Month, "StatsMonth"), (StatsPeriodKind.Year, "StatsYear"), (StatsPeriodKind.All, "StatsAllTime") })
            _kind.Items.Add(new SelectorBarItem { Text = Loc.Get(key), Tag = (int)kind });
        _kind.SelectedItem = _kind.Items[1];
        _kind.SelectionChanged += (_, _) =>
        {
            if (_kind.SelectedItem?.Tag is not int tag || (StatsPeriodKind)tag == _period.Kind) return;
            _period = StatsPeriod.Of((StatsPeriodKind)tag, StatsTexts.Today);
            Reload();
        };
        _content.Children.Add(_kind);

        // ‹ сентябрь 2026 › — листать периоды; вперёд дальше сегодняшнего и назад раньше первого прослушивания нельзя
        _previous.Click += (_, _) =>
        {
            _period = _period.Previous();
            Reload();
        };
        _next.Click += (_, _) =>
        {
            _period = _period.Next();
            Reload();
        };
        _navigation.Children.Add(_previous);
        _navigation.Children.Add(_label);
        _navigation.Children.Add(_next);
        _content.Children.Add(_navigation);
        _content.Children.Add(_empty);
        _content.Children.Add(_body);
        _note.Margin = new Thickness(0, 24, 0, 0);
        _content.Children.Add(_note);
        _scroller.Content = _content;
        Content = _scroller;

        // В узком окне поля по бокам 16, как у списков; исполнители и альбомы — рядом только в широком
        _scroller.SizeChanged += (_, e) =>
        {
            var side = e.NewSize.Width < MusicListView.NarrowWidth ? 16 : 36;
            if (_content.Margin.Left != side) _content.Margin = new Thickness(side, 24, side, 36);
            var wide = e.NewSize.Width >= WideWidth;
            if (wide == _wide) return;
            _wide = wide;
            Render();
        };
        _library.Changed += change =>
        {
            if (!change.HasFlag(LibraryChange.History) && !change.HasFlag(LibraryChange.Overrides)) return;
            _dirty = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLoaded) Reload();
            });
        };
        Loaded += async (_, _) =>
        {
            await _device.RefreshAsync();
            if (_dirty) Reload();
        };
    }

    public void ScrollToTop() => _scroller.ChangeView(null, 0, null);

    private async void Reload()
    {
        _dirty = false;
        var version = ++_version;
        var (period, filter) = (_period, _device.Filter);
        ShowPeriod();
        var (stats, first) = await Task.Run(() => (_library.Stats(period, TimeZoneInfo.Local, filter), _library.FirstPlayAt(filter)));
        if (version != _version) return;
        _stats = stats;
        _firstPlay = first;
        Render();
    }

    /// <summary>«Итоги года»: у «Года» — этого года, иначе в декабре и январе — уходящего (<see cref="StatsPeriod.RecapYear"/>).</summary>
    private int? RecapYear() => _period.Kind == StatsPeriodKind.Year
        ? _stats is { IsEmpty: true } ? null : _period.Start.Year
        : StatsPeriod.RecapYear(StatsTexts.Today);

    private void ShowPeriod()
    {
        _navigation.Visibility = _period.Kind == StatsPeriodKind.All ? Visibility.Collapsed : Visibility.Visible;
        _label.Text = StatsTexts.PeriodName(_period);
        _next.IsEnabled = _period.Next().Start <= StatsTexts.Today;
        _previous.IsEnabled = _firstPlay is { } first && first < _period.Range(TimeZoneInfo.Local).From;
        _note.Visibility = _period.Kind == StatsPeriodKind.All ? Visibility.Visible : Visibility.Collapsed;
        _recap.Visibility = RecapYear() is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Render()
    {
        ShowPeriod();
        _body.Children.Clear();
        if (_stats is not { } stats) return;
        if (stats.IsEmpty)
        {
            _empty.ShowEmpty("", Loc.Get("StatsEmpty"));
            return;
        }
        _empty.ShowContent();
        _body.Children.Add(Tiles(stats));
        _body.Children.Add(Section(Loc.Get("StatsTopTracks"), TrackList(stats.TopTracks, ShownOf("tracks")), "tracks", stats.TopTracks.Count));

        var artists = Section(Loc.Get("StatsTopArtists"), ArtistList(stats.TopArtists, ShownOf("artists")), "artists", stats.TopArtists.Count);
        var albums = stats.TopAlbums.Count == 0 ? null : Section(Loc.Get("StatsTopAlbums"), AlbumList(stats.TopAlbums, ShownOf("albums")), "albums", stats.TopAlbums.Count);
        if (_wide && albums is not null)
        {
            var pair = new Grid { ColumnSpacing = 32 };
            pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pair.Children.Add(artists);
            Grid.SetColumn(albums, 1);
            pair.Children.Add(albums);
            _body.Children.Add(pair);
        }
        else
        {
            _body.Children.Add(artists);
            if (albums is not null) _body.Children.Add(albums);
        }

        var when = new StackPanel { Spacing = 8 };
        when.Children.Add(Chart(stats.Buckets, i => StatsTexts.BucketName(stats, i), i => StatsTexts.BucketLabel(stats, i), StatsTexts.LabelSpan(stats.Period.Kind)));
        if (stats.BestBucket is { } best)
            when.Children.Add(StatsUi.Caption(Loc.Format("StatsBusiestFormat", StatsTexts.BucketName(stats, best), AllTracksPage.ListeningTime(stats.Buckets[best]))));
        _body.Children.Add(Section(Loc.Get("StatsWhen"), when));

        var hours = new StackPanel { Spacing = 8 };
        hours.Children.Add(Chart(stats.Hours, StatsTexts.HourName, h => h % 6 == 0 ? StatsTexts.HourName(h) : null, 6));
        if (stats.BestHour is { } hour)
            hours.Children.Add(StatsUi.Caption(Loc.Format("StatsHoursBusiestFormat", StatsTexts.HourName(hour), AllTracksPage.ListeningTime(stats.Hours[hour]))));
        _body.Children.Add(Section(Loc.Get("StatsTimeOfDay"), hours));

        if (stats.Discoveries > 0)
        {
            var discoveries = new StackPanel { Spacing = 4 };
            discoveries.Children.Add(new TextBlock { Text = Loc.Plural("StatsDiscovered", stats.Discoveries), Style = StatsUi.Style("BodyStrongTextBlockStyle"), FontSize = 20 });
            discoveries.Children.Add(StatsUi.Caption(Loc.Get("StatsDiscoveriesText")));
            discoveries.Children.Add(TrackList(stats.TopDiscoveries, 5));
            _body.Children.Add(Section(Loc.Get("StatsDiscoveries"), discoveries));
        }
    }

    private int ShownOf(string section) => _expanded.Contains(section) ? int.MaxValue : Shown;

    /// <summary>Время прослушивания (со сравнением), прослушивания, треки, исполнители, альбомы.</summary>
    private static VariableSizedWrapGrid Tiles(ListeningStats stats)
    {
        var tiles = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 196, ItemHeight = 108, Margin = new Thickness(0, 16, 0, 0) };
        var time = Tile(AllTracksPage.ListeningTime(stats.PlayTimeMs), Loc.Get("StatsListeningTime"), StatsTexts.Change(stats));
        VariableSizedWrapGrid.SetColumnSpan(time, 2);
        tiles.Children.Add(time);
        tiles.Children.Add(Tile(StatsTexts.Number(stats.Plays), Loc.Get("StatsPlays"), null));
        tiles.Children.Add(Tile(StatsTexts.Number(stats.Tracks), Loc.Get("StatsTracks"), null));
        tiles.Children.Add(Tile(StatsTexts.Number(stats.Artists), Loc.Get("StatsArtists"), null));
        tiles.Children.Add(Tile(StatsTexts.Number(stats.Albums), Loc.Get("StatsAlbums"), null));
        return tiles;
    }

    private static Border Tile(string value, string caption, string? change)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = value, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = caption, Foreground = StatsUi.Brush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (change is not null) text.Children.Add(StatsUi.Caption(change));
        var tile = new Border
        {
            Child = text,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = StatsUi.Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = StatsUi.Brush("CardStrokeColorDefaultBrush"),
        };
        AutomationProperties.SetName(tile, change is null ? $"{caption}: {value}" : $"{caption}: {value}, {change}");
        return tile;
    }

    /// <summary>Заголовок раздела и, если строк больше 10, «Показать все» (до 50) / «Свернуть».</summary>
    private StackPanel Section(string title, UIElement content, string? expandKey = null, int count = 0)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 24, 0, 0) };
        var header = new Grid { MinHeight = 32 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new TextBlock { Text = title, Style = StatsUi.Style("SubtitleTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        header.Children.Add(heading);
        if (expandKey is not null && count > Shown)
        {
            var expanded = _expanded.Contains(expandKey);
            var more = new HyperlinkButton { Content = Loc.Get(expanded ? "StatsShowLess" : "StatsShowAll") };
            more.Click += (_, _) =>
            {
                if (!_expanded.Add(expandKey)) _expanded.Remove(expandKey);
                Render();
            };
            Grid.SetColumn(more, 1);
            header.Children.Add(more);
        }
        panel.Children.Add(header);
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>Трек включается, очередь — весь список (топ треков или открытия); правый клик — меню трека.</summary>
    private static ListView TrackList(IReadOnlyList<StatsTrack> entries, int shown)
    {
        var queue = entries.Select(e => e.Track).ToList();
        var actions = App.Services.GetRequiredService<TrackActions>();
        var list = StatsUi.List();
        for (var i = 0; i < Math.Min(shown, entries.Count); i++)
        {
            var entry = entries[i];
            var display = RowVm.Display?.Invoke(entry.Track) ?? entry.Track;
            var item = StatsUi.Row(i, entry.Track.ThumbnailUrl, round: false, display.Title, display.ArtistsText, entry.PlayTimeMs, entry.Plays);
            item.ContextRequested += (_, e) =>
            {
                Windows.Foundation.Point? at = e.TryGetPosition(item, out var point) ? point : null;
                actions.ShowMenu(entry.Track, new TrackContext.List(), item, at);
                e.Handled = true;
            };
            list.Items.Add(item);
        }
        list.ItemClick += (_, e) =>
        {
            if (StatsUi.IndexOf(e.ClickedItem) is { } index) actions.Play(queue, index, new TrackContext.List());
        };
        return list;
    }

    /// <summary>Исполнитель открывается; без карты исполнителей или по своему имени — поиск по имени.</summary>
    private static ListView ArtistList(IReadOnlyList<StatsArtist> artists, int shown)
    {
        var list = StatsUi.List();
        for (var i = 0; i < Math.Min(shown, artists.Count); i++)
            list.Items.Add(StatsUi.Row(i, artists[i].ThumbnailUrl, round: true, artists[i].Name, null, artists[i].PlayTimeMs, artists[i].Plays));
        list.ItemClick += (_, e) =>
        {
            if (StatsUi.IndexOf(e.ClickedItem) is not { } index) return;
            var artist = artists[index];
            if (artist.BrowseId is { } id) StatsUi.Navigator.Open(typeof(ArtistPage), id);
            else StatsUi.Navigator.Open(typeof(SearchPage), new SearchRequest(artist.Name));
        };
        return list;
    }

    private static ListView AlbumList(IReadOnlyList<StatsAlbum> albums, int shown)
    {
        var list = StatsUi.List();
        for (var i = 0; i < Math.Min(shown, albums.Count); i++)
            list.Items.Add(StatsUi.Row(i, albums[i].ThumbnailUrl, round: false, albums[i].Title, null, albums[i].PlayTimeMs, albums[i].Plays));
        list.ItemClick += (_, e) =>
        {
            if (StatsUi.IndexOf(e.ClickedItem) is not { } index) return;
            var album = albums[index];
            if (album.BrowseId is { } id) StatsUi.Navigator.Open(typeof(AlbumPage), id);
            else StatsUi.Navigator.Open(typeof(SearchPage), new SearchRequest(album.Title));
        };
        return list;
    }

    /// <summary>
    /// Столбцы: высота — доля от самого слушаемого столбца; подсказка и имя для экранного диктора — «28 сентября: 2 ч 5 мин».
    /// Подписи — под частью столбцов, каждая занимает <paramref name="span"/> колонок.
    /// </summary>
    private static StackPanel Chart(IReadOnlyList<long> values, Func<int, string> name, Func<int, string?> label, int span)
    {
        var max = values.Count == 0 ? 0 : values.Max();
        var spacing = values.Count > 24 ? 2 : 4;
        var bars = new Grid { Height = ChartHeight, ColumnSpacing = spacing };
        var labels = new Grid { ColumnSpacing = spacing, Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < values.Count; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var value = values[i];
            var cell = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            cell.Children.Add(new Rectangle
            {
                Height = max == 0 ? 0 : Math.Max(value > 0 ? 3 : 0, value / (double)max * ChartHeight),
                MaxWidth = 48,
                VerticalAlignment = VerticalAlignment.Bottom,
                RadiusX = 3,
                RadiusY = 3,
                Fill = StatsUi.Brush("AccentFillColorDefaultBrush"),
            });
            var text = $"{StatsTexts.Capitalize(name(i))}: {AllTracksPage.ListeningTime(value)}";
            ToolTipService.SetToolTip(cell, text);
            AutomationProperties.SetName(cell, text);
            Grid.SetColumn(cell, i);
            bars.Children.Add(cell);
            if (label(i) is not { } caption) continue;
            var block = new TextBlock
            {
                Text = caption,
                Style = StatsUi.Style("CaptionTextBlockStyle"),
                Foreground = StatsUi.Brush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = span == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            };
            AutomationProperties.SetAccessibilityView(block, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetColumn(block, i);
            Grid.SetColumnSpan(block, Math.Min(span, values.Count - i));
            labels.Children.Add(block);
        }
        var chart = new StackPanel();
        chart.Children.Add(bars);
        chart.Children.Add(labels);
        return chart;
    }

    private static Button ArrowButton(string glyph, string key)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(10, 8, 10, 8) };
        AutomationProperties.SetName(button, Loc.Get(key));
        ToolTipService.SetToolTip(button, Loc.Get(key));
        return button;
    }
}

/// <summary>Общие куски экранов «Итогов» и «Итогов года».</summary>
internal static class StatsUi
{
    public static Navigator Navigator => App.Services.GetRequiredService<Navigator>();

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = Style("CaptionTextBlockStyle"),
        Foreground = Brush("TextFillColorSecondaryBrush"),
    };

    /// <summary>Список строк внутри страницы: сам не прокручивается (колесо уходит странице), клик — действие строки.</summary>
    public static ListView List()
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, Margin = new Thickness(-12, 0, -12, 0) };
        ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list;
    }

    public static int? IndexOf(object? clicked) => clicked is FrameworkElement { Tag: int index } ? index : null;

    /// <summary>Место, обложка, название и подпись, время и число прослушиваний.</summary>
    public static ListViewItem Row(int index, string? image, bool round, string title, string? subtitle, long ms, int plays)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Tag = index };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rank = (index + 1).ToString(CultureInfo.CurrentCulture);
        grid.Children.Add(new TextBlock { Text = rank, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextFillColorSecondaryBrush") });
        var cover = Cover(image, 40, round);
        Grid.SetColumn(cover, 1);
        grid.Children.Add(cover);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(subtitle))
            text.Children.Add(new TextBlock { Text = subtitle, TextTrimming = TextTrimming.CharacterEllipsis, Style = Style("CaptionTextBlockStyle"), Foreground = Brush("TextFillColorSecondaryBrush") });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        var time = AllTracksPage.ListeningTime(ms);
        var count = Loc.Plural("StatsPlaysCount", plays);
        var numbers = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        numbers.Children.Add(new TextBlock { Text = time, HorizontalAlignment = HorizontalAlignment.Right });
        numbers.Children.Add(new TextBlock { Text = count, HorizontalAlignment = HorizontalAlignment.Right, Style = Style("CaptionTextBlockStyle"), Foreground = Brush("TextFillColorSecondaryBrush") });
        Grid.SetColumn(numbers, 3);
        grid.Children.Add(numbers);
        var item = new ListViewItem { Content = grid, Tag = index };
        AutomationProperties.SetName(item, string.Join(", ", new[] { $"{rank}. {title}", subtitle, time, count }.Where(s => !string.IsNullOrEmpty(s))));
        return item;
    }

    public static Border Cover(string? url, double size, bool round) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(round ? size / 2 : Math.Max(4, size / 10)),
        Background = Brush("CardBackgroundFillColorSecondaryBrush"),
        Child = new Image { Source = size > 64 ? Images.Card(url) : Images.Row(url), Stretch = Stretch.UniformToFill },
    };
}

/// <summary>Тексты «Итогов»: имена периодов и столбцов, сравнение с прошлым периодом — на языке приложения.</summary>
internal static class StatsTexts
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static CultureInfo Culture => CultureInfo.CurrentUICulture;

    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Culture) + text[1..];

    /// <summary>«сентябрь» — как пишется сам по себе, без числа.</summary>
    public static string MonthName(int month) => Culture.DateTimeFormat.GetMonthName(month);

    /// <summary>«28 сентября», «September 28».</summary>
    public static string DayMonth(DateOnly day) => day.ToString(Culture.DateTimeFormat.MonthDayPattern, Culture);

    /// <summary>«28 сентября – 4 октября 2026», «Сентябрь 2026», «2026», «Всё время».</summary>
    public static string PeriodName(StatsPeriod period)
    {
        switch (period.Kind)
        {
            case StatsPeriodKind.Week:
                var last = period.End.AddDays(-1);
                return period.Start.Year == last.Year
                    ? $"{DayMonth(period.Start)} – {DayMonth(last)} {last.Year}"
                    : $"{DayMonth(period.Start)} {period.Start.Year} – {DayMonth(last)} {last.Year}";
            case StatsPeriodKind.Month:
                return $"{Capitalize(MonthName(period.Start.Month))} {period.Start.Year}";
            case StatsPeriodKind.Year:
                return period.Start.Year.ToString(CultureInfo.InvariantCulture);
            default:
                return Loc.Get("StatsAllTime");
        }
    }

    /// <summary>«+12 % к августу», «−5 % к прошлой неделе»; null — сравнивать не с чем.</summary>
    public static string? Change(ListeningStats stats)
    {
        if (stats.Change is not { } change) return null;
        var percent = (long)Math.Round(change * 100, MidpointRounding.AwayFromZero);
        var number = percent > 0 ? "+" + Number(percent) : percent < 0 ? "−" + Number(-percent) : "0";
        var previous = stats.Period.Previous().Start;
        var target = stats.Period.Kind switch
        {
            StatsPeriodKind.Week => Loc.Get("StatsVsLastWeek"),
            StatsPeriodKind.Month when previous.Year == Today.Year => MonthCompared(previous.Month),
            StatsPeriodKind.Month => Loc.Format("StatsVsMonthYearFormat", MonthCompared(previous.Month), previous.Year),
            _ => Loc.Format("StatsVsYearFormat", previous.Year),
        };
        return Loc.Format("StatsChangeFormat", number, target);
    }

    /// <summary>«к августу»: месяц в той форме, в какой он стоит после «к».</summary>
    private static string MonthCompared(int month) => Loc.Get("StatsMonthsCompare").Split('|') is { Length: 12 } names ? names[month - 1] : MonthName(month);

    /// <summary>Столбец «Когда вы слушали» целиком: «понедельник, 28 сентября», «28 сентября», «сентябрь», «2025».</summary>
    public static string BucketName(ListeningStats stats, int index) => stats.Period.Kind switch
    {
        StatsPeriodKind.Week => $"{stats.Period.Start.AddDays(index).ToString("dddd", Culture)}, {DayMonth(stats.Period.Start.AddDays(index))}",
        StatsPeriodKind.Month => DayMonth(stats.Period.Start.AddDays(index)),
        StatsPeriodKind.Year => MonthName(index + 1),
        _ => (stats.FirstBucketYear + index).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Подпись под столбцом: дни недели, 1 · 8 · 15 · 22 · 29, три буквы месяца, год.</summary>
    public static string? BucketLabel(ListeningStats stats, int index) => stats.Period.Kind switch
    {
        StatsPeriodKind.Week => Capitalize(Culture.DateTimeFormat.GetAbbreviatedDayName(stats.Period.Start.AddDays(index).DayOfWeek)),
        StatsPeriodKind.Month => index % 7 == 0 ? (index + 1).ToString(CultureInfo.CurrentCulture) : null,
        StatsPeriodKind.Year => Capitalize(MonthName(index + 1)[..Math.Min(3, MonthName(index + 1).Length)]),
        _ => (stats.FirstBucketYear + index).ToString(CultureInfo.InvariantCulture),
    };

    public static int LabelSpan(StatsPeriodKind kind) => kind == StatsPeriodKind.Month ? 7 : 1;

    public static string HourName(int hour) => $"{hour:00}:00";

    public static string DayPartName(DayPart part) => Loc.Get(part switch
    {
        DayPart.Night => "StatsDayPartNight",
        DayPart.Morning => "StatsDayPartMorning",
        DayPart.Afternoon => "StatsDayPartAfternoon",
        _ => "StatsDayPartEvening",
    });
}
