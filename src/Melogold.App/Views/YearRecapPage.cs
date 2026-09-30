using System.Globalization;
using System.Runtime.InteropServices;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.Core.Data;
using Melogold.Core.Music;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace Melogold.App.Views;

/// <summary>
/// «Итоги года» (tasks/0015): шесть карточек — минуты за год, трек года, топ-5 исполнителей, топ-5 треков, любимый месяц
/// и время суток, открытия; листаются стрелками, колесом и касанием. Фон — цвет обложки трека года. «Поделиться» —
/// картинка 1080×1920 PNG с надписью «Melogold · Итоги 2026» через системное окно или «Сохранить как…».
/// </summary>
public sealed partial class YearRecapPage : Page
{
    private const int PictureWidth = 1080;
    private const int PictureHeight = 1920;
    private const int ListShown = 5;
    private const double Darken = 0.45;

    /// <summary>Цвет Melogold — для года, у трека которого обложка серая или не загрузилась.</summary>
    private static readonly Color Brand = Color.FromArgb(255, 0xFE, 0x6B, 0x08);

    private readonly Library _library = App.Services.GetRequiredService<Library>();
    private readonly Grid _root = new();

    /// <summary>Здесь, под карточками, рисуется картинка для «Поделиться»: в дереве, но её не видно.</summary>
    private readonly Canvas _stage = new() { IsHitTestVisible = false };

