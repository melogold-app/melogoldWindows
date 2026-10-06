using Melogold.App.Services;
using Melogold.Core.Domain;
using Melogold.Core.Music;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>
/// Лист «Об исполнителе» (tasks/0024) — системный диалог: фото во всю ширину, имя, слушатели в месяц, подписчики,
/// просмотры, описание целиком и ссылка на Википедию, откуда YouTube Music его взял. Только то, что есть в данных YouTube
/// Music: откуда исполнитель, дата рождения, жанр там не приходят — их нет и пустыми строками. Esc закрывает.
/// </summary>
public static class ArtistAboutDialog
{
    private const double Width = 560;

    public static async Task ShowAsync(XamlRoot root, ArtistDetails artist)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var content = new StackPanel { Spacing = 12, Width = Width };

        if (!string.IsNullOrEmpty(artist.ThumbnailUrl))
        {
            var aspect = Thumbnails.Aspect(artist.ThumbnailUrl) ?? 1;
            var wide = aspect >= 1.5;
            content.Children.Add(new Border
            {
                Height = wide ? Math.Round(Width / aspect) : Width / 2,
                CornerRadius = new CornerRadius(8),
                Background = new ImageBrush
                {
                    ImageSource = Images.From(wide ? Thumbnails.Wide(artist.ThumbnailUrl, 1080) : Thumbnails.Sized(artist.ThumbnailUrl, 1080)),
                    Stretch = Stretch.UniformToFill,
                    AlignmentY = wide ? AlignmentY.Center : AlignmentY.Top,
                },
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        var name = new TextBlock { Text = artist.Name, Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], TextWrapping = TextWrapping.WrapWholeWords };
        AutomationProperties.SetHeadingLevel(name, AutomationHeadingLevel.Level1);
        content.Children.Add(name);

        var facts = new StackPanel { Spacing = 2 };
        foreach (var fact in Facts(artist))
            facts.Children.Add(new TextBlock { Text = fact, Foreground = secondary, TextWrapping = TextWrapping.WrapWholeWords, IsTextSelectionEnabled = true });
        if (facts.Children.Count > 0) content.Children.Add(facts);

        var (body, source) = DescriptionText.Split(artist.Description);
        if (body.Length > 0)
            content.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, LineHeight = 22, Margin = new Thickness(0, 4, 0, 0) });
        if (source is not null) content.Children.Add(DescriptionDialog.SourceLinks(source, secondary));

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = content,
            CloseButtonText = Loc.Get("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.Resources["ContentDialogMaxWidth"] = Width + 48;
        AutomationProperties.SetName(dialog, Loc.Get("ArtistAbout") + ": " + artist.Name);
        await dialog.ShowAsync();
    }

    /// <summary>Строки листа по порядку; пустых нет.</summary>
    public static IEnumerable<string> Facts(ArtistDetails artist)
    {
        if (!string.IsNullOrWhiteSpace(artist.MonthlyListenersText)) yield return artist.MonthlyListenersText;
        if (!string.IsNullOrWhiteSpace(artist.SubscriberCount)) yield return Loc.Format("ArtistSubscribersFormat", artist.SubscriberCount);
        if (!string.IsNullOrWhiteSpace(artist.ViewsText)) yield return artist.ViewsText;
    }
}
