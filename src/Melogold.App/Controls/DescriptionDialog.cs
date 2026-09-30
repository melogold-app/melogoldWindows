using Melogold.App.Services;
using Melogold.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>
/// Описание альбома или исполнителя целиком — отдельным окном вместо раскрытия в шапке: название, строка «Альбом · Исполнитель
/// · Год», обложка, текст (выделяется и прокручивается) и ссылка на статью Википедии, откуда YouTube Music его взял.
/// Описание приходит готовым из YouTube Music; клиент ничего не разбирает в самой Википедии (<see cref="DescriptionText"/>).
/// </summary>
public static class DescriptionDialog
{
    public static async Task ShowAsync(XamlRoot root, string title, string? subtitle, string? details, string? imageUrl, bool round, string body, DescriptionSource? source)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var header = new Grid { ColumnSpacing = 20 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cover = new Border
        {
            Width = 140,
            Height = 140,
            CornerRadius = new CornerRadius(round ? 70 : 8),
            VerticalAlignment = VerticalAlignment.Top,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = new Image { Source = Images.From(imageUrl, 280), Stretch = Stretch.UniformToFill },
        };
        header.Children.Add(cover);
        var names = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(subtitle)) names.Children.Add(new TextBlock { Text = subtitle, Foreground = secondary, TextWrapping = TextWrapping.WrapWholeWords });
        if (!string.IsNullOrEmpty(details)) names.Children.Add(new TextBlock { Text = details, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = secondary });
        Grid.SetColumn(names, 1);
        header.Children.Add(names);

        var content = new Grid { RowSpacing = 16, Width = 640 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(header);
        var text = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, LineHeight = 22 };
        var scroller = new ScrollViewer
        {
            Content = text,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 16, 0),
        };
        Grid.SetRow(scroller, 1);
        content.Children.Add(scroller);
        if (source is not null)
        {
            var links = SourceLinks(source, secondary);
            Grid.SetRow(links, 2);
            content.Children.Add(links);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = content,
            CloseButtonText = Loc.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        // Широкое окно: по умолчанию ContentDialog уже, чем нужно тексту с обложкой
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, title);
        await dialog.ShowAsync();
    }

    /// <summary>«Источник: Википедия · Лицензия: Creative Commons …» — обе части ссылками, если адрес можно открыть.</summary>
    private static StackPanel SourceLinks(DescriptionSource source, Brush secondary)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new TextBlock { Text = Loc.Get("DescriptionSource"), Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Link(Loc.Get("DescriptionWikipedia"), source.ArticleUrl, DescriptionText.IsWikipedia));
        if (source.License is { } license)
        {
            row.Children.Add(new TextBlock { Text = "·", Foreground = secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
            row.Children.Add(new TextBlock { Text = Loc.Get("DescriptionLicense"), Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(source.LicenseUrl is { } url ? Link(license, url, DescriptionText.IsLicense)
                : new TextBlock { Text = license, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
        }
        return row;
    }

    private static HyperlinkButton Link(string text, string url, Func<string, bool> allowed)
    {
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(4, 2, 4, 2) };
        // Адрес пришёл из сети: открывается только своя вики или лицензия, иначе — просто текст
        if (allowed(url)) link.NavigateUri = new Uri(url);
        else link.IsEnabled = false;
        return link;
    }
}