    private readonly Grid _surface = new();
    private readonly FlipView _cards = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly PipsPager _pips = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        PreviousButtonVisibility = PipsPagerButtonVisibility.Visible,
        NextButtonVisibility = PipsPagerButtonVisibility.Visible,
        MaxVisiblePips = 6,
    };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 24) };
    private readonly Button _share = IconButton("", "StatsShare");
    private readonly Button _save = IconButton("", "StatsSaveAs");
    private int _year;
    private ListeningStats? _stats;
    private Color _seed = Brand;
    private Color _ink = Microsoft.UI.Colors.White;
    private bool _busy;

    public YearRecapPage()
    {
        InitializeComponent();
        _root.Children.Add(_stage);
        _surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _surface.Children.Add(_cards);
        Grid.SetRow(_pips, 1);
        _surface.Children.Add(_pips);
        _share.Click += async (_, _) => await ShareAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _buttons.Children.Add(_share);
        _buttons.Children.Add(_save);
        Grid.SetRow(_buttons, 2);
        _surface.Children.Add(_buttons);
        _root.Children.Add(_surface);
        Content = _root;

        _cards.SelectionChanged += (_, _) =>
        {
            if (_cards.SelectedIndex >= 0 && _pips.SelectedPageIndex != _cards.SelectedIndex) _pips.SelectedPageIndex = _cards.SelectedIndex;
        };
        _pips.SelectedIndexChanged += (_, _) =>
        {
            if (_pips.SelectedPageIndex >= 0 && _cards.SelectedIndex != _pips.SelectedPageIndex) _cards.SelectedIndex = _pips.SelectedPageIndex;
        };
        AutomationProperties.SetName(_pips, Loc.Get("StatsRecap"));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _year = int.TryParse(Navigator.Resolve(e.Parameter) as string, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : StatsTexts.Today.Year;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var period = new StatsPeriod(StatsPeriodKind.Year, new DateOnly(_year, 1, 1));
        // Итоги года — со всех устройств аккаунта
        var stats = await Task.Run(() => _library.Stats(period, TimeZoneInfo.Local));
        if (stats.TopTracks.FirstOrDefault() is { } top
            && await ArtworkColors.MainColorAsync(top.Track.VideoId, Thumbnails.Sized(top.Track.ThumbnailUrl, 544)) is { } color)
            _seed = color;
        _stats = stats;
        Paint();
        Fill(stats);
        _cards.Focus(FocusState.Programmatic);
#if DEBUG
        // Только отладочная сборка: MELOGOLD_RECAP_PNG=<файл> — картинка для «Поделиться» без окна «Поделиться» (проверка вида)
        if (Environment.GetEnvironmentVariable("MELOGOLD_RECAP_PNG") is { Length: > 0 } debugPath && await PictureAsync() is { } picture)
            await picture.CopyAsync(await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(debugPath)!), Path.GetFileName(debugPath), NameCollisionOption.ReplaceExisting);
#endif
    }

    /// <summary>Фон — цвет обложки трека года, книзу темнее; текст — чёрный или белый, какой читается.</summary>
    private void Paint()
    {
        _ink = Luminance(Lerp(_seed, Darkened(_seed), 0.5)) > 0.45 ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A) : Microsoft.UI.Colors.White;
        _surface.Background = Gradient(_seed);
        // Кнопки, точки страниц и стрелки листания — в тон тексту
        _surface.RequestedTheme = _ink == Microsoft.UI.Colors.White ? ElementTheme.Dark : ElementTheme.Light;
    }

    private void Fill(ListeningStats stats)
    {
        _cards.Items.Clear();
        var cards = new List<UIElement>();
        if (stats.IsEmpty)
        {
            cards.Add(Card(Text(Loc.Get("StatsRecapEmpty"), 24, FontWeights.SemiBold)));
        }
        else
        {
            cards.Add(MinutesCard(stats));
            cards.Add(TrackCard(stats.TopTracks[0]));
            if (stats.TopArtists.Count > 0)
                cards.Add(ListCard(Loc.Get("StatsTopArtists"), stats.TopArtists.Take(ListShown).Select(a => (a.ThumbnailUrl, true, a.Name, (string?)null, a.PlayTimeMs))));
            cards.Add(ListCard(Loc.Get("StatsTopTracks"), stats.TopTracks.Take(ListShown).Select(t => Shown(t) is var shown ? (t.Track.ThumbnailUrl, false, shown.Title, shown.ArtistsText, t.PlayTimeMs) : default)));
            cards.Add(FavoriteCard(stats));
            if (stats.Discoveries > 0) cards.Add(DiscoveriesCard(stats));
        }
        for (var i = 0; i < cards.Count; i++)
        {
            AutomationProperties.SetName(cards[i], Loc.Format("StatsRecapPageFormat", i + 1, cards.Count));
            _cards.Items.Add(cards[i]);
        }
        _pips.NumberOfPages = cards.Count;
        _pips.Visibility = cards.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _buttons.Visibility = stats.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        _cards.SelectedIndex = 0;
    }

    private static Track Shown(StatsTrack entry) => RowVm.Display?.Invoke(entry.Track) ?? entry.Track;

    // ---------- Карточки ----------

    private UIElement Card(params UIElement[] children)
    {
        var content = new StackPanel { Spacing = 8, MaxWidth = 440, Padding = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var title = Text(Loc.Format("StatsRecapTitleFormat", _year), 14, FontWeights.SemiBold, secondary: true);
        title.Margin = new Thickness(0, 0, 0, 8);
        content.Children.Add(title);
        foreach (var child in children) content.Children.Add(child);
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled };
    }

    private UIElement MinutesCard(ListeningStats stats)
    {
        var minutes = stats.PlayTimeMs / 60_000;
        return Card(Text(StatsTexts.Number(minutes), 72, FontWeights.Bold), Text(Loc.Plural("StatsRecapMinutes", minutes), 20, FontWeights.SemiBold));
    }

    private UIElement TrackCard(StatsTrack top)
    {
        var shown = Shown(top);
        var cover = StatsUi.Cover(Thumbnails.Sized(top.Track.ThumbnailUrl, 544), 240, round: false);
        cover.HorizontalAlignment = HorizontalAlignment.Center;
        cover.Margin = new Thickness(0, 8, 0, 16);
        return Card(
            cover,
            Text(Loc.Get("StatsRecapTrack"), 16, FontWeights.SemiBold, secondary: true),
            Text(shown.Title, 28, FontWeights.Bold),
            Text(shown.ArtistsText ?? "", 18, FontWeights.Normal),
            Text($"{AllTracksPage.ListeningTime(top.PlayTimeMs)} · {Loc.Plural("StatsPlaysCount", top.Plays)}", 14, FontWeights.Normal, secondary: true));
    }

    private UIElement ListCard(string title, IEnumerable<(string? Image, bool Round, string Title, string? Subtitle, long Ms)> rows) =>
        Card(Text(title, 24, FontWeights.Bold), Rows(rows));

    /// <summary>Место, обложка, название и время — строки топа на карточке.</summary>
    private StackPanel Rows(IEnumerable<(string? Image, bool Round, string Title, string? Subtitle, long Ms)> rows)
    {
        var list = new StackPanel { Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        var place = 0;
        foreach (var (image, round, name, subtitle, ms) in rows)
        {
            place++;
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var number = Text(place.ToString(CultureInfo.CurrentCulture), 16, FontWeights.Bold);
            number.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(number);
            var cover = StatsUi.Cover(image, 48, round);
            Grid.SetColumn(cover, 1);
            row.Children.Add(cover);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Line(name, 16, FontWeights.SemiBold, secondary: false));
            if (!string.IsNullOrEmpty(subtitle)) text.Children.Add(Line(subtitle, 13, FontWeights.Normal, secondary: true));
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            var time = Line(AllTracksPage.ListeningTime(ms), 13, FontWeights.SemiBold, secondary: true);
            time.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(time, 3);
            row.Children.Add(time);
            list.Children.Add(row);
        }
        return list;
    }

    private UIElement FavoriteCard(ListeningStats stats)
    {
        var parts = new List<UIElement>();
        if (stats.BestBucket is { } month)
        {
            parts.Add(Text(Loc.Get("StatsRecapMonth"), 16, FontWeights.SemiBold, secondary: true));
            parts.Add(Text(StatsTexts.Capitalize(StatsTexts.MonthName(month + 1)), 40, FontWeights.Bold));
            parts.Add(Text(AllTracksPage.ListeningTime(stats.Buckets[month]), 16, FontWeights.Normal));
        }
        if (stats.FavoriteDayPart is { } part)
        {
            var label = Text(Loc.Get("StatsRecapTime"), 16, FontWeights.SemiBold, secondary: true);
            label.Margin = new Thickness(0, 24, 0, 0);
            parts.Add(label);
            parts.Add(Text(StatsTexts.DayPartName(part), 40, FontWeights.Bold));
            if (stats.BestHour is { } hour) parts.Add(Text(Loc.Format("StatsRecapPeakFormat", StatsTexts.HourName(hour)), 16, FontWeights.Normal));
        }
        return Card([.. parts]);
    }

    private UIElement DiscoveriesCard(ListeningStats stats)
    {
        var rows = stats.TopDiscoveries.Select(t => Shown(t) is var shown ? (t.Track.ThumbnailUrl, false, shown.Title, shown.ArtistsText, t.PlayTimeMs) : default);
        return Card(
            Text(Loc.Get("StatsRecapDiscoveries"), 16, FontWeights.SemiBold, secondary: true),
            Text(StatsTexts.Number(stats.Discoveries), 72, FontWeights.Bold),
            Text(Loc.Plural("StatsRecapNewTracks", stats.Discoveries), 20, FontWeights.SemiBold),
            Rows(rows));
    }

    private TextBlock Text(string text, double size, Windows.UI.Text.FontWeight weight, bool secondary = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        MaxLines = 3,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = new SolidColorBrush(secondary ? WithAlpha(_ink, 0xC0) : _ink),
    };

    private TextBlock Line(string text, double size, Windows.UI.Text.FontWeight weight, bool secondary) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = new SolidColorBrush(secondary ? WithAlpha(_ink, 0xC0) : _ink),
    };

    // ---------- Картинка для «Поделиться» ----------

    private async Task ShareAsync()
    {
        if (await PictureAsync() is not { } file) return;
        Share.File(Loc.Format("StatsShareTextFormat", _year), file, () => _ = SaveAsync(file));
    }

    private async Task SaveAsync(StorageFile? ready = null)
    {
        if (App.Current?.Window is not { } window) return;
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            SuggestedFileName = Loc.Format("StatsRecapTitleFormat", _year),
        };
        picker.FileTypeChoices.Add("PNG", [".png"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (await picker.PickSaveFileAsync() is not { } target) return;
        if ((ready ?? await PictureAsync()) is not { } file) return;
        try
        {
            await file.CopyAndReplaceAsync(target);
            window.Snackbar.Show(Loc.Get("StatsPictureSaved"), Loc.Get("SaveFileOpen"), () => _ = Windows.System.Launcher.LaunchFileAsync(target));
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("Year recap picture was not saved", e);
            window.Snackbar.Show(Loc.Get("StatsPictureFailed"));
        }
    }

    /// <summary>
    /// Картинка 1080×1920 PNG во временной папке: рисуется под карточками (в дереве, но не видна) — вёрстка в точках
    /// картинки внутри <see cref="Viewbox"/> размером 1080 пикселей экрана, поэтому текст чёткий при любом масштабе.
    /// </summary>
    private async Task<StorageFile?> PictureAsync()
    {
        if (_busy || _stats is not { IsEmpty: false } stats) return null;
        _busy = true;
        _share.IsEnabled = _save.IsEnabled = false;
        try
        {
            var cover = await DecodedAsync(stats.TopTracks[0].Track.ThumbnailUrl);
            var scale = XamlRoot?.RasterizationScale ?? 1;
            var picture = new Viewbox { Width = PictureWidth / scale, Height = PictureHeight / scale, Stretch = Stretch.Fill, Child = PictureContent(stats, cover) };
            _stage.Children.Add(picture);
            picture.UpdateLayout();
            await NextFrameAsync();
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(picture, PictureWidth, PictureHeight);
            var pixels = await bitmap.GetPixelsAsync();
            var folder = Path.Combine(Path.GetTempPath(), "Melogold");
            Directory.CreateDirectory(folder);
            var file = await (await StorageFolder.GetFolderFromPathAsync(folder)).CreateFileAsync($"melogold-{_year}.png", CreationCollisionOption.ReplaceExisting);
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
                // Размер картинки — ровно 1080×1920, каким бы ни был масштаб экрана (RenderAsync отдаёт кратно ему)
                encoder.BitmapTransform.ScaledWidth = PictureWidth;
                encoder.BitmapTransform.ScaledHeight = PictureHeight;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                await encoder.FlushAsync();
            }
            Log.Info($"Year recap picture {bitmap.PixelWidth}x{bitmap.PixelHeight}");
            return file;
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("Year recap picture failed", e);
            App.Current?.Window?.Snackbar.Show(Loc.Get("StatsPictureFailed"));
            return null;
        }
        finally
        {
            _stage.Children.Clear();
            _busy = false;
            _share.IsEnabled = _save.IsEnabled = true;
        }
    }

    /// <summary>Как у Android: надпись, обложка трека года с названием, пять исполнителей года и минуты — в точках картинки.</summary>
    private Grid PictureContent(ListeningStats stats, ImageSource? cover)
    {
        var grid = new Grid { Width = PictureWidth, Height = PictureHeight, Background = Gradient(_seed), Padding = new Thickness(96, 100, 96, 100) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        top.Children.Add(Text(Loc.Format("StatsShareWatermarkFormat", _year), 52, FontWeights.Bold));
        var art = new Border
        {
            Width = 560,
            Height = 560,
            CornerRadius = new CornerRadius(56),
            Margin = new Thickness(0, 48, 0, 40),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(WithAlpha(_ink, 0x1F)),
            Child = cover is null ? null : new Image { Source = cover, Stretch = Stretch.UniformToFill },
        };
        top.Children.Add(art);
        var track = Shown(stats.TopTracks[0]);
        top.Children.Add(Text(Loc.Get("StatsRecapTrack"), 40, FontWeights.Medium, secondary: true));
        var title = Text(track.Title, 76, FontWeights.Bold);
        title.MaxLines = 2;
        title.LineHeight = 84;
        top.Children.Add(title);
        if (!string.IsNullOrEmpty(track.ArtistsText))
        {
            var artist = Text(track.ArtistsText, 48, FontWeights.Normal);
            artist.MaxLines = 1;
            top.Children.Add(artist);
        }
        if (stats.TopArtists.Count > 0)
        {
            var heading = Text(Loc.Get("StatsTopArtists"), 40, FontWeights.Medium, secondary: true);
            heading.Margin = new Thickness(0, 48, 0, 12);
            top.Children.Add(heading);
            for (var i = 0; i < Math.Min(ListShown, stats.TopArtists.Count); i++)
            {
                var line = Text($"{i + 1}  {stats.TopArtists[i].Name}", 52, FontWeights.SemiBold);
                line.MaxLines = 1;
                line.Margin = new Thickness(0, 6, 0, 6);
                top.Children.Add(line);
            }
        }
        grid.Children.Add(top);
        var bottom = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var minutes = stats.PlayTimeMs / 60_000;
        bottom.Children.Add(Text(StatsTexts.Number(minutes), 128, FontWeights.Bold));
        bottom.Children.Add(Text(Loc.Plural("StatsRecapMinutes", minutes), 48, FontWeights.Medium));
        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);
        return grid;
    }

    /// <summary>Обложка трека года, раскодированная заранее: картинка рисуется один раз, без догрузки.</summary>
    private static async Task<ImageSource?> DecodedAsync(string? url)
    {
        if (Thumbnails.Sized(url, 544) is not { } sized || !Uri.TryCreate(sized, UriKind.Absolute, out var uri) || Images.Cache is not { } cache) return null;
        try
        {
            if (await cache.GetAsync(uri) is not { } path) return null;
            using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            return source;
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or ArgumentException or HttpRequestException)
        {
            Log.Warn("Year recap cover failed", e);
            return null;
        }
    }

    private static Task NextFrameAsync()
    {
        var done = new TaskCompletionSource();
        void OnRendering(object? sender, object e)
        {
            CompositionTarget.Rendering -= OnRendering;
            done.TrySetResult();
        }
        CompositionTarget.Rendering += OnRendering;
        return done.Task;
    }

    // ---------- Цвета ----------

    private static LinearGradientBrush Gradient(Color seed)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = seed, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Darkened(seed), Offset = 1 });
        return brush;
    }

    private static Color Darkened(Color color) => Lerp(color, Microsoft.UI.Colors.Black, Darken);

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>Относительная яркость sRGB (WCAG).</summary>
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static Button IconButton(string glyph, string key)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        content.Children.Add(new TextBlock { Text = Loc.Get(key) });
        return new Button { Content = content };
    }
}
